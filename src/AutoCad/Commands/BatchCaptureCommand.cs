using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
// NOTE: do NOT add `using Autodesk.AutoCAD.GraphicsSystem;` — that namespace also defines
// `Application`, which silently shadows `ApplicationServices.Application` when resolving
// `typeof(Application)` for window-handle reflection (handle comes back as IntPtr.Zero
// and the whole batch aborts with "no usable window handle"). Fully-qualified references
// below would survive that, but it's cleaner not to import the conflicting namespace.

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_BATCH_CAPTURE — batch zoom-to-bbox + PNG capture for the viewport refresh
    /// pipeline. Unlike CLAUDE_CAMERA (which dispatches one ZOOM via SendStringToExecute
    /// and returns immediately) and CLAUDE_SNAPSHOT (which captures the *current* view to
    /// a single fixed path), this command runs ENTIRELY on the AutoCAD main thread inside
    /// a single command invocation:
    ///   for each item: SetCurrentView(item.bbox) → UpdateScreen → PrintWindow → Save(item.png_path)
    ///
    /// Why synchronous: SendStringToExecute is async — it returns before the ZOOM actually
    /// commits. In a refresh loop that means we'd capture the previous frame for each item.
    /// Editor.SetCurrentView mutates the active view immediately, so the next PrintWindow
    /// sees the new framing.
    ///
    /// Input file: %TEMP%\claude_bridge\batch_capture_input.json
    ///   {
    ///     "items": [
    ///       {
    ///         "viewport_id": "vp_uzel_vrezki_pk0",
    ///         "bbox": {"min": {"x": ..., "y": ...}, "max": {"x": ..., "y": ...}},
    ///         "png_path": "C:\\absolute\\target\\path.png"
    ///       },
    ///       ...
    ///     ]
    ///   }
    ///
    /// Output file: %TEMP%\claude_bridge\batch_capture.json
    ///   {
    ///     "captured": N, "failed": M,
    ///     "items": [
    ///       {"viewport_id": "...", "png_path": "...", "ok": true|false,
    ///        "width": ..., "height": ..., "capture_method": "...", "error": null|"..."},
    ///       ...
    ///     ]
    ///   }
    /// </summary>
    public sealed class BatchCaptureCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string InputFileName    = "batch_capture_input.json";
        private const string OutputFileName   = "batch_capture.json";

        // Time to let the redraw settle between SetCurrentView and PrintWindow.
        // SnapshotCommand uses 150ms only for window-restore (much heavier);
        // for view changes 30ms is enough on a typical drawing — AutoCAD's vector pipeline
        // is fast even on dense layouts. Bump if stale frames appear in CI.
        private const int RedrawSettleMs = 50;

        // ---- Win32 interop (same as SnapshotCommand) -------------------------

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        private const uint PW_CLIENTONLY = 0x00000001;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        // ---- Command ----------------------------------------------------------

        [CommandMethod("CLAUDE_BATCH_CAPTURE")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null) return;

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, InputFileName);
            var outputPath = Path.Combine(bridgeDir, OutputFileName);

            List<BatchItem> items;
            try
            {
                items = ReadInput(inputPath);
            }
            catch (System.Exception ex)
            {
                WriteFatalError(outputPath, $"failed to read input: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_BATCH_CAPTURE: bad input — {ex.Message}");
                return;
            }

            // Resolve the capture window handle ONCE before the loop (it doesn't change
            // between viewports). Reuses the same selection logic as SnapshotCommand:
            // prefer Document.Window if on-screen, else fall back to Application.MainWindow.
            IntPtr documentHandle = TryGetHandleFromObject(document, "Window");
            IntPtr mainHandle = TryGetHandleFromType(typeof(Application), "MainWindow", staticMember: true);

            bool documentHandleUsable = documentHandle != IntPtr.Zero;
            if (documentHandleUsable && GetClientRect(documentHandle, out var probeRect))
            {
                var probeOrigin = new POINT { X = probeRect.Left, Y = probeRect.Top };
                ClientToScreen(documentHandle, ref probeOrigin);
                if (probeOrigin.X < -30000 || probeOrigin.Y < -30000)
                    documentHandleUsable = false;
            }
            IntPtr captureHandle = documentHandleUsable ? documentHandle : mainHandle;
            bool usedDocumentWindow = documentHandleUsable;

            if (captureHandle == IntPtr.Zero)
            {
                WriteFatalError(outputPath, "no usable window handle (document & main both unavailable)");
                editor.WriteMessage("\nCLAUDE_BATCH_CAPTURE: no usable window handle.");
                return;
            }

            var results = new List<object>(items.Count);
            int captured = 0;
            int failed = 0;

            foreach (var item in items)
            {
                try
                {
                    // 1. ZOOM synchronously via Editor.Command — same as user typing
                    //    "_ZOOM _W <min> <max>" at the prompt. Runs inline (NOT via
                    //    SendStringToExecute, which is async and races with our PrintWindow
                    //    on the next iteration). Editor.SetCurrentView with a modified
                    //    ViewTableRecord was tried first but did NOT change the active
                    //    viewport's view — PNGs came back as plain screenshots of whatever
                    //    framing the user already had. _.ZOOM _W is the proven path.
                    //
                    //    Underscore-dot prefix forces English command name even on a
                    //    localized AutoCAD (RU here), and forces the command-version
                    //    instead of any user-redefined alias.
                    var minPt = new Point3d(item.MinX, item.MinY, 0);
                    var maxPt = new Point3d(item.MaxX, item.MaxY, 0);
                    editor.Command("_.ZOOM", "_W", minPt, maxPt);

                    // 2. Force redraw + brief settle for paint to complete before PrintWindow.
                    editor.UpdateScreen();
                    Thread.Sleep(RedrawSettleMs);

                    // 3. Re-read client rect each iteration — defensive, in case user
                    //    resized the AutoCAD window mid-batch (rare but possible).
                    if (!GetClientRect(captureHandle, out var clientRect))
                    {
                        results.Add(MakeFailItem(item, "GetClientRect failed"));
                        failed++;
                        continue;
                    }
                    int width  = clientRect.Right  - clientRect.Left;
                    int height = clientRect.Bottom - clientRect.Top;
                    if (width <= 0 || height <= 0)
                    {
                        results.Add(MakeFailItem(item, $"window has zero size ({width}x{height})"));
                        failed++;
                        continue;
                    }

                    // 4. Capture via PrintWindow (PW_CLIENTONLY | PW_RENDERFULLCONTENT).
                    //    Same flags as SnapshotCommand — survives occlusion, handles DWM.
                    string captureMethod;
                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    {
                        bool printOk = TryPrintWindow(captureHandle, bitmap);
                        if (printOk)
                        {
                            captureMethod = "win32_printwindow";
                        }
                        else
                        {
                            // BitBlt fallback — only correct when AutoCAD is topmost.
                            var origin = new POINT { X = clientRect.Left, Y = clientRect.Top };
                            ClientToScreen(captureHandle, ref origin);
                            using (var g = Graphics.FromImage(bitmap))
                            {
                                g.CopyFromScreen(
                                    origin.X, origin.Y, 0, 0,
                                    new Size(width, height),
                                    CopyPixelOperation.SourceCopy);
                            }
                            captureMethod = "win32_bitblt_client_rect";
                        }

                        // Ensure target directory exists. Engineer-side paths are e.g.
                        // C:\...\.claude-bridge\snapshots\<ts>_<id>.png; the snapshots/
                        // subfolder may not exist on a fresh project.
                        var targetDir = Path.GetDirectoryName(item.PngPath);
                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        bitmap.Save(item.PngPath, ImageFormat.Png);
                    }

                    results.Add(new
                    {
                        viewport_id    = item.ViewportId,
                        png_path       = item.PngPath,
                        ok             = true,
                        width          = width,
                        height         = height,
                        capture_method = captureMethod,
                        bbox           = new {
                            min = new { x = item.MinX, y = item.MinY },
                            max = new { x = item.MaxX, y = item.MaxY }
                        },
                        error          = (string?)null,
                    });
                    captured++;
                }
                catch (System.Exception ex)
                {
                    results.Add(MakeFailItem(item, $"{ex.GetType().Name}: {ex.Message}"));
                    failed++;
                }
            }

            // Overlay cleanup once at end (same contract as SnapshotCommand): wipe any
            // transient overlay entities so they don't leak into the user's drawing.
            // Doing it once rather than per-iteration since we don't expect overlay use
            // during pure batch refresh.
            int overlayCleared = 0;
            string? overlayClearError = null;
            try
            {
                overlayCleared = OverlayCommand.ClearOverlayEntities(document.Database);
            }
            catch (System.Exception ex)
            {
                overlayClearError = $"{ex.GetType().Name}: {ex.Message}";
            }

            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                document = new
                {
                    name     = document.Name ?? string.Empty,
                    filename = document.Database.Filename ?? string.Empty,
                },
                requested = items.Count,
                captured  = captured,
                failed    = failed,
                capture_handle_source = usedDocumentWindow ? "document_window" : "main_window",
                overlay = new
                {
                    cleared_count = overlayCleared,
                    clear_error   = overlayClearError,
                },
                items = results,
                error = (string?)null,
            };

            File.WriteAllText(
                outputPath,
                JsonConvert.SerializeObject(payload, Formatting.Indented));

            editor.WriteMessage(
                $"\nCLAUDE_BATCH_CAPTURE: {captured}/{items.Count} captured, {failed} failed.");
        }

        // ---- input parsing ----------------------------------------------------

        private sealed class BatchItem
        {
            public string ViewportId { get; init; } = string.Empty;
            public double MinX { get; init; }
            public double MinY { get; init; }
            public double MaxX { get; init; }
            public double MaxY { get; init; }
            public string PngPath { get; init; } = string.Empty;
        }

        private static List<BatchItem> ReadInput(string path)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException("batch_capture_input.json not found");
            var raw = File.ReadAllText(path);
            var root = JObject.Parse(raw);
            var itemsTok = root["items"];
            if (itemsTok == null || itemsTok.Type != JTokenType.Array)
                throw new InvalidOperationException("input must contain items: [...] array");

            var arr = (JArray)itemsTok;
            var list = new List<BatchItem>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                var it = arr[i] as JObject;
                if (it == null)
                    throw new InvalidOperationException($"items[{i}] is not an object");

                var vid = it["viewport_id"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(vid))
                    throw new InvalidOperationException($"items[{i}].viewport_id missing");

                var png = it["png_path"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(png))
                    throw new InvalidOperationException($"items[{i}].png_path missing");

                var bbox = it["bbox"] as JObject;
                if (bbox == null)
                    throw new InvalidOperationException($"items[{i}].bbox missing");

                var min = bbox["min"] as JObject;
                var max = bbox["max"] as JObject;
                if (min == null || max == null)
                    throw new InvalidOperationException($"items[{i}].bbox.min/max missing");

                double? mnX = ReadDouble(min, "x"), mnY = ReadDouble(min, "y");
                double? mxX = ReadDouble(max, "x"), mxY = ReadDouble(max, "y");
                if (mnX is null || mnY is null || mxX is null || mxY is null)
                    throw new InvalidOperationException($"items[{i}].bbox has non-numeric coords");
                if (mxX.Value <= mnX.Value || mxY.Value <= mnY.Value)
                    throw new InvalidOperationException(
                        $"items[{i}] bbox invalid: min must be < max in both axes");

                list.Add(new BatchItem
                {
                    ViewportId = vid!,
                    MinX = mnX.Value, MinY = mnY.Value,
                    MaxX = mxX.Value, MaxY = mxY.Value,
                    PngPath = png!,
                });
            }

            if (list.Count == 0)
                throw new InvalidOperationException("items[] empty — nothing to capture");
            return list;
        }

        private static double? ReadDouble(JObject obj, string key)
        {
            var v = obj[key];
            if (v == null) return null;
            if (v.Type != JTokenType.Float && v.Type != JTokenType.Integer) return null;
            return v.Value<double>();
        }

        // ---- per-item error helpers ------------------------------------------

        private static object MakeFailItem(BatchItem item, string error) => new
        {
            viewport_id    = item.ViewportId,
            png_path       = item.PngPath,
            ok             = false,
            width          = 0,
            height         = 0,
            capture_method = (string?)null,
            bbox           = new {
                min = new { x = item.MinX, y = item.MinY },
                max = new { x = item.MaxX, y = item.MaxY }
            },
            error          = error,
        };

        private static void WriteFatalError(string outputPath, string message)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                requested     = 0,
                captured      = 0,
                failed        = 0,
                items         = new object[0],
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

        // ---- Win32 capture helpers (copied from SnapshotCommand) -------------

        private static bool TryPrintWindow(IntPtr hwnd, Bitmap dest)
        {
            using var g = Graphics.FromImage(dest);
            IntPtr hdc = g.GetHdc();
            try
            {
                return PrintWindow(hwnd, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        private static IntPtr TryGetHandleFromObject(object source, string propertyName)
        {
            try
            {
                var prop = source.GetType().GetProperty(propertyName);
                var window = prop?.GetValue(source);
                return window != null ? ExtractHandle(window) : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr TryGetHandleFromType(Type type, string propertyName, bool staticMember)
        {
            try
            {
                var flags = BindingFlags.Public | (staticMember ? BindingFlags.Static : BindingFlags.Instance);
                var prop = type.GetProperty(propertyName, flags);
                var window = prop?.GetValue(null);
                return window != null ? ExtractHandle(window) : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        private static IntPtr ExtractHandle(object source)
        {
            var handleProp = source.GetType().GetProperty("Handle");
            if (handleProp != null)
            {
                var value = handleProp.GetValue(source);
                if (value is IntPtr ptr) return ptr;
            }
            return IntPtr.Zero;
        }
    }
}
