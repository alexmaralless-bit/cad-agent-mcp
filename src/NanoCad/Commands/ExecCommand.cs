using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HostMgd.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Runtime;

namespace ClaudeBridge.NanoCad.Commands
{
    /// <summary>
    /// CLAUDE_EXEC — single .NET command behind two MCP tools (`execute_command`
    /// and `run_lisp`). Implements the two-phase safety contract:
    ///
    ///   1. Dry-run (confirm=false): writes a plan envelope to exec.json,
    ///      does NOT execute anything.
    ///   2. Confirm  (confirm=true):  creates a backup .dwg, wraps the user
    ///      payload in UNDO BEGIN / UNDO END (so the whole operation is a
    ///      single undo-able block), dispatches via SendStringToExecute.
    ///
    /// Input file: %TEMP%\claude_bridge\exec_input.json
    ///   { "kind": "command"|"lisp", "command": "<payload>", "confirm": false }
    ///
    /// Output file: %TEMP%\claude_bridge\exec.json — see <see cref="WritePlan"/>
    /// and <see cref="WriteCommitted"/> for the envelope shapes.
    ///
    /// Async note: SendStringToExecute returns BEFORE the command actually
    /// executes. The committed-envelope therefore reports "dispatched", not
    /// "completed". Claude is expected to verify via get_view / query_objects
    /// afterwards. This is honest contract — no fake promises about state.
    /// </summary>
    public sealed class ExecCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string BackupsFolderName = "backups";
        private const string InputFileName  = "exec_input.json";
        private const string OutputFileName = "exec.json";

        [CommandMethod("CLAUDE_EXEC")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null) return;

            var bridgeDir  = Path.Combine(Path.GetTempPath(), BridgeFolderName);
            Directory.CreateDirectory(bridgeDir);
            var inputPath  = Path.Combine(bridgeDir, InputFileName);
            var outputPath = Path.Combine(bridgeDir, OutputFileName);

            ExecInput input;
            try
            {
                input = ReadInput(inputPath);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, kind: null, command: null, confirm: false,
                    $"failed to read input ({inputPath}): {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_EXEC: bad input — {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(input.Command))
            {
                WriteError(outputPath, input.Kind, input.Command, input.Confirm,
                    "command/code is empty");
                editor.WriteMessage("\nCLAUDE_EXEC: empty payload");
                return;
            }

            string wrapped = WrapWithUndoGroup(input.Kind, input.Command);

            // -------- dry-run -------------------------------------------------
            if (!input.Confirm)
            {
                var docFilename = document.Database?.Filename ?? string.Empty;
                var backupHint  = string.IsNullOrEmpty(docFilename)
                    ? "(unavailable — document is unsaved; you must save before confirm=true)"
                    : ProjectedBackupPath(bridgeDir, docFilename);

                WritePlan(outputPath, input, wrapped, backupHint, docFilename);
                editor.WriteMessage($"\nCLAUDE_EXEC: dry-run — plan written ({input.Kind})");
                return;
            }

            // -------- confirm=true: backup → wrap → dispatch -----------------
            string docFilenameC = document.Database?.Filename ?? string.Empty;
            if (string.IsNullOrEmpty(docFilenameC))
            {
                WriteError(outputPath, input.Kind, input.Command, input.Confirm,
                    "document has no on-disk filename — save the drawing first, then retry. "
                    + "Backup cannot be created for an unsaved drawing.");
                editor.WriteMessage("\nCLAUDE_EXEC: refusing — document is unsaved");
                return;
            }

            string backupPath;
            try
            {
                backupPath = CreateBackup(bridgeDir, document.Database, docFilenameC);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, input.Kind, input.Command, input.Confirm,
                    $"backup failed — refusing to execute: {ex.Message}");
                editor.WriteMessage($"\nCLAUDE_EXEC: backup failed — {ex.Message}");
                return;
            }

            // Dispatch on the main thread (this command IS main thread already —
            // SendStringToExecute is safe from inside a CommandMethod).
            try
            {
                document.SendStringToExecute(wrapped, true, true, true);
            }
            catch (System.Exception ex)
            {
                WriteError(outputPath, input.Kind, input.Command, input.Confirm,
                    $"dispatch failed: {ex.Message} (backup is at {backupPath})");
                editor.WriteMessage($"\nCLAUDE_EXEC: dispatch failed — {ex.Message}");
                return;
            }

