using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_EXTRACT_ROUTE — produce a "thin extract" of the active drawing as JSON
    /// matching the contract in <c>pipe_engine/io_schema.py</c> (schema_version "1").
    ///
    /// Reads optional filter from <c>%TEMP%\claude_bridge\extract_route_input.json</c>:
    /// {
    ///   "block_pattern": "СО_*",                  // wildcard, AutoCAD-style; default "СО_*"
    ///   "include_anonymous_resolved": true,       // resolve dynamic block *U37 → real name
    ///   "polyline_layer_pattern": "*",            // wildcard, default "*" (all polylines)
    ///   "drawing_units_assumed": "m"              // recorded as metadata for the consumer
    /// }
    ///
    /// Writes <c>%TEMP%\claude_bridge\extract_route.json</c>:
    /// {
    ///   "schema_version": "1",
    ///   "source": { dwg_path, extracted_at_utc, extractor_version, insunits_raw,
    ///               drawing_units_assumed, extents_min, extents_max },
    ///   "polylines": [ { handle, layer, length, start, end, is_closed, vertex_count }, ... ],
    ///   "blocks":    [ { handle, block_name_raw, block_name_effective, layer, position,
    ///                   rotation_rad, scale, attributes }, ... ],
    ///   "block_filter": { include_pattern, include_anonymous_resolved,
    ///                     matched_count, skipped_count }
    /// }
    ///
    /// Read-only — never modifies the drawing.
    /// </summary>
    public sealed class ExtractRouteCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string InputFileName    = "extract_route_input.json";
        private const string OutputFileName   = "extract_route.json";
        private const string SchemaVersion    = "1";

        [CommandMethod("CLAUDE_EXTRACT_ROUTE")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null) return;

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, InputFileName);
            var outputPath = Path.Combine(bridgeDir, OutputFileName);

            ExtractFilter filter;
            try { filter = ReadFilter(inputPath); }
            catch (System.Exception ex)
            {
                WriteError(outputPath, $"failed to read filter ({inputPath}): {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_EXTRACT_ROUTE: bad filter — {ex.Message}");
                return;
            }

            try
            {
                var database = document.Database;
                var polylines = new List<object>();
                var blocks    = new List<object>();
                int matchedBlocks = 0;
                int skippedBlocks = 0;

                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)transaction.GetObject(
                        database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)transaction.GetObject(
                        blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId entityId in modelSpace)
                    {
                        var ent = transaction.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                        if (ent == null) continue;

                        // ---- Polylines (LWPolyline + Polyline2d + Polyline3d) ----
                        if (ent is Polyline lwp)
                        {
                            if (filter.PolylineLayerMatches(lwp.Layer))
                                polylines.Add(BuildLwPolylineRecord(lwp));
                            continue;
                        }
                        if (ent is Polyline3d p3d)
                        {
                            if (filter.PolylineLayerMatches(p3d.Layer))
                                polylines.Add(BuildPolyline3dRecord(p3d, transaction));
                            continue;
                        }
                        if (ent is Polyline2d p2d)
                        {
                            if (filter.PolylineLayerMatches(p2d.Layer))
                                polylines.Add(BuildPolyline2dRecord(p2d, transaction));
                            continue;
                        }

                        // ---- Block references ----
                        if (ent is BlockReference br)
                        {
                            string rawName = br.Name ?? string.Empty;
                            string effName = ResolveEffectiveName(br, transaction, filter.IncludeAnonymousResolved);
                            if (filter.BlockNameMatches(effName))
                            {
                                blocks.Add(BuildBlockRecord(br, rawName, effName, transaction));
                                matchedBlocks++;
                            }
                            else
                            {
                                skippedBlocks++;
                            }
                        }
                    }

                    transaction.Commit();
                }

                var (extMin, extMax) = TryGetExtents(database);
                int insunits = SafeGetInsunits();

                var doc = new
                {
                    schema_version = SchemaVersion,
                    source = new
                    {
                        dwg_path = database.Filename ?? string.Empty,
                        extracted_at_utc = DateTime.UtcNow.ToString("O"),
                        extractor_version = AssemblyVersion(),
                        insunits_raw = insunits,
                        drawing_units_assumed = filter.DrawingUnitsAssumed,
                        extents_min = extMin,
                        extents_max = extMax,
                    },
                    polylines = polylines,
                    blocks = blocks,
                    block_filter = new
                    {
                        include_pattern = filter.BlockPattern,
                        include_anonymous_resolved = filter.IncludeAnonymousResolved,
                        matched_count = matchedBlocks,
                        skipped_count = skippedBlocks,
                    },
                    error = (string?)null,
                };

                File.WriteAllText(
                    outputPath,
                    JsonConvert.SerializeObject(doc, Formatting.Indented));

                editor.WriteMessage(
                    $"\nCLAUDE_EXTRACT_ROUTE: polylines={polylines.Count}, " +
                    $"blocks_matched={matchedBlocks}, skipped={skippedBlocks}");
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, $"extract failed: {ex.GetType().Name}: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_EXTRACT_ROUTE failed: {ex.Message}");
            }
        }

        // ---- filter -----------------------------------------------------------

        private sealed class ExtractFilter
        {
            public string  BlockPattern              { get; init; } = "СО_*";
            public string  PolylineLayerPattern      { get; init; } = "*";
            public bool    IncludeAnonymousResolved  { get; init; } = true;
            public string  DrawingUnitsAssumed       { get; init; } = "m";

            private Regex? _blockRe;
            private Regex? _polylineLayerRe;

            public bool BlockNameMatches(string name)
            {
                _blockRe ??= CompileWildcard(BlockPattern);
                return _blockRe.IsMatch(name);
            }

            public bool PolylineLayerMatches(string layer)
            {
                _polylineLayerRe ??= CompileWildcard(PolylineLayerPattern);
                return _polylineLayerRe.IsMatch(layer);
            }

            private static Regex CompileWildcard(string pattern)
            {
                // AutoCAD-style wildcard: '*' matches any sequence, '?' matches one char.
                var sb = new System.Text.StringBuilder();
                sb.Append('^');
                foreach (char c in pattern)
                {
                    switch (c)
                    {
                        case '*': sb.Append(".*"); break;
                        case '?': sb.Append('.');  break;
                        default:  sb.Append(Regex.Escape(c.ToString())); break;
                    }
                }
                sb.Append('$');
                return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
            }
        }

        private static ExtractFilter ReadFilter(string path)
        {
            if (!File.Exists(path)) return new ExtractFilter();
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw)) return new ExtractFilter();

            var root = JObject.Parse(raw);
            var blockPattern = TryGetString(root, "block_pattern") ?? "СО_*";
            var layerPattern = TryGetString(root, "polyline_layer_pattern") ?? "*";
            var units = TryGetString(root, "drawing_units_assumed") ?? "m";
            bool resolveAnon = true;
            if (root["include_anonymous_resolved"] is JToken t && t.Type == JTokenType.Boolean)
                resolveAnon = t.Value<bool>();

            return new ExtractFilter
            {
                BlockPattern = blockPattern,
                PolylineLayerPattern = layerPattern,
                IncludeAnonymousResolved = resolveAnon,
                DrawingUnitsAssumed = units,
            };
        }

        private static string? TryGetString(JObject obj, string name)
        {
            var v = obj[name];
            if (v == null || v.Type != JTokenType.String) return null;
            var s = v.Value<string>();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // ---- polyline records ------------------------------------------------

        private static object BuildLwPolylineRecord(Polyline lwp)
        {
            double length;
            try { length = lwp.Length; }
            catch { length = SafeCurveLength(lwp); }

            return new
            {
                handle = lwp.Handle.ToString(),
                layer = lwp.Layer,
                length = length,
                start = FormatPoint(lwp.StartPoint),
                end   = FormatPoint(lwp.EndPoint),
                is_closed = lwp.Closed,
                vertex_count = lwp.NumberOfVertices,
            };
        }

        private static object BuildPolyline3dRecord(Polyline3d p3d, Transaction tr)
        {
            // No direct .Length property — use Curve API.
            double length = SafeCurveLength(p3d);
            int vertexCount = 0;
            try
            {
                foreach (ObjectId vid in p3d)
                {
                    var v = tr.GetObject(vid, OpenMode.ForRead, false);
                    if (v != null) vertexCount++;
                }
            }
            catch { /* ignore — leave as 0 */ }

            return new
            {
                handle = p3d.Handle.ToString(),
                layer = p3d.Layer,
                length = length,
                start = FormatPoint(p3d.StartPoint),
                end   = FormatPoint(p3d.EndPoint),
                is_closed = p3d.Closed,
                vertex_count = vertexCount,
            };
        }

        private static object BuildPolyline2dRecord(Polyline2d p2d, Transaction tr)
        {
            double length = SafeCurveLength(p2d);
            int vertexCount = 0;
            try
            {
                foreach (ObjectId vid in p2d)
                {
                    var v = tr.GetObject(vid, OpenMode.ForRead, false);
                    if (v != null) vertexCount++;
                }
            }
            catch { /* ignore */ }

            return new
            {
                handle = p2d.Handle.ToString(),
                layer = p2d.Layer,
                length = length,
                start = FormatPoint(p2d.StartPoint),
                end   = FormatPoint(p2d.EndPoint),
                is_closed = p2d.Closed,
                vertex_count = vertexCount,
            };
        }

        private static double SafeCurveLength(Curve curve)
        {
            try { return curve.GetDistanceAtParameter(curve.EndParam); }
            catch { return 0.0; }
        }

        // ---- block records ---------------------------------------------------

        private static string ResolveEffectiveName(BlockReference br, Transaction tr, bool resolve)
        {
            if (!resolve) return br.Name ?? string.Empty;
            try
            {
                if (br.IsDynamicBlock)
                {
                    var dyn = (BlockTableRecord)tr.GetObject(
                        br.DynamicBlockTableRecord, OpenMode.ForRead);
                    return dyn.Name ?? br.Name ?? string.Empty;
                }
                // Resolve anonymous *U-named non-dynamic blocks (some legacy palettes)
                var raw = br.Name ?? string.Empty;
                if (raw.StartsWith("*"))
                {
                    var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    return btr.Name ?? raw;
                }
                return raw;
            }
            catch
            {
                return br.Name ?? string.Empty;
            }
        }

        private static object BuildBlockRecord(
            BlockReference br, string rawName, string effName, Transaction tr)
        {
            var attrs = new Dictionary<string, string>();
            try
            {
                foreach (ObjectId attId in br.AttributeCollection)
                {
                    var att = tr.GetObject(attId, OpenMode.ForRead, false) as AttributeReference;
                    if (att == null) continue;
                    var tag = att.Tag ?? string.Empty;
                    var val = att.TextString ?? string.Empty;
                    if (!string.IsNullOrEmpty(tag))
                        attrs[tag] = val;
                }
            }
            catch { /* swallow — partial attributes better than failing the whole row */ }

            return new
            {
                handle = br.Handle.ToString(),
                block_name_raw = rawName,
                block_name_effective = effName,
                layer = br.Layer,
                position = FormatPoint(br.Position),
                rotation_rad = br.Rotation,
                scale = new[] { br.ScaleFactors.X, br.ScaleFactors.Y, br.ScaleFactors.Z },
                attributes = attrs,
            };
        }

        // ---- misc helpers ----------------------------------------------------

        private static double[] FormatPoint(Point3d p) => new[] { p.X, p.Y, p.Z };

        private static (double[], double[]) TryGetExtents(Database db)
        {
            try
            {
                var minP = db.Extmin;
                var maxP = db.Extmax;
                return (FormatPoint(minP), FormatPoint(maxP));
            }
            catch
            {
                return (new[] { 0.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 });
            }
        }

        private static int SafeGetInsunits()
        {
            try
            {
                var v = Application.GetSystemVariable("INSUNITS");
                return Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        private static string AssemblyVersion() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

        private static void WriteError(string outputPath, string message)
        {
            var payload = new
            {
                schema_version = SchemaVersion,
                source = (object?)null,
                polylines = Array.Empty<object>(),
                blocks = Array.Empty<object>(),
                block_filter = (object?)null,
                error = message,
            };
            try
            {
                File.WriteAllText(outputPath,
                    JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch { /* nothing to do */ }
        }
    }
}
