using System;
using System.IO;
using HostMgd.ApplicationServices;
using Teigha.Runtime;
using ClaudeBridge.NanoCad.Services;

namespace ClaudeBridge.NanoCad
{
    /// <summary>
    /// Entry point invoked by NanoCAD after NETLOAD.
    ///
    /// Critical: Initialize runs on the NanoCAD main thread. We MUST capture the
    /// SynchronizationContext here so that HTTP request handlers (which run on
    /// background threadpool threads) can marshal <c>SendStringToExecute</c> back to
    /// the main thread. NanoCAD 24.1 crashes if SendStringToExecute is called from a
    /// background thread.
    ///
    /// Q23 (auto-open default drawing): NanoCAD does NOT call AutoCAD's
    /// <c>(s::startup)</c> hook (verified empirically) and a top-level
    /// <c>(command "_OPEN" path)</c> from ncad.lsp runs before Drawing1 is fully
    /// materialized and gets clobbered. The reliable mechanism is the managed
    /// <see cref="DocumentCollection.DocumentCreated"/> event: subscribe here in
    /// Initialize(), open the configured default drawing on the FIRST create, and
    /// immediately unsubscribe (one-shot). The managed
    /// <c>DocumentManager.Open</c> bypasses FILEDIA and the
    /// "repeat-last-command-on-empty-Enter" command-line quirk.
    /// </summary>
    public sealed class PluginApp : IExtensionApplication
    {
        private const string DefaultDrawingEnvVar = "CLAUDE_BRIDGE_DEFAULT_DRAWING";
        // No hardcoded fallback drawing: auto-open happens only when the
        // CLAUDE_BRIDGE_DEFAULT_DRAWING env-var points to an existing file.
        private const string DefaultDrawingFallback = "";
        private static readonly string AutoOpenLogPath =
            Path.Combine(Path.GetTempPath(), "claude_bridge", "autoopen.log");

        public void Initialize()
        {
            LogAuto($"Initialize start. MdiActiveDocument={(Application.DocumentManager.MdiActiveDocument != null ? "EXISTS" : "null")}, Count={Application.DocumentManager.Count}");

            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;

            editor?.WriteMessage(
                "\nClaudeBridge.NanoCad loaded. Commands: "
                + "CLAUDE_PING, CLAUDE_SUMMARY, CLAUDE_SNAPSHOT, CLAUDE_PROBE, "
                + "CLAUDE_QUERY, CLAUDE_EXEC, CLAUDE_CAMERA, "
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

            // Q23 auto-open. Two scenarios:
            // 1. Drawing1 (or any document) is already active by the time
            //    Initialize() runs -> the DocumentCreated event has already
            //    fired without us, so subscribing is useless. Do an
            //    immediate direct call instead.
            // 2. No document yet -> subscribe to DocumentCreated, fire once.
            // Both paths are one-shot.
            if (Application.DocumentManager.MdiActiveDocument != null)
            {
                LogAuto("Active document exists at Initialize -> direct open");
                TryOpenDefaultDrawing();
            }
            else
            {
                LogAuto("No active document at Initialize -> subscribing to DocumentCreated");
                Application.DocumentManager.DocumentCreated += OnFirstDocumentCreated;
            }
        }

        public void Terminate()
        {
            Application.DocumentManager.DocumentCreated -= OnFirstDocumentCreated;
            HttpBridgeService.Instance.Stop();
        }

        private static void OnFirstDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            LogAuto("DocumentCreated event fired");
            Application.DocumentManager.DocumentCreated -= OnFirstDocumentCreated;
            TryOpenDefaultDrawing();
        }

        private static void TryOpenDefaultDrawing()
        {
            var path = ResolveDefaultDrawing();
            if (string.IsNullOrEmpty(path))
            {
                LogAuto("ResolveDefaultDrawing returned null -> skip");
                return;
            }
            LogAuto($"Calling DocumentManager.Open('{path}', false)");
            try
            {
                Application.DocumentManager.Open(path, false);
                LogAuto("Open returned without throw");
            }
            catch (System.Exception ex)
            {
                LogAuto($"Open threw {ex.GetType().Name}: {ex.Message}");
                var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage(
                    $"\nClaudeBridge: auto-open '{path}' failed -- {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void LogAuto(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AutoOpenLogPath));
                File.AppendAllText(AutoOpenLogPath,
                    $"[{DateTime.UtcNow:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch { /* logger must never throw */ }
        }

        /// <summary>
        /// Default drawing comes from the <c>CLAUDE_BRIDGE_DEFAULT_DRAWING</c>
        /// env-var (per-machine override). If it does not resolve to an
        /// existing file, returns null and auto-open is skipped silently.
        /// </summary>
        private static string ResolveDefaultDrawing()
        {
            var fromEnv = Environment.GetEnvironmentVariable(DefaultDrawingEnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            {
                return fromEnv;
            }
            if (!string.IsNullOrEmpty(DefaultDrawingFallback) && File.Exists(DefaultDrawingFallback))
            {
                return DefaultDrawingFallback;
            }
            return null;
        }
    }
}
