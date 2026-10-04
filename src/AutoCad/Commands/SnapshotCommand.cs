using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_SNAPSHOT — captures the active drawing as a PNG image, plus a JSON sidecar
    /// with metadata. Writes:
    ///   %TEMP%\claude_bridge\snapshot.png
    ///   %TEMP%\claude_bridge\snapshot.json
    /// Does not modify the drawing.
    ///
    /// Renderer (HEALTH Q1 resolved 2026-05-13 via CLAUDE_PROBE):
    /// AutoCAD 2020 has working managed render APIs (Autodesk.AutoCAD.GraphicsInterface
    /// etc.), but we use the OS-level path for cross-host parity with the NanoCAD-side
    /// fork (where Teigha's GetGsView / CapturePreviewImage are MGD NOT_IMPLEMENTED).
    /// Two paths are tried in order:
    ///   1) Win32 <c>PrintWindow</c> with PW_RENDERFULLCONTENT | PW_CLIENTONLY —
    ///      asks the window to render itself into our DC; works even when occluded.
    ///   2) <c>Graphics.CopyFromScreen</c> — last-resort screen-pixel BitBlt;
    ///      only works correctly when AutoCAD is the topmost window.
    /// The JSON metadata records which path was used in <c>capture.method</c>.
    /// Long-term cleanest path remains the PlotEngine pipeline
    /// (<c>Autodesk.AutoCAD.PlottingServices.PlotFactory.CreatePublishEngine</c>) — to be used
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

                // Resolve handles. Note on AutoCAD vs NanoCAD divergence:
                //   - NanoCAD has true MDI children; Document.Window.Handle is the
                //     drawing window we want to capture (drawing area only).
                //   - AutoCAD 2010+ moved to tabbed documents under one main frame;
                //     Document.Window.Handle is a legacy stub that gets parked at
                //     (-32000, -32000) when "minimized" within the tab system, so
                //     PrintWindow fails and BitBlt grabs off-screen white pixels.
                //     The real render surface lives inside MainWindow.
                // We prefer Document.Window when it's a real on-screen window;
                // otherwise (off-screen / iconic / missing) fall back to MainWindow
                // and accept that the snapshot includes the ribbon/panels chrome.
                IntPtr documentHandle = TryGetHandleFromObject(document, "Window");
                IntPtr mainHandle = TryGetHandleFromType(typeof(Application), "MainWindow", staticMember: true);

                bool restoredFromMinimized = false;
                if (mainHandle != IntPtr.Zero && IsIconic(mainHandle))
                {
                    ShowWindow(mainHandle, SW_RESTORE);
                    Thread.Sleep(RestoreSettleMs);
                    editor.UpdateScreen();
                    restoredFromMinimized = true;
                }

                // Detect "parked off-screen" documentHandle (AutoCAD tabbed-doc case):
                // probe its client-rect screen position; anything ≤ -30000 means parked.
                bool documentHandleUsable = documentHandle != IntPtr.Zero;
                if (documentHandleUsable && GetClientRect(documentHandle, out var probeRect))
                {
                    var probeOrigin = new POINT { X = probeRect.Left, Y = probeRect.Top };
                    ClientToScreen(documentHandle, ref probeOrigin);
                    if (probeOrigin.X < -30000 || probeOrigin.Y < -30000)
                    {
                        documentHandleUsable = false;
                    }
                }

                IntPtr handle = documentHandleUsable ? documentHandle : mainHandle;
                bool usedDocumentWindow = documentHandleUsable;
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
                        // Path 2: fall back to screen BitBlt — requires AutoCAD on top.
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
                    plugin = "ClaudeBridge.AutoCad",
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
                    JsonConvert.SerializeObject(meta, Formatting.Indented));

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
