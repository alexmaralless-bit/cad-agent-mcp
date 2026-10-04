using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using HostMgd.ApplicationServices;

namespace ClaudeBridge.NanoCad.Services
{
    /// <summary>
    /// HTTP bridge — exposes the plugin's CLAUDE_* commands over a local HTTP endpoint
    /// at <c>http://127.0.0.1:7890/</c>. Designed for the MCP proxy (or curl) to call
    /// from the same machine — no auth, no remote access.
    ///
    /// Threading (HARD-LEARNED — discovered empirically 2026-05-13):
    /// NanoCAD 24.1's <c>Document.SendStringToExecute</c> is **NOT thread-safe** despite
    /// what AutoCAD documentation suggests for the equivalent API. Calling it from a
    /// thread-pool thread crashes the entire NanoCAD process.
    ///
    /// Mitigation: at plugin Initialize (which runs on the NanoCAD main thread) we
    /// capture <c>SynchronizationContext.Current</c>. HTTP request handlers, which run
    /// on the thread pool, marshal the <c>SendStringToExecute</c> call onto the main
    /// thread via <c>SynchronizationContext.Send</c> (synchronous post).
    ///
    /// Architecture:
    /// - Singleton, owned by <c>PluginApp</c> (init→capture UI ctx + Start; terminate→Stop).
    /// - Accept loop on a dedicated background thread.
    /// - Request handlers on the .NET ThreadPool (default <c>HttpListener</c> behavior).
    /// - Command execution marshaled to NanoCAD main thread via captured sync context.
    /// - Result delivery: each command writes its output file at a known path; the HTTP
    ///   handler polls the file's mtime and returns content once it changes (504 on timeout).
    /// </summary>
    public sealed class HttpBridgeService : IDisposable
    {
        private const string DefaultPrefix = "http://127.0.0.1:7890/";
        private const string BridgeFolderName = "claude_bridge";
        private const int PollIntervalMs = 100;
        private const int DefaultTimeoutMs = 30000;

        private static readonly object SyncRoot = new object();
        private static HttpBridgeService? _instance;

        public static HttpBridgeService Instance
        {
            get
            {
                lock (SyncRoot)
                {
                    _instance ??= new HttpBridgeService();
                    return _instance;
                }
            }
        }

        private HttpListener? _listener;
        private Thread? _acceptThread;
        private CancellationTokenSource? _cts;
        private SynchronizationContext? _uiContext;

        public string Prefix { get; private set; } = DefaultPrefix;

        public bool IsRunning => _listener?.IsListening ?? false;

        public string? LastStartError { get; private set; }

        public bool HasUiContext => _uiContext != null;

        private HttpBridgeService() { }

