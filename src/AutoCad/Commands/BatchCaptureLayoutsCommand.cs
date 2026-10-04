using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
// NOTE: do NOT add `using Autodesk.AutoCAD.GraphicsSystem;` — that namespace also defines
// `Application`, which silently shadows `ApplicationServices.Application` when resolving
// `typeof(Application)` for window-handle reflection (handle → IntPtr.Zero, batch aborts).

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_BATCH_CAPTURE_LAYOUTS — batch PNG capture of PAPER-SPACE layouts (sheets),
    /// the layout-equivalent of CLAUDE_BATCH_CAPTURE (which frames model-space bboxes).
    ///
    /// Why a dedicated command: refreshing sheet snapshots used to require, PER sheet,
    /// a run_lisp (vla-put-ActiveLayout + ZOOM E) — each an async dispatch that (a) triggers
    /// an auto-backup of the dwg, (b) cannot return its result, and (c) forced a get_view
    /// round-trip whose image landed in the agent context. This command does the whole loop
    /// on the AutoCAD main thread, synchronously, writing PNGs straight to disk — no backups,
    /// no images in context, no async race — and restores the engineer's active tab at the end.
    ///
    /// Layouts are addressed by TabOrder (int) — robust against cyrillic layout names that
    /// mojibake through the bridge. `layout_name` is also accepted as a fallback.
    ///
    /// Input file: %TEMP%\claude_bridge\batch_capture_layouts_input.json
    ///   { "list_layouts": true }                              ← inventory only, no capture
    ///   OR
    ///   { "restore_active": true,                             ← default true
    ///     "items": [ { "tab": 6, "png_path": "C:\\...\\sheet.png" }, ... ] }
    ///
    /// Output file: %TEMP%\claude_bridge\batch_capture_layouts.json
    ///   list mode:    { mode:"list", current_layout, current_tab, count, layouts:[{tab,name,is_model}] }
    ///   capture mode: { mode:"capture", requested, captured, failed, restored_layout,
    ///                   layouts:[...], items:[{tab,layout_name,png_path,ok,width,height,capture_method,error}] }
    /// </summary>
    public sealed class BatchCaptureLayoutsCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string InputFileName    = "batch_capture_layouts_input.json";
        private const string OutputFileName   = "batch_capture_layouts.json";

        // Layout switch + regen is heavier than a plain view change, so allow a touch more
        // settle than BatchCaptureCommand's 50ms before grabbing the frame.
        private const int RedrawSettleMs = 80;

        // ---- Win32 interop (duplicated from BatchCaptureCommand) --------------

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

        [CommandMethod("CLAUDE_BATCH_CAPTURE_LAYOUTS")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null) return;
            var db = document.Database;

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, InputFileName);
            var outputPath = Path.Combine(bridgeDir, OutputFileName);

            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(inputPath));
            }
            catch (System.Exception ex)
            {
                WriteFatalError(outputPath, $"failed to read input: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_BATCH_CAPTURE_LAYOUTS: bad input — {ex.Message}");
                return;
            }

            var inventory = ReadLayoutInventory(db);
            var lm = LayoutManager.Current;
            string originalLayout = lm.CurrentLayout ?? string.Empty;
            int originalTab = inventory.FirstOrDefault(l => l.Name == originalLayout)?.Tab ?? -1;

            bool listOnly      = root["list_layouts"]?.Value<bool>() ?? false;
            bool restoreActive = root["restore_active"]?.Value<bool>() ?? true;

            if (listOnly)
            {
                WriteListOutput(outputPath, document, inventory, originalLayout, originalTab);
                editor.WriteMessage($"\nCLAUDE_BATCH_CAPTURE_LAYOUTS: listed {inventory.Count} layouts.");
                return;
            }

            // ---- capture mode -------------------------------------------------
            List<LayoutItem> items;
            try
            {
                items = ReadItems(root, inventory);
            }
            catch (System.Exception ex)
            {
                WriteFatalError(outputPath, ex.Message);
                editor.WriteMessage($"\nCLAUDE_BATCH_CAPTURE_LAYOUTS: {ex.Message}");
                return;
            }

            IntPtr captureHandle = ResolveCaptureHandle(document, out bool usedDocumentWindow);
            if (captureHandle == IntPtr.Zero)
            {
                WriteFatalError(outputPath, "no usable window handle (document & main both unavailable)");
                editor.WriteMessage("\nCLAUDE_BATCH_CAPTURE_LAYOUTS: no usable window handle.");
                return;
            }

            var results = new List<object>(items.Count);
            int captured = 0, failed = 0;

            foreach (var item in items)
            {
                var r = CaptureOneLayout(editor, lm, captureHandle, item);
                results.Add(r.ToJson(item));
                if (r.Ok) captured++; else failed++;
            }

            // Restore the engineer's original active tab.
            string? restoreError = null;
            if (restoreActive && !string.IsNullOrEmpty(originalLayout))
            {
                try
                {
                    lm.CurrentLayout = originalLayout;
                    editor.UpdateScreen();
                }
                catch (System.Exception ex)
                {
                    restoreError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                mode          = "capture",
                document = new
                {
                    name     = document.Name ?? string.Empty,
                    filename = document.Database.Filename ?? string.Empty,
                },
                requested = items.Count,
                captured  = captured,
                failed    = failed,
                capture_handle_source = usedDocumentWindow ? "document_window" : "main_window",
                restored_layout = restoreActive ? originalLayout : null,
                restore_error   = restoreError,
                layouts = inventory.Select(l => new { tab = l.Tab, name = l.Name, is_model = l.IsModel }).ToArray(),
                items   = results,
                error   = (string?)null,
            };

            File.WriteAllText(outputPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
            editor.WriteMessage(
                $"\nCLAUDE_BATCH_CAPTURE_LAYOUTS: {captured}/{items.Count} captured, {failed} failed.");
        }

        // ---- the "snapshot one specified layout" primitive --------------------

        private sealed class CaptureResult
        {
            public bool Ok;
            public int Width;
            public int Height;
            public string? Method;
            public string? Error;

            public object ToJson(LayoutItem item) => new
            {
                tab            = item.Tab,
                layout_name    = item.LayoutName,
                png_path       = item.PngPath,
                ok             = Ok,
                width          = Width,
                height         = Height,
                capture_method = Method,
                error          = Error,
            };
        }

        /// <summary>
        /// Switch the active tab to <paramref name="item"/>'s layout, ZOOM Extents (paper
        /// space), then PrintWindow → save PNG. Fully synchronous on the main thread.
        /// </summary>
        private static CaptureResult CaptureOneLayout(Editor editor, LayoutManager lm, IntPtr captureHandle, LayoutItem item)
        {
            try
            {
                // 1. Switch active layout. Property set (NOT SendStringToExecute) → synchronous,
                //    no async race, no auto-backup. Drawing reflects the tab before we capture.
                lm.CurrentLayout = item.LayoutName;

                // 2. Frame the whole sheet. Underscore-dot forces the English ZOOM even on RU AutoCAD.
                editor.Command("_.ZOOM", "_E");
                editor.UpdateScreen();
                Thread.Sleep(RedrawSettleMs);

                // 3. Capture the window client area.
                if (!GetClientRect(captureHandle, out var clientRect))
                    return new CaptureResult { Ok = false, Error = "GetClientRect failed" };
                int width  = clientRect.Right  - clientRect.Left;
                int height = clientRect.Bottom - clientRect.Top;
                if (width <= 0 || height <= 0)
                    return new CaptureResult { Ok = false, Error = $"window has zero size ({width}x{height})" };

                string method;
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    if (TryPrintWindow(captureHandle, bitmap))
                    {
                        method = "win32_printwindow";
                    }
                    else
                    {
                        var origin = new POINT { X = clientRect.Left, Y = clientRect.Top };
                        ClientToScreen(captureHandle, ref origin);
                        using (var g = Graphics.FromImage(bitmap))
                        {
                            g.CopyFromScreen(origin.X, origin.Y, 0, 0,
                                new Size(width, height), CopyPixelOperation.SourceCopy);
                        }
                        method = "win32_bitblt_client_rect";
                    }

                    var targetDir = Path.GetDirectoryName(item.PngPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        Directory.CreateDirectory(targetDir);

                    bitmap.Save(item.PngPath, ImageFormat.Png);
                }

                return new CaptureResult { Ok = true, Width = width, Height = height, Method = method };
            }
            catch (System.Exception ex)
            {
                return new CaptureResult { Ok = false, Error = $"{ex.GetType().Name}: {ex.Message}" };
            }
        }

        // ---- layout inventory -------------------------------------------------

        private sealed class LayoutInfo
        {
            public string Name { get; init; } = string.Empty;
            public int Tab { get; init; }
            public bool IsModel { get; init; }
        }

        private static List<LayoutInfo> ReadLayoutInventory(Database db)
        {
            var list = new List<LayoutInfo>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout)
                    {
                        list.Add(new LayoutInfo
                        {
                            Name = layout.LayoutName,
                            Tab = layout.TabOrder,
                            IsModel = layout.TabOrder == 0,  // Model is always TabOrder 0
                        });
                    }
                }
                tr.Commit();
            }
            list.Sort((a, b) => a.Tab.CompareTo(b.Tab));
            return list;
        }

        // ---- input parsing ----------------------------------------------------

        private sealed class LayoutItem
        {
            public int Tab { get; init; }
            public string LayoutName { get; init; } = string.Empty;
            public string PngPath { get; init; } = string.Empty;
        }

        private static List<LayoutItem> ReadItems(JObject root, List<LayoutInfo> inventory)
        {
            var itemsTok = root["items"];
            if (itemsTok == null || itemsTok.Type != JTokenType.Array)
                throw new InvalidOperationException("input must contain items:[...] (or list_layouts:true)");

            var arr = (JArray)itemsTok;
            var list = new List<LayoutItem>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JObject it)
                    throw new InvalidOperationException($"items[{i}] is not an object");

                var png = it["png_path"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(png))
                    throw new InvalidOperationException($"items[{i}].png_path missing");

                int? tab = (it["tab"]?.Type == JTokenType.Integer) ? it["tab"]!.Value<int>() : (int?)null;
                var name = it["layout_name"]?.Value<string>();

                LayoutInfo? match = null;
                if (tab.HasValue)
                    match = inventory.FirstOrDefault(l => l.Tab == tab.Value);
                if (match == null && !string.IsNullOrWhiteSpace(name))
                    match = inventory.FirstOrDefault(l => l.Name == name);

                if (match == null)
                    throw new InvalidOperationException(
                        $"items[{i}] layout not found (tab={(tab?.ToString() ?? "?")}, name={name ?? "?"})");

                list.Add(new LayoutItem { Tab = match.Tab, LayoutName = match.Name, PngPath = png! });
            }

            if (list.Count == 0)
                throw new InvalidOperationException("items[] empty — nothing to capture");
            return list;
        }

        // ---- output helpers ---------------------------------------------------

        private static void WriteListOutput(
            string outputPath, Document document, List<LayoutInfo> inventory,
            string currentLayout, int currentTab)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                mode          = "list",
                document = new
                {
                    name     = document.Name ?? string.Empty,
                    filename = document.Database.Filename ?? string.Empty,
                },
                current_layout = currentLayout,
                current_tab    = currentTab,
                count          = inventory.Count,
                layouts        = inventory.Select(l => new { tab = l.Tab, name = l.Name, is_model = l.IsModel }).ToArray(),
                error          = (string?)null,
            };
            File.WriteAllText(outputPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
        }

        private static void WriteFatalError(string outputPath, string message)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                mode          = "error",
                requested     = 0,
                captured      = 0,
                failed        = 0,
                items         = new object[0],
                error         = message,
            };
            try { File.WriteAllText(outputPath, JsonConvert.SerializeObject(payload, Formatting.Indented)); }
            catch { /* last-ditch */ }
        }

        // ---- Win32 capture helpers (duplicated from BatchCaptureCommand) -------

        private static IntPtr ResolveCaptureHandle(Document document, out bool usedDocumentWindow)
        {
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
            usedDocumentWindow = documentHandleUsable;
            return documentHandleUsable ? documentHandle : mainHandle;
        }

        private static bool TryPrintWindow(IntPtr hwnd, Bitmap dest)
        {
            using var g = Graphics.FromImage(dest);
            IntPtr hdc = g.GetHdc();
            try { return PrintWindow(hwnd, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
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
