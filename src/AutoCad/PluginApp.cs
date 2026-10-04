using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using ClaudeBridge.AutoCad.Services;

namespace ClaudeBridge.AutoCad
{
    /// <summary>
    /// Entry point invoked by AutoCAD after NETLOAD.
    ///
    /// Responsibilities:
    ///   1. Capture the main-thread <see cref="System.Threading.SynchronizationContext"/>
    ///      so HTTP request handlers (which run on background threadpool threads) can
    ///      marshal <c>SendStringToExecute</c> back to the main thread. Calling
    ///      <c>SendStringToExecute</c> from a background thread can crash the host.
    ///   2. Start the HTTP bridge listener on 127.0.0.1:7890.
    ///   3. Print a one-line "loaded" message and the bridge prefix to the editor.
    ///
    /// What this DOES NOT do (intentional divergence from the NanoCAD-side fork):
    ///   - Auto-opens nothing. AutoCAD has its own startup-drawing mechanisms
    ///     (Recent Files, FILEDIA, acaddoc.lsp, shell associations); injecting our
    ///     own DocumentManager.Open call would steal control from the user.
    ///     The NanoCAD-side Q23 patch only existed because NanoCAD lacked a
    ///     reliable equivalent.
    /// </summary>
    public sealed class PluginApp : IExtensionApplication
    {
        public void Initialize()
        {
            var editor = Application.DocumentManager.MdiActiveDocument?.Editor;

            editor?.WriteMessage(
                "\nClaudeBridge.AutoCad loaded. Commands: "
                + "CLAUDE_PING, CLAUDE_SUMMARY, CLAUDE_SNAPSHOT, CLAUDE_PROBE, "
                + "CLAUDE_QUERY, CLAUDE_EXEC, CLAUDE_CAMERA, CLAUDE_OVERLAY, "
                + "CLAUDE_HTTP_START/STOP/STATUS.");

            // Capture the main-thread sync context BEFORE starting the listener.
            HttpBridgeService.Instance.CaptureUiContext();

            var ok = HttpBridgeService.Instance.Start();
            if (ok)
            {
                editor?.WriteMessage(
                    $"\nHTTP bridge: listening on {HttpBridgeService.Instance.Prefix}"
                    + (HttpBridgeService.Instance.HasUiContext ? "" : " (WARN: UI context NOT captured)"));
            }
            else
            {
                editor?.WriteMessage(
                    $"\nHTTP bridge: failed to start ({HttpBridgeService.Instance.LastStartError}). Retry with CLAUDE_HTTP_START.");
            }
        }

        public void Terminate()
        {
            HttpBridgeService.Instance.Stop();
        }
    }
}
