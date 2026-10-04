using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_OVERLAY — view-only annotations on a dedicated layer (`CLAUDE_OVERLAY`).
    /// First piece of the phase-2 visual-overlay set from concept.md.
    ///
    /// Currently supports six ops:
    ///   highlight: draw a numbered red bounding-box around each handle in the
    ///              input list. Numbers run 1..N in input order, so the same
    ///              numbering can be referenced in chat ("which one — the 3rd?").
    ///   hud:       draw an informational corner-panel (doc name, extents, active
    ///              layer, top entity-types) anchored at top-left of the current view.
    ///              Yellow text. Caller should overlay_clear first if HUD already drawn.
    ///   grid:      draw a coordinate grid (cyan lines + axis-value labels) spanning
    ///              the current view. Step is either caller-supplied or auto-picked to
    ///              a "nice round" value (1/2/5 × 10^N) that yields ~15 cells across
    ///              the diagonal.
    ///   scale_bar: draw a 5-segment cartographic scale bar in a chosen corner of the
    ///              current view. Even segments are filled (Solid), odd segments outline.
    ///              Six numeric labels (0..length) anchor under the ticks. Length is
    ///              caller-supplied or auto-picked (1/2/5 × 10^N near 20% of view width).
    ///   markers:   draw small blue + crosses as POINT references (not bbox like
    ///              highlight). Optional 4 anchored corners (TL/TR/BL/BR) provide a
    ///              spatial-reference frame; optional list of handles pinpoints each
    ///              entity at its insertion-point (BlockReference / DBPoint / DBText
    ///              anchor, or centroid fallback). Numbered #1..#N like highlight.
    ///
    ///   Sizing/positioning for hud/grid/scale_bar/markers tracks the CURRENT view
    ///   (see GetOverlayFrame), NOT the whole-database GeometricExtents — so the
    ///   overlay stays proportional to the captured window even when far-away topo/
    ///   cadastral underlays bloat Extmin/Extmax. Falls back to extents if no view.
    ///   clear:     wipe every entity whose Layer == "CLAUDE_OVERLAY".
    ///
    /// All work is done through the managed Teigha API inside one transaction.
    /// Nothing on the user's geometry is touched — we only create / delete on
    /// our own layer. Therefore: NO backup, NO dry-run/confirm. Same "view-only"
    /// category rationale as CLAUDE_CAMERA.
    ///
    /// Input file: %TEMP%\claude_bridge\overlay_input.json
    ///   { "op": "highlight", "handles": ["19BC41", "19BC42", ...] }
    ///   { "op": "hud" }
    ///   { "op": "grid", "step": 5000 }  -- step optional; null = auto
    ///   { "op": "scale_bar", "length": 10000, "position": "bottom_left" }  -- both optional
    ///   { "op": "markers", "anchored": true, "handles": ["19BC41", ...] }  -- both optional
    ///   { "op": "clear" }
    ///
    /// Output file: %TEMP%\claude_bridge\overlay.json — op echo, counts of
    /// highlighted / cleared entities, list of handles that could not be
    /// resolved or had no extents, and an `error` field.
    /// </summary>
    public sealed class OverlayCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string InputFileName    = "overlay_input.json";
        private const string OutputFileName   = "overlay.json";
        private const string OverlayLayer     = "CLAUDE_OVERLAY";
        // Red. ColorIndex 1 is "red" in the AutoCAD/Teigha index palette and
        // pops against pretty much any background.
        private const short  OverlayColorIndex = 1;
        // Yellow for HUD text — keeps it visually distinct from red bbox highlights.
        private const short  HudColorIndex     = 2;
        // Cyan for grid lines and axis labels — distinct from red highlight and yellow HUD.
        private const short  GridColorIndex    = 4;
        // White for scale bar — neutral and contrasts on the dark AutoCAD background.
        private const short  ScaleBarColorIndex = 7;
        // Scale bar always has 5 segments (6 ticks) — standard cartographic convention.
        private const int    ScaleBarSegments = 5;
        // Blue for markers — fifth distinct overlay color
        // (red=highlight, yellow=hud, cyan=grid, white=scale_bar, blue=markers).
        private const short  MarkerColorIndex = 5;

        [CommandMethod("CLAUDE_OVERLAY")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null) return;

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, InputFileName);
            var outputPath = Path.Combine(bridgeDir, OutputFileName);

            OverlayInput input;
            try
            {
                input = ReadInput(inputPath);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, null, $"failed to read input: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_OVERLAY: bad input — {ex.Message}");
                return;
            }

            try
            {
                switch (input.Op)
                {
                    case "highlight":
                        DoHighlight(document, input.Handles, outputPath);
                        break;
                    case "hud":
                        DoHud(document, outputPath);
                        break;
                    case "grid":
                        DoGrid(document, input.Step, outputPath);
                        break;
                    case "scale_bar":
                        DoScaleBar(document, input.Length, input.Position, outputPath);
                        break;
                    case "markers":
                        DoMarkers(document, input.Anchored, input.Handles, outputPath);
                        break;
                    case "clear":
                        DoClear(document, outputPath);
                        break;
                    default:
                        WriteError(outputPath, input.Op, $"unknown op '{input.Op}'");
                        return;
                }
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, input.Op, $"{ex.GetType().Name}: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_OVERLAY failed: {ex.Message}");
            }
        }

        // ---- input ----------------------------------------------------------

        private sealed class OverlayInput
        {
            public string       Op       { get; init; } = "highlight";
            /// <summary>Entity handles. For op="highlight" → bbox targets; for op="markers" → dynamic point targets.</summary>
            public List<string> Handles  { get; init; } = new List<string>();
            /// <summary>Optional grid step in drawing units (op="grid"). Null = auto-pick.</summary>
            public double?      Step     { get; init; }
            /// <summary>Optional scale-bar length in drawing units (op="scale_bar"). Null = auto-pick.</summary>
            public double?      Length   { get; init; }
            /// <summary>Optional scale-bar corner (op="scale_bar"): bottom_left | bottom_right | top_left | top_right.</summary>
            public string?      Position { get; init; }
            /// <summary>op="markers": also draw the 4 reference markers at extents corners.</summary>
            public bool         Anchored { get; init; }
        }

        private static OverlayInput ReadInput(string path)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException("overlay_input.json not found");
            var raw = File.ReadAllText(path);
            var root = JObject.Parse(raw);

            var opTok = root["op"];
            var op = (opTok != null && opTok.Type == JTokenType.String)
                ? (opTok.Value<string>() ?? "highlight")
                : "highlight";

            var handles = new List<string>();
            if (root["handles"] is JArray hs)
            {
                foreach (var h in hs)
                {
                    if (h.Type == JTokenType.String)
                    {
                        var s = h.Value<string>();
                        if (!string.IsNullOrWhiteSpace(s)) handles.Add(s!);
                    }
                }
            }

            double? step = null;
            if (root["step"] is JToken st && (st.Type == JTokenType.Float || st.Type == JTokenType.Integer))
            {
                var sv = st.Value<double>();
                if (sv > 0) step = sv;
            }

            double? length = null;
            if (root["length"] is JToken ln && (ln.Type == JTokenType.Float || ln.Type == JTokenType.Integer))
            {
                var lv = ln.Value<double>();
                if (lv > 0) length = lv;
            }

            string? position = null;
            if (root["position"] is JToken ps && ps.Type == JTokenType.String)
            {
                var pv = ps.Value<string>();
                if (!string.IsNullOrWhiteSpace(pv)) position = pv;
            }

            bool anchored = false;
            if (root["anchored"] is JToken an && an.Type == JTokenType.Boolean)
            {
                anchored = an.Value<bool>();
            }

            return new OverlayInput
            {
                Op = op, Handles = handles, Step = step, Length = length,
                Position = position, Anchored = anchored,
            };
        }

        // ---- highlight ------------------------------------------------------

        private static void DoHighlight(Document document, List<string> handles, string outputPath)
        {
            var database = document.Database;
            var resolved = new List<object>();
            var skipped  = new List<object>();

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                var layerId = EnsureOverlayLayer(database, transaction);

                int index = 0;
                foreach (var handleStr in handles)
                {
                    index++;
                    ObjectId entId = ObjectId.Null;
                    try
                    {
                        var handle = new Handle(Convert.ToInt64(handleStr, 16));
                        entId = database.GetObjectId(false, handle, 0);
                    }
                    catch
                    {
                        skipped.Add(new { index = index, handle = handleStr, reason = "handle not resolvable" });
                        continue;
                    }

                    if (entId == ObjectId.Null || !(transaction.GetObject(entId, OpenMode.ForRead, false) is Entity entity))
                    {
                        skipped.Add(new { index = index, handle = handleStr, reason = "entity not found" });
                        continue;
                    }

                    Extents3d ext;
                    try
                    {
                        ext = entity.GeometricExtents;
                    }
                    catch
                    {
                        skipped.Add(new { index = index, handle = handleStr, reason = "no geometric extents" });
                        continue;
                    }
                    // NanoCAD/Teigha returns sentinel ~±1e20 for some entity types; AutoCAD throws — see GetExtentsSafely defense above.
                    if (Math.Abs(ext.MinPoint.X) > 1e19 || Math.Abs(ext.MaxPoint.X) > 1e19)
                    {
                        skipped.Add(new { index = index, handle = handleStr, reason = "sentinel extents (±1e20)" });
                        continue;
                    }

                    var rect = BuildBboxPolyline(ext, layerId);
                    modelSpace.AppendEntity(rect);
                    transaction.AddNewlyCreatedDBObject(rect, true);

                    var label = BuildLabelText(ext, index, layerId);
                    modelSpace.AppendEntity(label);
                    transaction.AddNewlyCreatedDBObject(label, true);

                    resolved.Add(new
                    {
                        index = index,
                        handle = handleStr,
                        bounds = new
                        {
                            min = new { x = ext.MinPoint.X, y = ext.MinPoint.Y, z = ext.MinPoint.Z },
                            max = new { x = ext.MaxPoint.X, y = ext.MaxPoint.Y, z = ext.MaxPoint.Z },
                        }
                    });
                }

                transaction.Commit();
            }

            WriteOk(outputPath, "highlight", new
            {
                highlighted_count = resolved.Count,
                skipped_count = skipped.Count,
                results = resolved,
                skipped = skipped,
            });
        }

        private static Polyline BuildBboxPolyline(Extents3d ext, ObjectId layerId)
        {
            var pl = new Polyline(4)
            {
                LayerId = layerId,
                Closed = true,
                Color = Color.FromColorIndex(ColorMethod.ByAci, OverlayColorIndex),
            };
            pl.AddVertexAt(0, new Point2d(ext.MinPoint.X, ext.MinPoint.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(ext.MaxPoint.X, ext.MinPoint.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(ext.MinPoint.X, ext.MaxPoint.Y), 0, 0, 0);
            return pl;
        }

        private static DBText BuildLabelText(Extents3d ext, int number, ObjectId layerId)
        {
            // Text size scales with bbox so labels stay legible at any zoom but
            // don't drown out tiny entities. 8% of the larger side, clamped.
            double sizeX = ext.MaxPoint.X - ext.MinPoint.X;
            double sizeY = ext.MaxPoint.Y - ext.MinPoint.Y;
            double height = Math.Max(0.5, Math.Min(Math.Max(sizeX, sizeY) * 0.08, 100.0));

            return new DBText
            {
                LayerId = layerId,
                Color = Color.FromColorIndex(ColorMethod.ByAci, OverlayColorIndex),
                Position = new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, 0),
                Height = height,
                TextString = "#" + number.ToString(CultureInfo.InvariantCulture),
            };
        }

        // ---- hud ------------------------------------------------------------

        private static void DoHud(Document document, string outputPath)
        {
            var database = document.Database;
            var lines = new List<string>();
            int linesDrawn = 0;
            object anchor;
            double textHeight;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                var layerId = EnsureOverlayLayer(database, transaction);

                // --- Sizing/anchor frame vs. reported extents ---
                // SIZE + POSITION track the current view (so the panel is readable and
                // on-screen in the snapshot even when far-away underlays bloat the DB
                // extents). The "ext:" CONTENT line still reports the true drawing
                // extents — that's the datum the HUD exists to surface.
                GetOverlayFrame(document, out var frameMin, out var frameMax, out _);
                double frameMaxSide = Math.Max(frameMax.X - frameMin.X, frameMax.Y - frameMin.Y);

                GetExtentsSafely(database, out var extMin, out var extMax, out bool sentinel);
                double width  = extMax.X - extMin.X;
                double height = extMax.Y - extMin.Y;

                // Text height: 2.5% of the frame max-side, with only a lower floor so a
                // 0.1-unit sketch still gets readable HUD. No upper cap.
                textHeight = Math.Max(0.5, frameMaxSide * 0.025);
                double lineSpacing = textHeight * 1.5;

                // --- Compose lines ---
                string docName = !string.IsNullOrEmpty(database.Filename)
                    ? Path.GetFileName(database.Filename)
                    : (document.Name ?? "<unsaved>");
                lines.Add("doc: " + docName + (sentinel ? "  (no extents)" : ""));

                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "ext: {0:0.##}x{1:0.##}  @ ({2:0.##}, {3:0.##})",
                    width, height, extMin.X, extMin.Y));

                var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                int totalLayers   = 0;
                int visibleLayers = 0;
                foreach (ObjectId lid in layerTable)
                {
                    if (transaction.GetObject(lid, OpenMode.ForRead, false) is LayerTableRecord ltr)
                    {
                        totalLayers++;
                        if (!ltr.IsOff && !ltr.IsFrozen) visibleLayers++;
                    }
                }
                string activeLayer = "<unknown>";
                if (transaction.GetObject(database.Clayer, OpenMode.ForRead, false) is LayerTableRecord active)
                {
                    activeLayer = active.Name;
                }
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "layer: {0}  ({1}/{2} visible)", activeLayer, visibleLayers, totalLayers));

                var counts = new Dictionary<string, int>();
                foreach (ObjectId eid in modelSpace)
                {
                    if (transaction.GetObject(eid, OpenMode.ForRead, false) is Entity e)
                    {
                        if (string.Equals(e.Layer, OverlayLayer, StringComparison.Ordinal)) continue;
                        var t = e.GetType().Name;
                        counts[t] = counts.TryGetValue(t, out var n) ? n + 1 : 1;
                    }
                }
                var top = counts.OrderByDescending(kv => kv.Value).Take(3).ToList();
                if (top.Count == 0)
                {
                    lines.Add("top: <no entities>");
                }
                else
                {
                    var parts = top.Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture));
                    lines.Add("top: " + string.Join(" ", parts));
                }

                // --- Draw lines top-down from the frame's top-left corner ---
                // DBText Position is the lower-left corner of the text, so the first
                // line sits with its baseline just below the frame top (i.e. inside it).
                double anchorX = frameMin.X;
                double anchorY = frameMax.Y - textHeight;
                anchor = new { x = anchorX, y = frameMax.Y };

                foreach (var text in lines)
                {
                    var dbt = new DBText
                    {
                        LayerId    = layerId,
                        Color      = Color.FromColorIndex(ColorMethod.ByAci, HudColorIndex),
                        Position   = new Point3d(anchorX, anchorY, 0),
                        Height     = textHeight,
                        TextString = text,
                    };
                    modelSpace.AppendEntity(dbt);
                    transaction.AddNewlyCreatedDBObject(dbt, true);
                    linesDrawn++;
                    anchorY -= lineSpacing;
                }

                transaction.Commit();
            }

            WriteOk(outputPath, "hud", new
            {
                rendered_lines = linesDrawn,
                text_height = textHeight,
                anchor = anchor,
                color_index = (int)HudColorIndex,
                hud_text = string.Join("\n", lines),
            });
        }

        // ---- grid -----------------------------------------------------------

        private static void DoGrid(Document document, double? requestedStep, string outputPath)
        {
            var database = document.Database;
            int vLines = 0, hLines = 0, labels = 0;
            double step;
            string frameSource;
            object anchor;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                var layerId = EnsureOverlayLayer(database, transaction);

                GetOverlayFrame(document, out var extMin, out var extMax, out frameSource);
                double width  = extMax.X - extMin.X;
                double height = extMax.Y - extMin.Y;
                double maxSide = Math.Max(width, height);
                double diagonal = Math.Sqrt(width * width + height * height);

                // Step: explicit caller value wins; otherwise auto-pick a "nice round"
                // value so the grid hits ~15 cells across the diagonal.
                step = requestedStep ?? AutoPickStep(diagonal);
                // Defensive: step shouldn't outsize the drawing or be so small it produces
                // tens of thousands of lines. Clamp to [maxSide/200, maxSide].
                if (step <= 0 || step > maxSide) step = AutoPickStep(diagonal);
                if (step < maxSide / 200.0) step = maxSide / 200.0;

                // Label height: smaller than HUD (1.5% of max-side). Floor 0.5.
                double labelHeight = Math.Max(0.5, maxSide * 0.015);
                double labelPadding = labelHeight * 0.5;

                var gridColor = Color.FromColorIndex(ColorMethod.ByAci, GridColorIndex);

                // Label-skip factors: when the expected text width / height is wider than
                // one grid step, labels would overlap each other. Skip every Nth line so
                // labels stay readable. The X-axis tends to need this on drawings whose
                // coordinates are large (UTM-style 7-digit easting/northing); the Y-axis
                // rarely does because labels are typeset horizontally.
                int xSampleDigits = EstimateLabelWidthChars(extMax.X, step);
                double xLabelWidth = xSampleDigits * labelHeight * 0.7;
                int xLabelSkip = Math.Max(1, (int)Math.Ceiling(xLabelWidth / step));
                // For Y: vertical spacing is `step`; label vertical extent is `labelHeight`.
                int yLabelSkip = Math.Max(1, (int)Math.Ceiling(labelHeight * 1.4 / step));

                // Vertical gridlines: x = ceil(extMin.X/step)*step, +step until <= extMax.X.
                double firstX = Math.Ceiling(extMin.X / step) * step;
                int xIndex = 0;
                for (double x = firstX; x <= extMax.X + 1e-9; x += step, xIndex++)
                {
                    var line = new Line(new Point3d(x, extMin.Y, 0), new Point3d(x, extMax.Y, 0))
                    {
                        LayerId = layerId,
                        Color   = gridColor,
                    };
                    modelSpace.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);
                    vLines++;

                    if (xIndex % xLabelSkip != 0) continue;
                    // X-axis value label: just above bottom edge, slightly right of the line.
                    var lbl = new DBText
                    {
                        LayerId    = layerId,
                        Color      = gridColor,
                        Position   = new Point3d(x + labelPadding, extMin.Y + labelPadding, 0),
                        Height     = labelHeight,
                        TextString = FormatGridValue(x, step),
                    };
                    modelSpace.AppendEntity(lbl);
                    transaction.AddNewlyCreatedDBObject(lbl, true);
                    labels++;
                }

                // Horizontal gridlines.
                double firstY = Math.Ceiling(extMin.Y / step) * step;
                int yIndex = 0;
                for (double y = firstY; y <= extMax.Y + 1e-9; y += step, yIndex++)
                {
                    var line = new Line(new Point3d(extMin.X, y, 0), new Point3d(extMax.X, y, 0))
                    {
                        LayerId = layerId,
                        Color   = gridColor,
                    };
                    modelSpace.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);
                    hLines++;

                    if (yIndex % yLabelSkip != 0) continue;
                    // Y-axis value label: just right of left edge, slightly above the line.
                    var lbl = new DBText
                    {
                        LayerId    = layerId,
                        Color      = gridColor,
                        Position   = new Point3d(extMin.X + labelPadding, y + labelPadding, 0),
                        Height     = labelHeight,
                        TextString = FormatGridValue(y, step),
                    };
                    modelSpace.AppendEntity(lbl);
                    transaction.AddNewlyCreatedDBObject(lbl, true);
                    labels++;
                }

                anchor = new
                {
                    extents_min = new { x = extMin.X, y = extMin.Y },
                    extents_max = new { x = extMax.X, y = extMax.Y },
                };

                transaction.Commit();
            }

            WriteOk(outputPath, "grid", new
            {
                step = step,
                step_auto = requestedStep == null,
                vertical_lines = vLines,
                horizontal_lines = hLines,
                labels = labels,
                frame_source = frameSource,
                anchor = anchor,
                color_index = (int)GridColorIndex,
            });
        }

        /// <summary>
        /// Pick a "nice round" grid step (1, 2, or 5 × power-of-ten) that produces
        /// ~15 cells across the extents diagonal. Keeps grid density readable across
        /// drawings of wildly different scales (a 0.5m sketch vs a 100km survey).
        /// </summary>
        private static double AutoPickStep(double diagonal)
        {
            if (diagonal <= 0) return 1.0;
            double target = diagonal / 15.0;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double mant = target / mag;
            // Snap mantissa to the closest of {1, 2, 5, 10} not greater than the target.
            double snap;
            if      (mant >= 5) snap = 5;
            else if (mant >= 2) snap = 2;
            else                snap = 1;
            return snap * mag;
        }

        /// <summary>
        /// Format an axis-tick value. Integer-step grids show integers; sub-unit-step
        /// grids show enough decimals to be unambiguous.
        /// </summary>
        private static string FormatGridValue(double v, double step)
        {
            if (step >= 1)   return v.ToString("0", CultureInfo.InvariantCulture);
            int decimals = Math.Min(6, (int)Math.Ceiling(-Math.Log10(step)));
            var fmt = "0." + new string('#', Math.Max(1, decimals));
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Rough estimate of how many characters the widest axis label will occupy —
        /// used to decide whether to skip every Nth label so they don't overlap.
        /// Returns at least 1 so callers can multiply by char-width.
        /// </summary>
        private static int EstimateLabelWidthChars(double maxAbs, double step)
        {
            string sample = FormatGridValue(maxAbs, step);
            return Math.Max(1, sample.Length);
        }

        // ---- scale_bar ------------------------------------------------------

        private static void DoScaleBar(
            Document document, double? requestedLength, string? requestedPosition, string outputPath)
        {
            var database = document.Database;
            double length;
            string position = NormalizePosition(requestedPosition);
            string frameSource;
            int linesDrawn = 0, fillsDrawn = 0, labelsDrawn = 0;
            object anchor;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                var layerId = EnsureOverlayLayer(database, transaction);

                GetOverlayFrame(document, out var extMin, out var extMax, out frameSource);
                double width  = extMax.X - extMin.X;
                double height = extMax.Y - extMin.Y;
                double maxSide = Math.Max(width, height);

                // Length: ~20% of width, snapped to nice round, unless caller specified.
                length = requestedLength ?? AutoPickScaleLength(width);
                if (length <= 0 || length > width * 0.8) length = AutoPickScaleLength(width);

                // Bar dimensions tied to label height. The vertical thickness of the bar
                // is small (1.5×label-height) — it's a footer, not a heading.
                double labelHeight = Math.Max(0.5, maxSide * 0.015);
                double barH = labelHeight * 1.5;
                double padding = maxSide * 0.02;
                double segmentLen = length / ScaleBarSegments;

                // Corner anchor (lower-left corner of the bar itself).
                double x0, y0;
                switch (position)
                {
                    case "bottom_right":
                        x0 = extMax.X - padding - length;
                        y0 = extMin.Y + padding;
                        break;
                    case "top_left":
                        x0 = extMin.X + padding;
                        y0 = extMax.Y - padding - barH - labelHeight * 1.5;
                        break;
                    case "top_right":
                        x0 = extMax.X - padding - length;
                        y0 = extMax.Y - padding - barH - labelHeight * 1.5;
                        break;
                    default: // bottom_left
                        x0 = extMin.X + padding;
                        y0 = extMin.Y + padding + labelHeight * 1.5; // leave room for labels below
                        break;
                }

                var color = Color.FromColorIndex(ColorMethod.ByAci, ScaleBarColorIndex);

                // Even-indexed segments filled with a Solid quad. Odd segments stay outline.
                for (int i = 0; i < ScaleBarSegments; i++)
                {
                    if (i % 2 != 0) continue;
                    double xa = x0 + i * segmentLen;
                    double xb = xa + segmentLen;
                    // Teigha Solid takes 4 corners in a specific order (CCW triangle pairs).
                    var fill = new Solid(
                        new Point3d(xa, y0,         0),
                        new Point3d(xb, y0,         0),
                        new Point3d(xa, y0 + barH,  0),
                        new Point3d(xb, y0 + barH,  0))
                    {
                        LayerId = layerId,
                        Color   = color,
                    };
                    modelSpace.AppendEntity(fill);
                    transaction.AddNewlyCreatedDBObject(fill, true);
                    fillsDrawn++;
                }

                // Outer rectangle (top, bottom, left, right) — drawn after fills so its
                // lines aren't covered. Single closed Polyline.
                var outline = new Polyline(4)
                {
                    LayerId = layerId,
                    Color   = color,
                    Closed  = true,
                };
                outline.AddVertexAt(0, new Point2d(x0,          y0),         0, 0, 0);
                outline.AddVertexAt(1, new Point2d(x0 + length, y0),         0, 0, 0);
                outline.AddVertexAt(2, new Point2d(x0 + length, y0 + barH),  0, 0, 0);
                outline.AddVertexAt(3, new Point2d(x0,          y0 + barH),  0, 0, 0);
                modelSpace.AppendEntity(outline);
                transaction.AddNewlyCreatedDBObject(outline, true);
                linesDrawn++;

                // Vertical separators at internal segment boundaries.
                for (int i = 1; i < ScaleBarSegments; i++)
                {
                    double xt = x0 + i * segmentLen;
                    var sep = new Line(
                        new Point3d(xt, y0, 0),
                        new Point3d(xt, y0 + barH, 0))
                    {
                        LayerId = layerId,
                        Color   = color,
                    };
                    modelSpace.AppendEntity(sep);
                    transaction.AddNewlyCreatedDBObject(sep, true);
                    linesDrawn++;
                }

                // Tick labels: value at each segment boundary (0, seg, 2seg, ..., length).
                // Labels sit below the bar (for bottom_* positions) or above (for top_*).
                bool labelsAbove = position.StartsWith("top_", StringComparison.Ordinal);
                double labelY = labelsAbove
                    ? y0 + barH + labelHeight * 0.4
                    : y0 - labelHeight * 1.4;
                // Pick label format from segment length, not the absolute length — that's
                // the precision the bar can resolve.
                double labelStep = segmentLen;

                // Pick which ticks get labeled: always 0 and length, fill in the middle so
                // that no two labels overlap (label width = chars × height × 0.7).
                string maxText = FormatGridValue(length, labelStep);
                double labelWidth = maxText.Length * labelHeight * 0.7;
                int skip = Math.Max(1, (int)Math.Ceiling(labelWidth / segmentLen));
                var labelIndices = new List<int> { 0 };
                for (int i = skip; i < ScaleBarSegments; i += skip) labelIndices.Add(i);
                if (labelIndices[labelIndices.Count - 1] != ScaleBarSegments)
                    labelIndices.Add(ScaleBarSegments);
                // Drop second-to-last if too close to last (last is non-negotiable).
                while (labelIndices.Count >= 3 &&
                       (labelIndices[labelIndices.Count - 1] - labelIndices[labelIndices.Count - 2]) * segmentLen < labelWidth)
                {
                    labelIndices.RemoveAt(labelIndices.Count - 2);
                }

                foreach (int i in labelIndices)
                {
                    double v = i * segmentLen;
                    double xt = x0 + v;
                    string text = FormatGridValue(v, labelStep);
                    double textWidth = text.Length * labelHeight * 0.7;
                    var lbl = new DBText
                    {
                        LayerId    = layerId,
                        Color      = color,
                        Position   = new Point3d(xt - textWidth * 0.5, labelY, 0),
                        Height     = labelHeight,
                        TextString = text,
                    };
                    modelSpace.AppendEntity(lbl);
                    transaction.AddNewlyCreatedDBObject(lbl, true);
                    labelsDrawn++;
                }

                anchor = new
                {
                    x = x0,
                    y = y0,
                    width = length,
                    height = barH,
                };

                transaction.Commit();
            }

            WriteOk(outputPath, "scale_bar", new
            {
                length = length,
                length_auto = requestedLength == null,
                position = position,
                segments = ScaleBarSegments,
                lines = linesDrawn,
                fills = fillsDrawn,
                labels = labelsDrawn,
                frame_source = frameSource,
                anchor = anchor,
                color_index = (int)ScaleBarColorIndex,
            });
        }

        /// <summary>
        /// Auto-pick a "nice round" scale-bar total length (1 / 2 / 5 × 10^N) close to
        /// 20% of the drawing width. Mirrors the philosophy of AutoPickStep — round
        /// numbers across vastly different drawing scales.
        /// </summary>
        private static double AutoPickScaleLength(double width)
        {
            if (width <= 0) return 1.0;
            double target = width * 0.2;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(target)));
            double mant = target / mag;
            double snap;
            if      (mant >= 5) snap = 5;
            else if (mant >= 2) snap = 2;
            else                snap = 1;
            return snap * mag;
        }

        private static string NormalizePosition(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "bottom_left";
            var v = raw.Trim().ToLowerInvariant();
            return v switch
            {
                "bottom_left"  => v,
                "bottom_right" => v,
                "top_left"     => v,
                "top_right"    => v,
                _              => "bottom_left",
            };
        }

        // ---- markers --------------------------------------------------------

        private static void DoMarkers(
            Document document, bool anchored, List<string> handles, string outputPath)
        {
            var database = document.Database;
            var anchoredOut = new List<object>();
            var dynamicOut  = new List<object>();
            var skipped     = new List<object>();
            double markerSize;
            string frameSource;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                var layerId = EnsureOverlayLayer(database, transaction);

                GetOverlayFrame(document, out var extMin, out var extMax, out frameSource);
                double maxSide = Math.Max(extMax.X - extMin.X, extMax.Y - extMin.Y);
                // Cross half-width: 1% of the FRAME max-side (current view, not whole-DB
                // extents — see GetOverlayFrame), floored. Visible without dominating geometry.
                markerSize = Math.Max(0.5, maxSide * 0.01);
                double labelHeight = markerSize * 1.2;

                var color = Color.FromColorIndex(ColorMethod.ByAci, MarkerColorIndex);

                if (anchored)
                {
                    var corners = new (string Tag, Point3d P)[]
                    {
                        ("TL", new Point3d(extMin.X, extMax.Y, 0)),
                        ("TR", new Point3d(extMax.X, extMax.Y, 0)),
                        ("BL", new Point3d(extMin.X, extMin.Y, 0)),
                        ("BR", new Point3d(extMax.X, extMin.Y, 0)),
                    };
                    foreach (var (tag, p) in corners)
                    {
                        DrawCross(modelSpace, transaction, layerId, color, p, markerSize);
                        // Label offset inward from the corner so it stays inside the frame
                        // (and thus inside the captured snapshot) rather than spilling off-edge.
                        double dx = tag.EndsWith("L") ?  markerSize * 1.2 : -markerSize * 4.5;
                        double dy = tag.StartsWith("T") ? -markerSize * 2.0 :  markerSize * 1.2;
                        var lbl = new DBText
                        {
                            LayerId    = layerId,
                            Color      = color,
                            Position   = new Point3d(p.X + dx, p.Y + dy, 0),
                            Height     = labelHeight,
                            TextString = tag,
                        };
                        modelSpace.AppendEntity(lbl);
                        transaction.AddNewlyCreatedDBObject(lbl, true);
                        anchoredOut.Add(new
                        {
                            tag = tag,
                            point = new { x = p.X, y = p.Y },
                        });
                    }
                }

                int index = 0;
                foreach (var handleStr in handles)
                {
                    index++;
                    ObjectId entId;
                    try
                    {
                        var handle = new Handle(Convert.ToInt64(handleStr, 16));
                        entId = database.GetObjectId(false, handle, 0);
                    }
                    catch
                    {
                        skipped.Add(new { index = index, handle = handleStr, reason = "handle not resolvable" });
                        continue;
                    }
                    if (entId == ObjectId.Null
                        || !(transaction.GetObject(entId, OpenMode.ForRead, false) is Entity entity))
                    {
                        skipped.Add(new { index = index, handle = handleStr, reason = "entity not found" });
                        continue;
                    }

                    Point3d? point = ExtractInsertionPoint(entity);
                    if (point == null)
                    {
                        skipped.Add(new { index = index, handle = handleStr, reason = "no insertion / position / centroid available" });
                        continue;
                    }

                    var p = point.Value;
                    DrawCross(modelSpace, transaction, layerId, color, p, markerSize);
                    // Label "#N" anchored just up-right of the cross.
                    var lbl = new DBText
                    {
                        LayerId    = layerId,
                        Color      = color,
                        Position   = new Point3d(p.X + markerSize * 1.2, p.Y + markerSize * 0.4, 0),
                        Height     = labelHeight,
                        TextString = "#" + index.ToString(CultureInfo.InvariantCulture),
                    };
                    modelSpace.AppendEntity(lbl);
                    transaction.AddNewlyCreatedDBObject(lbl, true);

                    dynamicOut.Add(new
                    {
                        index = index,
                        handle = handleStr,
                        point = new { x = p.X, y = p.Y, z = p.Z },
                        type = entity.GetType().Name,
                    });
                }

                transaction.Commit();
            }

            WriteOk(outputPath, "markers", new
            {
                anchored_count = anchoredOut.Count,
                dynamic_count  = dynamicOut.Count,
                skipped_count  = skipped.Count,
                marker_size    = markerSize,
                frame_source   = frameSource,
                anchored = anchoredOut,
                dynamic  = dynamicOut,
                skipped  = skipped,
                color_index = (int)MarkerColorIndex,
            });
        }

        /// <summary>
        /// Draw a small + cross centered on <paramref name="p"/>. Two Line entities,
        /// horizontal and vertical, each <paramref name="size"/> half-length on either side.
        /// </summary>
        private static void DrawCross(
            BlockTableRecord modelSpace, Transaction transaction,
            ObjectId layerId, Color color, Point3d p, double size)
        {
            var h = new Line(new Point3d(p.X - size, p.Y, 0), new Point3d(p.X + size, p.Y, 0))
            {
                LayerId = layerId,
                Color   = color,
            };
            modelSpace.AppendEntity(h);
            transaction.AddNewlyCreatedDBObject(h, true);

            var v = new Line(new Point3d(p.X, p.Y - size, 0), new Point3d(p.X, p.Y + size, 0))
            {
                LayerId = layerId,
                Color   = color,
            };
            modelSpace.AppendEntity(v);
            transaction.AddNewlyCreatedDBObject(v, true);
        }

        /// <summary>
        /// Resolve the canonical "this is where the entity lives" point. BlockReference/
        /// DBPoint/DBText have a natural anchor; for everything else fall back to the
        /// centroid of <c>GeometricExtents</c>. Returns null when no point is derivable
        /// (sentinel extents, abstract entities without geometry).
        /// </summary>
        private static Point3d? ExtractInsertionPoint(Entity entity)
        {
            switch (entity)
            {
                case BlockReference br: return br.Position;
                case DBPoint pt:        return pt.Position;
                case DBText txt:        return txt.Position;
            }
            try
            {
                var ext = entity.GeometricExtents;
                if (Math.Abs(ext.MinPoint.X) > 1e19 || Math.Abs(ext.MaxPoint.X) > 1e19) return null;
                return new Point3d(
                    (ext.MinPoint.X + ext.MaxPoint.X) * 0.5,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5,
                    (ext.MinPoint.Z + ext.MaxPoint.Z) * 0.5);
            }
            catch
            {
                return null;
            }
        }

        // ---- clear ----------------------------------------------------------

        private static void DoClear(Document document, string outputPath)
        {
            int removed = ClearOverlayEntities(document.Database);
            WriteOk(outputPath, "clear", new { removed_count = removed });
        }

        /// <summary>
        /// Read database extents defensively. AutoCAD throws <c>eInvalidInput</c>
        /// when extents have never been computed (empty drawing, no <c>_ZOOM_E</c>
        /// ever issued); NanoCAD/Teigha silently returns sentinel ±1e20. Either
        /// failure mode normalizes here: <paramref name="extMin"/> /
        /// <paramref name="extMax"/> fall back to (0,0)-(1,1), and the
        /// <paramref name="sentinel"/> out flag is true so the caller can annotate
        /// the response that the geometry frame is synthetic.
        /// </summary>
        private static void GetExtentsSafely(
            Database database, out Point3d extMin, out Point3d extMax, out bool sentinel)
        {
            sentinel = false;
            try
            {
                extMin = database.Extmin;
                extMax = database.Extmax;
                if (Math.Abs(extMin.X) > 1e19 || Math.Abs(extMax.X) > 1e19) sentinel = true;
            }
            catch
            {
                sentinel = true;
                extMin = new Point3d();
                extMax = new Point3d();
            }
            if (sentinel)
            {
                extMin = new Point3d(0, 0, 0);
                extMax = new Point3d(1, 1, 0);
            }
        }

        /// <summary>
        /// Frame (WCS rectangle) that overlay annotations are sized and positioned
        /// against. Prefers the CURRENT view window over the whole-database
        /// GeometricExtents.
        ///
        /// WHY: surveys often carry far-away topo/cadastral underlays, so
        /// Extmin/Extmax can span tens of kilometres. Sizing a marker cross or grid
        /// step off that (1% of a 93 km extents ≈ 930 m) makes the overlay useless
        /// once the user is zoomed to a ~160 m working window — crosses fill the
        /// frame and #N labels land off-screen. get_view captures the LIVE window
        /// (PrintWindow/BitBlt of the current view; it does NOT zoom-to-extents), so
        /// the current view's Width/Height is exactly what the snapshot will show.
        ///
        /// Falls back to database extents (with sentinel handling) when the view
        /// can't be read or is degenerate. <paramref name="source"/> reports which
        /// path was taken ("view" | "extents" | "extents_sentinel").
        /// </summary>
        private static void GetOverlayFrame(
            Document document, out Point3d frameMin, out Point3d frameMax, out string source)
        {
            if (TryGetCurrentViewWcsBox(document, out frameMin, out frameMax))
            {
                source = "view";
                return;
            }
            GetExtentsSafely(document.Database, out frameMin, out frameMax, out bool sentinel);
            source = sentinel ? "extents_sentinel" : "extents";
        }

        /// <summary>
        /// WCS bounding box of the current view's visible window. Reads the active
        /// viewport's <see cref="Autodesk.AutoCAD.DatabaseServices.ViewTableRecord"/>
        /// (Width/Height/CenterPoint in DCS, plus Target/ViewDirection/ViewTwist) and
        /// transforms the four DCS corners back to WCS via the standard DCS→WCS matrix.
        /// Returns false (and leaves outputs default) if the view is unreadable or its
        /// size is degenerate/sentinel — callers then fall back to database extents.
        /// </summary>
        private static bool TryGetCurrentViewWcsBox(Document document, out Point3d min, out Point3d max)
        {
            min = default(Point3d);
            max = default(Point3d);
            try
            {
                using (var view = document.Editor.GetCurrentView())
                {
                    double w = view.Width;
                    double h = view.Height;
                    if (!(w > 0) || !(h > 0) || w > 1e19 || h > 1e19) return false;

                    // DCS → WCS for the current view. Composition order (applied
                    // right-to-left): orient the view plane (PlaneToWorld), move it to
                    // the target, then undo the view twist. Inverse of the WCS→DCS
                    // matrix used by the standard ZOOM-window idiom.
                    Matrix3d dcsToWcs =
                        Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                        Matrix3d.Displacement(view.Target - Point3d.Origin) *
                        Matrix3d.PlaneToWorld(view.ViewDirection);

                    var c = view.CenterPoint;
                    double hw = w * 0.5, hh = h * 0.5;
                    var corners = new[]
                    {
                        new Point3d(c.X - hw, c.Y - hh, 0).TransformBy(dcsToWcs),
                        new Point3d(c.X + hw, c.Y - hh, 0).TransformBy(dcsToWcs),
                        new Point3d(c.X + hw, c.Y + hh, 0).TransformBy(dcsToWcs),
                        new Point3d(c.X - hw, c.Y + hh, 0).TransformBy(dcsToWcs),
                    };

                    double minX = corners[0].X, minY = corners[0].Y;
                    double maxX = corners[0].X, maxY = corners[0].Y;
                    foreach (var p in corners)
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.Y < minY) minY = p.Y;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Y > maxY) maxY = p.Y;
                    }
                    if (Math.Abs(minX) > 1e19 || Math.Abs(maxX) > 1e19) return false;

                    min = new Point3d(minX, minY, 0);
                    max = new Point3d(maxX, maxY, 0);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Erase every entity on the <c>CLAUDE_OVERLAY</c> layer in model space and
        /// return how many were removed. Single source of truth for overlay cleanup —
        /// also called by <c>SnapshotCommand</c> right after PNG capture so overlay
        /// annotations behave as transient (one snapshot, then gone).
        /// </summary>
        internal static int ClearOverlayEntities(Database database)
        {
            int removed = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var modelSpace = (BlockTableRecord)transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);

                var toErase = new List<ObjectId>();
                foreach (ObjectId id in modelSpace)
                {
                    if (transaction.GetObject(id, OpenMode.ForRead, false) is Entity e
                        && string.Equals(e.Layer, OverlayLayer, StringComparison.Ordinal))
                    {
                        toErase.Add(id);
                    }
                }
                foreach (var id in toErase)
                {
                    var e = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
                    e.Erase();
                    removed++;
                }
                transaction.Commit();
            }
            return removed;
        }

        // ---- layer ----------------------------------------------------------

        private static ObjectId EnsureOverlayLayer(Database database, Transaction transaction)
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(OverlayLayer))
            {
                return layerTable[OverlayLayer];
            }

            layerTable.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name = OverlayLayer,
                Color = Color.FromColorIndex(ColorMethod.ByAci, OverlayColorIndex),
                IsPlottable = false, // don't accidentally print overlay annotations
            };
            var id = layerTable.Add(ltr);
            transaction.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        // ---- envelopes ------------------------------------------------------

        private static void WriteOk(string outputPath, string op, object detail)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                op            = op,
                layer         = OverlayLayer,
                detail        = detail,
                note  = "view-only overlay change; no DWG mutation. Call get_view to see the annotation.",
                error = (string?)null,
            };
            File.WriteAllText(
                outputPath,
                JsonConvert.SerializeObject(payload, Formatting.Indented));
        }

        private static void WriteError(string outputPath, string? op, string message)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                op            = op,
                error         = message,
            };
            try
            {
                File.WriteAllText(
                    outputPath,
                    JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch { /* last-ditch */ }
        }
    }
}
