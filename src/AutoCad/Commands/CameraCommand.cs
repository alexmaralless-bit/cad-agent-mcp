using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_CAMERA — view-changing operations: zoom-to-extents and
    /// zoom-to-window. The "camera" category is distinct from "hands":
    /// these operations do NOT modify the DWG (no geometry change, no
    /// database mutation), so they intentionally skip the backup +
    /// dry-run/confirm machinery that gates CLAUDE_EXEC. They are
    /// dispatch-only.
    ///
    /// Input file: %TEMP%\claude_bridge\camera_input.json
    ///   { "op": "extents" }
    ///   { "op": "window", "min": {"x": .., "y": ..}, "max": {"x": .., "y": ..} }
    ///
    /// Output file: %TEMP%\claude_bridge\camera.json — echo of the op,
    /// the LISP we dispatched, an "ok" note, and a null/text error field.
    ///
    /// Asynchrony note (same as CLAUDE_EXEC): SendStringToExecute returns
    /// before the dispatched LISP actually runs. The caller should
    /// reissue get_view after this if they need to see the new framing.
    /// </summary>
    public sealed class CameraCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string InputFileName    = "camera_input.json";
        private const string OutputFileName   = "camera.json";

        [CommandMethod("CLAUDE_CAMERA")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null) return;

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, InputFileName);
            var outputPath = Path.Combine(bridgeDir, OutputFileName);

            CameraInput input;
            try
            {
                input = ReadInput(inputPath);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, null, $"failed to read input: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_CAMERA: bad input — {ex.Message}");
                return;
            }

            string lisp;
            try
            {
                lisp = BuildLisp(input);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, input.Op, ex.Message);
                editor.WriteMessage($"\nCLAUDE_CAMERA: {ex.Message}");
                return;
            }

            try
            {
                // wrapUpInactiveDocument=false: AutoCAD throws eInvalidInput with true
                // on already-active docs (NanoCAD silently allowed it).
                document.SendStringToExecute(lisp, true, false, true);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, input.Op, $"dispatch failed: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_CAMERA: dispatch failed — {ex.Message}");
                return;
            }

            WriteOk(outputPath, input, lisp);
            editor.WriteMessage($"\nCLAUDE_CAMERA: dispatched op={input.Op}");
        }

        // ---- input ----------------------------------------------------------

        private sealed class CameraInput
        {
            public string  Op   { get; init; } = "extents";
            public double? MinX { get; init; }
            public double? MinY { get; init; }
            public double? MaxX { get; init; }
            public double? MaxY { get; init; }
        }

        private static CameraInput ReadInput(string path)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException("camera_input.json not found");
            var raw = File.ReadAllText(path);
            var root = JObject.Parse(raw);

            var opTok = root["op"];
            var op = (opTok != null && opTok.Type == JTokenType.String)
                ? (opTok.Value<string>() ?? "extents")
                : "extents";
            if (op != "extents" && op != "window")
                throw new InvalidOperationException($"op must be 'extents' or 'window', got '{op}'");

            return new CameraInput
            {
                Op   = op,
                MinX = TryGetDouble(root, "min", "x"),
                MinY = TryGetDouble(root, "min", "y"),
                MaxX = TryGetDouble(root, "max", "x"),
                MaxY = TryGetDouble(root, "max", "y"),
            };
        }

        private static double? TryGetDouble(JObject root, string outer, string inner)
        {
            var o = root[outer] as JObject;
            if (o == null) return null;
            var v = o[inner];
            if (v == null) return null;
            if (v.Type != JTokenType.Float && v.Type != JTokenType.Integer) return null;
            return v.Value<double>();
        }

        // ---- LISP builder ---------------------------------------------------

        private static string BuildLisp(CameraInput input)
        {
            switch (input.Op)
            {
                case "extents":
                    // Zoom to drawing extents. Equivalent to user typing _ZOOM E.
                    return "(command \"_ZOOM\" \"_E\") (princ) ";

                case "window":
                    if (input.MinX is null || input.MinY is null
                        || input.MaxX is null || input.MaxY is null)
                    {
                        throw new InvalidOperationException(
                            "op=window requires min={x,y} and max={x,y}");
                    }
                    // InvariantCulture so "1.5" stays "1.5" (and not "1,5" on ru-RU
                    // locales) — LISP would choke on commas inside a number.
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "(command \"_ZOOM\" \"_W\" '({0} {1} 0) '({2} {3} 0)) (princ) ",
                        input.MinX, input.MinY, input.MaxX, input.MaxY);

                default:
                    throw new InvalidOperationException($"unknown op '{input.Op}'");
            }
        }

        // ---- envelopes ------------------------------------------------------

        private static void WriteOk(string outputPath, CameraInput input, string lisp)
        {
            object? min = input.MinX is not null
                ? (object)new { x = input.MinX, y = input.MinY }
                : null;
            object? max = input.MaxX is not null
                ? (object)new { x = input.MaxX, y = input.MaxY }
                : null;

            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                op            = input.Op,
                min           = min,
                max           = max,
                dispatched_lisp = lisp,
                note  = "view change dispatched; no DWG mutation. Call get_view next if you need to confirm the new framing.",
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