            WriteCommitted(outputPath, input, wrapped, backupPath, docFilenameC);
            editor.WriteMessage(
                $"\nCLAUDE_EXEC: dispatched ({input.Kind}); backup at {backupPath}");
        }

        // ---- input parsing ---------------------------------------------------

        private sealed class ExecInput
        {
            public string Kind    { get; init; } = "command";
            public string Command { get; init; } = string.Empty;
            public bool   Confirm { get; init; }
        }

        private static ExecInput ReadInput(string path)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("exec_input.json not found");
            }
            var raw = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? (k.GetString() ?? "command")
                : "command";
            if (kind != "command" && kind != "lisp")
            {
                throw new InvalidOperationException(
                    $"kind must be 'command' or 'lisp', got '{kind}'");
            }

            var command = root.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? (c.GetString() ?? string.Empty)
                : string.Empty;

            var confirm = root.TryGetProperty("confirm", out var cf)
                && cf.ValueKind == JsonValueKind.True;

            return new ExecInput { Kind = kind, Command = command, Confirm = confirm };
        }

        // ---- UNDO grouping ---------------------------------------------------

        /// <summary>
        /// Wraps the user payload for safe dispatch.
        ///
        /// HISTORICAL NOTE (step 7, after a live smoke run on NanoCAD 24.1):
        /// the original implementation wrapped command payloads in
        /// `_UNDO _BEGIN ... _UNDO _END` to make the whole operation collapse to
        /// a single Ctrl+Z step. This was based on the AutoCAD syntax.
        /// NanoCAD 24.1 does NOT implement `UNDO BEGIN/END/GROUP` at the
        /// command-line level — typing `_UNDO` in the NanoCAD console just runs
        /// a single-step undo (no sub-options prompt). The wrapping was therefore
        /// a no-op in the best case and a parser corruption in the worst (the
        /// stray `._UNDO` tokens leaked into the next active command as an
        /// argument; see the smoke log).
        ///
        /// What we do instead:
        /// - For `kind=command`: send the payload verbatim. NanoCAD's UNDOCTL
        ///   auto-group mode (bit 2 of UNDOCTL=53 confirmed live) does some
        ///   automatic grouping; the exact rules are not documented and not
        ///   relied on here. The user's safety net is the File.Copy backup.
        /// - For `kind=lisp`: keep the `(vl-catch-all-apply ...)` wrapper. It
        ///   does NOT touch undo, but it traps LISP errors so a bad expression
        ///   doesn't leave NanoCAD's command line dangling waiting for input.
        ///
        /// Trailing newline is still required so SendStringToExecute treats the
        /// buffer as terminated.
        ///
        /// Tracked as HEALTH item: "NanoCAD 24.1 has no manual undo-grouping
        /// API at command-line or in the managed Database/Document surface
        /// (probe found only IsUndoing / UndoFiler / DisableUndoRecording —
        /// no Begin/Mark/Group counterpart)."
        /// </summary>
        private static string WrapWithUndoGroup(string kind, string payload)
        {
            if (kind == "lisp")
            {
                // vl-catch-all-apply catches LISP errors; outer (princ) suppresses
                // the "nil" echo at the end. No UNDO grouping — see note above.
                // LISP eval terminates on the closing paren — a trailing newline
                // here just yields the eval result, no command-repeat side-effects.
                return
                    "(progn (vl-catch-all-apply (function (lambda () "
                    + payload + "))) (princ))\n";
            }

            // kind == "command".
            // Each whitespace character in a command-string buffer is treated by
            // NanoCAD as <Enter>. A LONE <Enter> in the command line means
            // "repeat last command" — empirically confirmed during step 7 smoke:
            // a payload of "_ZOOM E " followed by "\n" became "_ZOOM<Enter>E<Enter><Enter>",
            // which re-triggered the ZOOM command after it had already completed.
            // The re-triggered ZOOM then swallowed the next CLAUDE_EXEC as its
            // scale argument, producing 504 timeouts and "Некорректный формат
            // масштаба" errors.
            // Fix: ensure exactly ONE trailing terminator (a single space, which
            // = <Enter>). Do not append \n. If the caller already ended the
            // payload with a space or newline, don't add another one.
            if (payload.EndsWith(" ") || payload.EndsWith("\n"))
            {
                return payload;
            }
            return payload + " ";
        }

        // ---- backup ----------------------------------------------------------

        private static string ProjectedBackupPath(string bridgeDir, string docFilename)
        {
            var backupsDir = Path.Combine(bridgeDir, BackupsFolderName);
            var baseName   = Path.GetFileNameWithoutExtension(docFilename);
            // Millisecond precision: two consecutive exec calls (e.g. back-to-back
            // smoke runs) can land in the same second, and File.Copy(overwrite:false)
            // would then collide. ms is enough disambiguation for the foreseeable
            // call rates; if exec ever runs in tight loops, add a counter suffix.
            var stamp      = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            return Path.Combine(backupsDir, $"{stamp}_{baseName}.dwg");
        }

        private static string CreateBackup(string bridgeDir, Database database, string docFilename)
        {
            var backupPath = ProjectedBackupPath(bridgeDir, docFilename);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

            // NOTE on the backup mechanism (step 7, decided after a smoke regression):
            // We DO NOT use Database.SaveAs here. In NanoCAD 24.1 Teigha,
            // Database.SaveAs writes the new file AND mutates Database.Filename to
            // point at the new path — which silently re-aliases the user's open
            // document to the backup file. Verified empirically during the first
            // smoke run; Database.Filename is a read-only property in this build,
            // so we cannot restore it after the fact.
            //
            // Instead we File.Copy the on-disk drawing. Trade-off: this captures
            // the LAST SAVED state, not the in-memory state. Any unsaved edits the
            // user has not yet Ctrl+S'd are NOT included in the backup. This is the
            // smaller of two evils — a corrupted Filename would be hard to detect
            // and could silently send the user's Ctrl+S into a temp folder.
            //
            // If `docFilename` doesn't exist on disk (document opened, then file
            // deleted externally) File.Copy throws — we let that bubble up so the
            // caller returns an error envelope and refuses to dispatch.
            File.Copy(docFilename, backupPath, overwrite: false);

            return backupPath;
        }

        // ---- envelope writers ------------------------------------------------

        private static void WritePlan(
            string outputPath, ExecInput input, string wrapped,
            string backupHint, string docFilename)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.NanoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                kind          = input.Kind,
                command       = input.Command,
                confirm       = false,
                executed      = false,
                plan          = new
                {
                    will_execute    = wrapped,
                    safety_model    = "File.Copy backup before dispatch; no manual undo grouping (NanoCAD 24.1 has no command-line BEGIN/END syntax — Ctrl+Z behaviour is whatever NanoCAD's UNDOCTL auto-grouping decides, not deterministic).",
                    backup_path_if_confirmed = backupHint,
                    document_filename = docFilename,
                    document_unsaved  = string.IsNullOrEmpty(docFilename)
                },
                warnings = string.IsNullOrEmpty(docFilename)
                    ? new[] { "document has no on-disk filename — backup cannot be created; save the drawing before sending confirm=true" }
                    : new[] { "backup captures the LAST SAVED state of the drawing; any unsaved edits will not be in the backup. To include them, save (Ctrl+S) before sending confirm=true." },
                error = (string?)null
            };
            Write(outputPath, payload);
        }

        private static void WriteCommitted(
            string outputPath, ExecInput input, string wrapped,
            string backupPath, string docFilename)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.NanoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                kind          = input.Kind,
                command       = input.Command,
                confirm       = true,
                executed      = true,
                wrapped_command = wrapped,
                backup_path     = backupPath,
                document_filename = docFilename,
                note = "command dispatched to NanoCAD command queue; SendStringToExecute is "
                     + "asynchronous, so the drawing may not yet reflect the change at the moment "
                     + "this response is read. Verify results with get_view / query_objects / "
                     + "get_document_summary on the next call.",
                warnings = new[] { "backup captures the LAST SAVED state of the drawing; any in-memory unsaved edits at the time of confirm are NOT in this backup." },
                error = (string?)null
            };
            Write(outputPath, payload);
        }

        private static void WriteError(
            string outputPath, string? kind, string? command, bool confirm, string message)
        {
            var payload = new
            {
                plugin        = "ClaudeBridge.NanoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                kind          = kind,
                command       = command,
                confirm       = confirm,
                executed      = false,
                error         = message,
                warnings      = System.Array.Empty<string>()
            };
            try { Write(outputPath, payload); } catch { /* last-ditch */ }
        }

        private static void Write(string path, object payload)
        {
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
