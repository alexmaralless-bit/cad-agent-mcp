using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_QUERY — read-only entity search in the active drawing's model space.
    /// Reads a JSON filter from <c>%TEMP%\claude_bridge\query_input.json</c>, iterates
    /// model-space entities, applies the filter, and writes matches to
    /// <c>%TEMP%\claude_bridge\query.json</c>.
    ///
    /// Filter shape (all fields optional, AND-semantics — MVP per HEALTH Q3):
    /// {
    ///   "type": "Line",            // exact match on entity .NET type name
    ///   "layer": "0",              // exact match on entity.Layer
    ///   "block_name": "MyBlock",   // only matches BlockReference entities with this Name
    ///   "max_results": 500         // truncate response after N matches (default 500)
    /// }
    ///
    /// Missing input file or invalid JSON → command writes an error envelope to query.json
    /// (still 200-equivalent at HTTP layer; consumers check the "error" field).
    /// </summary>
    public sealed class QueryCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string InputFileName    = "query_input.json";
        private const string OutputFileName   = "query.json";
        private const int    DefaultMaxResults = 500;

        [CommandMethod("CLAUDE_QUERY")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null) return;

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, InputFileName);
            var outputPath = Path.Combine(bridgeDir, OutputFileName);

            QueryFilter filter;
            try
            {
                filter = ReadFilter(inputPath);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, $"failed to read filter ({inputPath}): {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_QUERY: bad filter — {ex.Message}");
                return;
            }

            try
            {
                var database = document.Database;
                int examined = 0;
                int matched  = 0;
                bool truncated = false;
                var results = new List<object>();

                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)transaction.GetObject(
                        database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)transaction.GetObject(
                        blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId entityId in modelSpace)
                    {
                        examined++;
                        if (!(transaction.GetObject(entityId, OpenMode.ForRead, false) is Entity entity))
                        {
                            continue;
                        }

                        if (!filter.Matches(entity))
                        {
                            continue;
                        }

                        matched++;
                        if (results.Count >= filter.MaxResults)
                        {
                            truncated = true;
                            continue; // keep counting matches, just don't materialize them
                        }

                        results.Add(BuildEntityRecord(entity));
                    }

                    transaction.Commit();
                }

                var payload = new
                {
                    plugin = "ClaudeBridge.AutoCad",
                    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                    timestamp_utc = DateTime.UtcNow.ToString("O"),
                    filter = filter.Echo(),
                    total_examined = examined,
                    match_count = matched,
                    truncated = truncated,
                    results = results,
                    error = (string?)null
                };

                File.WriteAllText(
                    outputPath,
                    JsonConvert.SerializeObject(payload, Formatting.Indented));

                editor.WriteMessage(
                    $"\nCLAUDE_QUERY: examined={examined}, matched={matched}, returned={results.Count}, truncated={truncated}");
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, $"query failed: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_QUERY failed: {ex.Message}");
            }
        }

        // ---- filter ----------------------------------------------------------

        private sealed class QueryFilter
        {
            public string? Type      { get; init; }
            public string? Layer     { get; init; }
            public string? BlockName { get; init; }
            public int     MaxResults { get; init; } = DefaultMaxResults;

            public bool Matches(Entity entity)
            {
                if (Type != null && entity.GetType().Name != Type) return false;
                if (Layer != null && entity.Layer != Layer)        return false;
                if (BlockName != null)
                {
                    if (!(entity is BlockReference br)) return false;
                    if (br.Name != BlockName)           return false;
                }
                return true;
            }

            public object Echo() => new
            {
                type        = Type,
                layer       = Layer,
                block_name  = BlockName,
                max_results = MaxResults
            };
        }

        private static QueryFilter ReadFilter(string path)
        {
            if (!File.Exists(path))
            {
                // No input file → empty filter (match everything, capped at MaxResults).
                return new QueryFilter();
            }
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new QueryFilter();
            }

            var root = JObject.Parse(raw);
            string? type      = TryGetString(root, "type");
            string? layer     = TryGetString(root, "layer");
            string? blockName = TryGetString(root, "block_name");
            int maxResults    = DefaultMaxResults;
            if (root["max_results"] is JToken mr
                && mr.Type == JTokenType.Integer)
            {
                var n = mr.Value<int>();
                if (n > 0) maxResults = n;
            }
            return new QueryFilter
            {
                Type = type, Layer = layer, BlockName = blockName, MaxResults = maxResults
            };
        }

        private static string? TryGetString(JObject obj, string name)
        {
            var v = obj[name];
            if (v == null || v.Type != JTokenType.String) return null;
            var s = v.Value<string>();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // ---- entity record ---------------------------------------------------

        private static object BuildEntityRecord(Entity entity)
        {
            string? blockName = entity is BlockReference br ? br.Name : null;
            object? bounds    = TryGetBounds(entity);
            object? position  = TryGetPosition(entity);

            return new
            {
                handle = entity.Handle.ToString(),
                type = entity.GetType().Name,
                layer = entity.Layer,
                color_index = SafeColorIndex(entity),
                linetype = entity.Linetype,
                block_name = blockName,
                position = position,
                bounds = bounds
            };
        }

        private static object? TryGetBounds(Entity entity)
        {
            // GeometricExtents throws for entities with no extents (e.g. some degenerate
            // or invisible entities). Swallow and return null — the caller treats null
            // bounds as "unknown" rather than failing the whole query.
            try
            {
                var ext = entity.GeometricExtents;
                return new
                {
                    min = FormatPoint(ext.MinPoint),
                    max = FormatPoint(ext.MaxPoint)
                };
            }
            catch
            {
                return null;
            }
        }

        private static object? TryGetPosition(Entity entity) =>
            entity switch
            {
                BlockReference br => FormatPoint(br.Position),
                DBPoint        p  => FormatPoint(p.Position),
                _                 => null
            };

        private static int SafeColorIndex(Entity entity)
        {
            try { return (int)entity.Color.ColorIndex; }
            catch { return 0; }
        }

        private static object FormatPoint(Point3d p) => new { x = p.X, y = p.Y, z = p.Z };

        // ---- error envelope --------------------------------------------------

        private static void WriteError(string outputPath, string message)
        {
            var payload = new
            {
                plugin = "ClaudeBridge.AutoCad",
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                filter = (object?)null,
                total_examined = 0,
                match_count = 0,
                truncated = false,
                results = Array.Empty<object>(),
                error = message
            };
            try
            {
                File.WriteAllText(
                    outputPath,
                    JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch
            {
                // Last-resort: if even writing the error fails, the HTTP layer will time out.
            }
        }
    }
}