        /// <summary>
        /// Captures the synchronization context of the calling thread. MUST be called
        /// from the NanoCAD main thread (typically from <c>PluginApp.Initialize</c>),
        /// otherwise marshaled commands will crash NanoCAD.
        /// </summary>
        public void CaptureUiContext()
        {
            _uiContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// Starts the listener if it isn't already running. Idempotent.
        /// </summary>
        public bool Start()
        {
            lock (SyncRoot)
            {
                if (IsRunning) return true;

                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(Prefix);
                    _listener.Start();

                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    _acceptThread = new Thread(() => AcceptLoop(token))
                    {
                        IsBackground = true,
                        Name = "ClaudeBridge.HttpListener"
                    };
                    _acceptThread.Start();

                    LastStartError = null;
                    return true;
                }
                catch (System.Exception ex)
                {
                    LastStartError = $"{ex.GetType().Name}: {ex.Message}";
                    try { _listener?.Close(); } catch { }
                    _listener = null;
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (SyncRoot)
            {
                if (_listener == null) return;

                try { _cts?.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
                _listener = null;

                try { _acceptThread?.Join(2000); } catch { }
                _acceptThread = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        public void Dispose() => Stop();

        // ---- accept loop ------------------------------------------------------

        private void AcceptLoop(CancellationToken token)
        {
            var listener = _listener;
            if (listener == null) return;

            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Listener was stopped mid-call. Exit the loop cleanly.
                    break;
                }
                catch (System.Exception)
                {
                    // Catch-all to keep the accept thread alive across unexpected errors.
                    // Brief backoff so we don't spin.
                    Thread.Sleep(50);
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        HandleRequest(context);
                    }
                    catch (System.Exception ex)
                    {
                        TryRespondError(context, 500, ex.GetType().Name + ": " + ex.Message);
                    }
                });
            }
        }

        // ---- request handling -------------------------------------------------

        private void HandleRequest(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            switch (path)
            {
                case "/healthz":
                    Respond(context, 200, "application/json", BuildHealthBody());
                    break;
                case "/ping":
                    ServeCommandOutput(context, "CLAUDE_PING", "status.json", "application/json");
                    break;
                case "/summary":
                    ServeCommandOutput(context, "CLAUDE_SUMMARY", "summary.json", "application/json");
                    break;
                case "/snapshot":
                    ServeCommandOutput(context, "CLAUDE_SNAPSHOT", "snapshot.json", "application/json");
                    break;
                case "/probe":
                    ServeCommandOutput(context, "CLAUDE_PROBE", "api_probe.txt", "text/plain");
                    break;
                case "/query":
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        Respond(context, 405, "text/plain",
                            "Method Not Allowed: /query requires POST with a JSON filter body.\n");
                        break;
                    }
                    ServePostCommand(
                        context,
                        "CLAUDE_QUERY",
                        inputFileName: "query_input.json",
                        outputFileName: "query.json",
                        contentType: "application/json");
                    break;
                case "/exec":
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        Respond(context, 405, "text/plain",
                            "Method Not Allowed: /exec requires POST with a JSON body "
                            + "{ kind: 'command'|'lisp', command: string, confirm: bool }.\n");
                        break;
                    }
                    ServePostCommand(
                        context,
                        "CLAUDE_EXEC",
                        inputFileName: "exec_input.json",
                        outputFileName: "exec.json",
                        contentType: "application/json");
                    break;
                case "/camera":
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        Respond(context, 405, "text/plain",
                            "Method Not Allowed: /camera requires POST with a JSON body "
                            + "{ op: 'extents'|'window', min?: {x,y}, max?: {x,y} }.\n");
                        break;
                    }
                    ServePostCommand(
                        context,
                        "CLAUDE_CAMERA",
                        inputFileName: "camera_input.json",
                        outputFileName: "camera.json",
                        contentType: "application/json");
                    break;
                case "/overlay":
                    if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        Respond(context, 405, "text/plain",
                            "Method Not Allowed: /overlay requires POST with a JSON body "
                            + "{ op: 'highlight'|'hud'|'grid'|'scale_bar'|'markers'|'clear', "
                            + "handles?: [string,...], step?: number, length?: number, position?: string, anchored?: bool }.\n");
                        break;
                    }
                    ServePostCommand(
                        context,
                        "CLAUDE_OVERLAY",
                        inputFileName: "overlay_input.json",
                        outputFileName: "overlay.json",
                        contentType: "application/json");
                    break;
                default:
                    Respond(context, 404, "text/plain", $"Not Found: {path}\n");
                    break;
            }
        }

        private string BuildHealthBody()
        {
            var version = typeof(HttpBridgeService).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
            var hasCtx = _uiContext != null ? "true" : "false";
            return "{\"status\":\"ok\",\"plugin\":\"ClaudeBridge.NanoCad\",\"version\":\""
                + version + "\",\"ui_context_captured\":" + hasCtx + "}";
        }

        private void ServeCommandOutput(
            HttpListenerContext context,
            string command,
            string outputFileName,
            string contentType,
            int timeoutMs = DefaultTimeoutMs)
        {
            if (_uiContext == null)
            {
                Respond(context, 503, "text/plain",
                    "UI context not captured — plugin must be loaded interactively (NETLOAD) "
                    + "so the main thread's SynchronizationContext can be captured at init time.\n");
                return;
            }

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var outputPath = Path.Combine(bridgeDir, outputFileName);

            var mtimeBefore = File.Exists(outputPath)
                ? File.GetLastWriteTimeUtc(outputPath)
                : DateTime.MinValue;

            // Marshal the SendStringToExecute call to the NanoCAD main thread.
            // SynchronizationContext.Send blocks until the posted action completes.
            System.Exception? marshalError = null;
            _uiContext.Send(_ =>
            {
                try
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                    {
                        marshalError = new InvalidOperationException("No active document.");
                        return;
                    }
                    // NanoCAD 24.1 signature: (string command, bool activate,
                    //                          bool wrapUpInactiveDoc, bool echoCommand)
                    doc.SendStringToExecute(command + " ", true, true, true);
                }
                catch (System.Exception ex)
                {
                    marshalError = ex;
                }
            }, null);

            if (marshalError != null)
            {
                var code = marshalError is InvalidOperationException ? 503 : 500;
                Respond(context, code, "text/plain",
                    marshalError.GetType().Name + ": " + marshalError.Message + "\n");
                return;
            }

            // Poll for output file change.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(PollIntervalMs);
                if (!File.Exists(outputPath)) continue;
                var currentMtime = File.GetLastWriteTimeUtc(outputPath);
                if (currentMtime <= mtimeBefore) continue;

                Thread.Sleep(20); // grace period for the writer to flush

                try
                {
                    var content = File.ReadAllText(outputPath, Encoding.UTF8);
                    Respond(context, 200, contentType, content);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                    var content = File.ReadAllText(outputPath, Encoding.UTF8);
                    Respond(context, 200, contentType, content);
                    return;
                }
            }

            Respond(context, 504, "text/plain",
                $"Timeout ({timeoutMs} ms) waiting for {outputFileName} to update.\n");
        }

        /// <summary>
        /// POST variant of <see cref="ServeCommandOutput"/>. Reads the HTTP request body,
        /// writes it to a known input file inside <c>%TEMP%\claude_bridge\</c>, then triggers
        /// the named NanoCAD command and polls for the output file to update. Identical
        /// threading discipline: the <c>SendStringToExecute</c> call is marshaled to the UI
        /// thread through <c>_uiContext.Send</c>.
        /// </summary>
        private void ServePostCommand(
            HttpListenerContext context,
            string command,
            string inputFileName,
            string outputFileName,
            string contentType,
            int timeoutMs = DefaultTimeoutMs)
        {
            if (_uiContext == null)
            {
                Respond(context, 503, "text/plain",
                    "UI context not captured — plugin must be loaded interactively (NETLOAD) "
                    + "so the main thread's SynchronizationContext can be captured at init time.\n");
                return;
            }

            var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, inputFileName);
            var outputPath = Path.Combine(bridgeDir, outputFileName);

            // Read the request body — caller is expected to send JSON; we don't parse here,
            // we just relay raw bytes to the command via the input file. The command itself
            // validates the JSON shape.
            string body;
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                body = reader.ReadToEnd();
            }
            catch (System.Exception ex)
            {
                Respond(context, 400, "text/plain", $"failed to read request body: {ex.Message}\n");
                return;
            }
            if (string.IsNullOrWhiteSpace(body)) body = "{}";

            try
            {
                File.WriteAllText(inputPath, body, Encoding.UTF8);
            }
            catch (System.Exception ex)
            {
                Respond(context, 500, "text/plain",
                    $"failed to write input file {inputPath}: {ex.Message}\n");
                return;
            }

            var mtimeBefore = File.Exists(outputPath)
                ? File.GetLastWriteTimeUtc(outputPath)
                : DateTime.MinValue;

            System.Exception? marshalError = null;
            _uiContext.Send(_ =>
            {
                try
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                    {
                        marshalError = new InvalidOperationException("No active document.");
                        return;
                    }
                    doc.SendStringToExecute(command + " ", true, true, true);
                }
                catch (System.Exception ex)
                {
                    marshalError = ex;
                }
            }, null);

            if (marshalError != null)
            {
                var code = marshalError is InvalidOperationException ? 503 : 500;
                Respond(context, code, "text/plain",
                    marshalError.GetType().Name + ": " + marshalError.Message + "\n");
                return;
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(PollIntervalMs);
                if (!File.Exists(outputPath)) continue;
                var currentMtime = File.GetLastWriteTimeUtc(outputPath);
                if (currentMtime <= mtimeBefore) continue;

                Thread.Sleep(20);

                try
                {
                    var content = File.ReadAllText(outputPath, Encoding.UTF8);
                    Respond(context, 200, contentType, content);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                    var content = File.ReadAllText(outputPath, Encoding.UTF8);
                    Respond(context, 200, contentType, content);
                    return;
                }
            }

            Respond(context, 504, "text/plain",
                $"Timeout ({timeoutMs} ms) waiting for {outputFileName} to update.\n");
        }

        private static void Respond(HttpListenerContext context, int statusCode, string contentType, string body)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = contentType + "; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            finally
            {
                try { context.Response.OutputStream.Close(); } catch { }
                try { context.Response.Close(); } catch { }
            }
        }

        private static void TryRespondError(HttpListenerContext context, int statusCode, string message)
        {
            try { Respond(context, statusCode, "text/plain", message + "\n"); }
            catch { /* last-ditch */ }
        }
    }
}
