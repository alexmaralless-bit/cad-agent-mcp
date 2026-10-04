using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using HostMgd.ApplicationServices;
using Teigha.Runtime;

namespace ClaudeBridge.NanoCad.Commands
{
    /// <summary>
    /// CLAUDE_SNAPSHOT — captures the active drawing as a PNG image, plus a JSON sidecar
    /// with metadata. Writes:
    ///   %TEMP%\claude_bridge\snapshot.png
    ///   %TEMP%\claude_bridge\snapshot.json
    /// Does not modify the drawing.
    ///
    /// Renderer (HEALTH Q1 resolved 2026-05-13 via CLAUDE_PROBE):
    /// Managed Teigha render APIs (GetGsView, CapturePreviewImage) are both
    /// MGD NOT_IMPLEMENTED in NanoCAD 24.1, so we capture at the OS level instead.
    /// Two paths are tried in order:
    ///   1) Win32 <c>PrintWindow</c> with PW_RENDERFULLCONTENT | PW_CLIENTONLY —
    ///      asks the window to render itself into our DC; works even when occluded.
    ///   2) <c>Graphics.CopyFromScreen</c> — last-resort screen-pixel BitBlt;
    ///      only works correctly when NanoCAD is the topmost window.
    /// The JSON metadata records which path was used in <c>capture.method</c>.
    /// Long-term cleanest path remains the PlotEngine pipeline
    /// (<c>HostMgd.PlottingServices.PlotFactory.CreatePublishEngine</c>) — to be used
    /// when controlled paper size / scale is required.
    /// </summary>
    public sealed class SnapshotCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string PngFileName = "snapshot.png";
        private const string JsonFileName = "snapshot.json";

        // ---- Win32 interop ----------------------------------------------------

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const uint PW_CLIENTONLY = 0x00000001;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        private const int SW_RESTORE = 9;
        // How long to let the window finish its restore animation + DWM repaint before
        // we ask PrintWindow for the contents. Anything below ~80 ms tends to capture
        // a partial frame on slower machines; 150 ms is a safe-but-not-sluggish default.
        private const int RestoreSettleMs = 150;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        // ---- Command ----------------------------------------------------------

        [CommandMethod("CLAUDE_SNAPSHOT")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null)
            {
                return;
            }

            try
            {
                var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
                Directory.CreateDirectory(bridgeDir);
                var pngPath = Path.Combine(bridgeDir, PngFileName);
                var jsonPath = Path.Combine(bridgeDir, JsonFileName);

                editor.UpdateScreen();

                // Resolve handles. The document MDI child is what we want to capture
                // (drawing area only); the application main frame is what gets minimized.
                IntPtr documentHandle = TryGetHandleFromObject(document, "Window");
                IntPtr mainHandle = TryGetHandleFromType(typeof(Application), "MainWindow", staticMember: true);

                // If the main frame is iconic (minimized), the MDI child is "parked"
                // at ~(-32000,-32000) and has no renderable surface — PrintWindow returns
                // false and the BitBlt fallback paints off-screen pixels (i.e. white).
                // Restore the frame so the document window has a real client area.
                bool restoredFromMinimized = false;
                if (mainHandle != IntPtr.Zero && IsIconic(mainHandle))
                {
                    ShowWindow(mainHandle, SW_RESTORE);
                    Thread.Sleep(RestoreSettleMs);
                    editor.UpdateScreen();
                    restoredFromMinimized = true;
                }

                IntPtr handle = documentHandle;
                bool usedDocumentWindow = handle != IntPtr.Zero;
                if (handle == IntPtr.Zero)
                {
                    handle = mainHandle;
                }
                if (handle == IntPtr.Zero)
                {
                    editor.WriteMessage("\nCLAUDE_SNAPSHOT: no usable window handle found.");
                    return;
                }

                if (!GetClientRect(handle, out var clientRect))
                {
                    editor.WriteMessage("\nCLAUDE_SNAPSHOT: GetClientRect failed.");
                    return;
                }
                int width = clientRect.Right - clientRect.Left;
                int height = clientRect.Bottom - clientRect.Top;
                if (width <= 0 || height <= 0)
                {
                    editor.WriteMessage($"\nCLAUDE_SNAPSHOT: window has zero size ({width}x{height}).");
                    return;
                }

                string captureMethod;
                int screenOriginX = 0;
                int screenOriginY = 0;

                using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    // Path 1: PrintWindow — survives occlusion, asks the window to redraw
                    // into our DC. Combined flags work best on modern DWM-composited windows.
                    bool printOk = TryPrintWindow(handle, bitmap);

                    if (printOk)
                    {
                        captureMethod = "win32_printwindow";
                    }
                    else
                    {
                        // Path 2: fall back to screen BitBlt — requires NanoCAD on top.
                        var origin = new POINT { X = clientRect.Left, Y = clientRect.Top };
                        ClientToScreen(handle, ref origin);
                        screenOriginX = origin.X;
                        screenOriginY = origin.Y;

                        using (var g = Graphics.FromImage(bitmap))
                        {
                            g.CopyFromScreen(
                                origin.X, origin.Y, 0, 0,
                                new Size(width, height),
                                CopyPixelOperation.SourceCopy);
                        }
                        captureMethod = "win32_bitblt_client_rect";
                    }

                    bitmap.Save(pngPath, ImageFormat.Png);
                }

                // Overlay annotations (HUD, bbox highlights, …) are transient by contract:
                // they exist for the snapshot and are wiped once the PNG is on disk, so
                // they never leak into the user's drawing state. If clearing fails for
                // any reason we still return the snapshot — the overlay is purely visual.
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

                var meta = new
                {
                    plugin = "ClaudeBridge.NanoCad",
                    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                    timestamp_utc = DateTime.UtcNow.ToString("O"),
                    document = new
                    {
                        name = document.Name ?? string.Empty,
                        filename = document.Database.Filename ?? string.Empty
                    },
                    png = new
                    {
                        path = pngPath,
                        width = width,
                        height = height
                    },
                    capture = new
                    {
                        method = captureMethod,
                        source = usedDocumentWindow ? "document_window" : "main_window",
                        restored_from_minimized = restoredFromMinimized,
                        // Only meaningful for the BitBlt fallback path.
                        screen_origin = new { x = screenOriginX, y = screenOriginY }
                    },
                    overlay = new
                    {
                        cleared_count = overlayCleared,
                        clear_error = overlayClearError,
                    }
                };

                File.WriteAllText(
                    jsonPath,
                    JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

                editor.WriteMessage(
                    $"\nCLAUDE_SNAPSHOT: wrote {pngPath} ({width}x{height}, method={captureMethod}) and {jsonPath}");
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nCLAUDE_SNAPSHOT failed: {ex.Message}");
            }
        }

        // ---- helpers ----------------------------------------------------------

        /// <summary>
        /// Calls <c>PrintWindow</c> with flags suited for modern DWM-composited windows.
        /// Returns true if PrintWindow returned success. Caller is responsible for the
        /// Bitmap lifecycle.
        /// </summary>
        private static bool TryPrintWindow(IntPtr hwnd, Bitmap dest)
        {
            using var g = Graphics.FromImage(dest);
            IntPtr hdc = g.GetHdc();
            try
            {
                // PW_CLIENTONLY     — skip non-client area (title bar / borders)
                // PW_RENDERFULLCONTENT — required for layered / DWM windows; without it,
                //                       semi-transparent overlays render as black.
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
            catch
            {
                return IntPtr.Zero;
            }
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
            catch
            {
                return IntPtr.Zero;
            }
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
