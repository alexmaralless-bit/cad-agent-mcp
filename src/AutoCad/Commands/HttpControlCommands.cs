using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using ClaudeBridge.AutoCad.Services;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// Manual control commands for the HTTP bridge service. The bridge auto-starts on
    /// plugin load (see <c>PluginApp.Initialize</c>) and auto-stops on plugin unload,
    /// so these are mostly diagnostic / recovery commands — useful when the listener
    /// failed to bind (port in use) or when the user wants to free the port temporarily.
    /// </summary>
    public sealed class HttpControlCommands
    {
        [CommandMethod("CLAUDE_HTTP_START")]
        public void Start()
        {
            var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (editor == null) return;

            var ok = HttpBridgeService.Instance.Start();
            if (ok)
            {
                editor.WriteMessage(
                    $"\nCLAUDE_HTTP_START: listening on {HttpBridgeService.Instance.Prefix}");
            }
            else
            {
                editor.WriteMessage(
                    $"\nCLAUDE_HTTP_START failed: {HttpBridgeService.Instance.LastStartError}");
            }
        }

        [CommandMethod("CLAUDE_HTTP_STOP")]
        public void Stop()
        {
            var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (editor == null) return;

            HttpBridgeService.Instance.Stop();
            editor.WriteMessage("\nCLAUDE_HTTP_STOP: bridge stopped.");
        }

        [CommandMethod("CLAUDE_HTTP_STATUS")]
        public void Status()
        {
            var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (editor == null) return;

            var svc = HttpBridgeService.Instance;
            if (svc.IsRunning)
            {
                editor.WriteMessage($"\nCLAUDE_HTTP_STATUS: running on {svc.Prefix}");
            }
            else
            {
                var reason = string.IsNullOrEmpty(svc.LastStartError)
                    ? "(not started)"
                    : $"(last error: {svc.LastStartError})";
                editor.WriteMessage($"\nCLAUDE_HTTP_STATUS: stopped {reason}");
            }
        }
    }
}
