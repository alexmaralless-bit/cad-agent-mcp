using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HostMgd.ApplicationServices;
using Teigha.Runtime;

namespace ClaudeBridge.NanoCad.Commands
{
    /// <summary>
    /// CLAUDE_PING — proof-of-life command.
    /// Writes a short message to the NanoCAD editor and a status JSON file to %TEMP%\claude_bridge\status.json.
    /// Acceptance criterion (step 1): NETLOAD plugin → run CLAUDE_PING → status.json appears with current timestamp.
    /// Does not modify the drawing in any way.
    /// </summary>
    public sealed class PingCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string StatusFileName = "status.json";

        [CommandMethod("CLAUDE_PING")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;

            try
            {
                var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
                Directory.CreateDirectory(bridgeDir);

                var statusPath = Path.Combine(bridgeDir, StatusFileName);

                var pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
                var documentName = document?.Name ?? string.Empty;

                var status = new
                {
                    plugin = "ClaudeBridge.NanoCad",
                    version = pluginVersion,
                    status = "alive",
                    timestamp_utc = DateTime.UtcNow.ToString("O"),
                    nanocad_document = documentName
                };

                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(statusPath, json);

                editor?.WriteMessage(
                    $"\nCLAUDE_PING: alive. Status file: {statusPath}");
            }
            catch (System.Exception ex)
            {
                // Note: `Teigha.Runtime` also declares an `Exception` type, so we fully-qualify
                // to `System.Exception` to disambiguate.
                editor?.WriteMessage($"\nCLAUDE_PING failed: {ex.Message}");
            }
        }
    }
}
