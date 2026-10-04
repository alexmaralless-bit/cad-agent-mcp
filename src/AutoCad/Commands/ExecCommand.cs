using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge.AutoCad.Commands
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
                // wrapUpInactiveDocument=false: AutoCAD-specific. See HttpBridgeService.
                document.SendStringToExecute(wrapped, true, false, true);
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
            var root = JObject.Parse(raw);

            var kTok = root["kind"];
            var kind = (kTok != null && kTok.Type == JTokenType.String)
                ? (kTok.Value<string>() ?? "command")
                : "command";
            if (kind != "command" && kind != "lisp")
            {
                throw new InvalidOperationException(
                    $"kind must be 'command' or 'lisp', got '{kind}'");
            }

            var cTok = root["command"];
            var command = (cTok != null && cTok.Type == JTokenType.String)
                ? (cTok.Value<string>() ?? string.Empty)
                : string.Empty;

            var cfTok = root["confirm"];
            var confirm = (cfTok != null && cfTok.Type == JTokenType.Boolean) && cfTok.Value<bool>();

            return new ExecInput { Kind = kind, Command = command, Confirm = confirm };
        }

        // ---- UNDO grouping ---------------------------------------------------

        /// <summary>
        /// Wraps the user payload for safe dispatch.
        ///
        /// Two concerns layered together:
        ///   1. UNDO grouping — wrap the payload in `_.UNDO _Begin / _.UNDO _End`
        ///      so a single Ctrl+Z in AutoCAD atomically reverts the whole
        ///      write-op batch (everything the user's command or LISP touched).
        ///      AutoCAD's command-line UNDO supports this since the 1990s; the
        ///      backup .dwg stays as a belt-and-suspenders fallback for cases
        ///      where the user can't reach Ctrl+Z (script-driven dispatch, etc.).
        ///   2. LISP error trapping — wrap user LISP in `vl-catch-all-apply` so
        ///      a malformed expression doesn't leave the command line dangling
        ///      waiting for input from a half-evaluated form.
        ///
        /// FORK NOTE (NanoCAD-side step 7, archived): the NanoCAD-side ancestor
        /// of this file forfeited UNDO grouping entirely because NanoCAD 24.1
        /// doesn't implement `_UNDO _Begin/_End` at the command line — typing
        /// `_UNDO` there just runs a single-step undo. AutoCAD does implement it
        /// properly, so we're restoring the grouping here.
        ///
        /// Trailing newline is required for LISP so SendStringToExecute treats
        /// the buffer as terminated; for command strings, exactly ONE trailing
        /// space (=Enter) is required and a second one would re-trigger "repeat
        /// last command", swallowing the next CLAUDE_EXEC payload — so we
        /// normalize to exactly one trailing space.
        /// </summary>
        private static string WrapWithUndoGroup(string kind, string payload)
        {
            if (kind == "lisp")
            {
                // (command "._UNDO" "_Begin") opens the undo group.
                // vl-catch-all-apply traps LISP errors so a bad expression
                // doesn't dangle. The matching "_End" runs even if the user's
                // code threw (because vl-catch-all-apply absorbs the throw).
                // Outer (princ) suppresses the "nil" echo at the end.
                return
                    "(progn"
                    + " (command \"._UNDO\" \"_Begin\")"
                    + " (vl-catch-all-apply (function (lambda () " + payload + ")))"
                    + " (command \"._UNDO\" \"_End\")"
                    + " (princ))\n";
            }

            // kind == "command".
            // Normalize trailing whitespace: each whitespace char in the
            // SendStringToExecute buffer is parsed by AutoCAD as <Enter>, and a
            // lone <Enter> means "repeat last command" — empirically confirmed
            // to cause regressions when a stray double-space slipped through.
            // So strip then re-add exactly one trailing space.
            var trimmed = payload.TrimEnd(' ', '\n', '\t', '\r');

            // Hybrid wrap: command-string for the user's command sandwiched between
            // two LISP (command "._UNDO" "_Begin/_End") calls. Why hybrid:
            //   Empirically (smoke-test 2026-05-20 in AutoCAD 2020 ru-RU) the pure
            //   command-string form `._UNDO _Begin <cmd> ._UNDO _End ` opens the
            //   Begin-group correctly (UNDOCTL bit 0x08 set) but doesn't make the
            //   whole batch a SINGLE undo step — Ctrl+Z still walks sub-operations.
            //   The LISP `(command ...)` form does behave atomically: one Ctrl+Z
            //   reverts the whole group. AutoCAD's command-line buffer is happy
            //   to interleave LISP expressions and raw command strings, so we
            //   wrap with LISP and put the raw command between them.
            return "(command \"._UNDO\" \"_Begin\") " + trimmed + " (command \"._UNDO\" \"_End\") ";
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

            // NOTE on the backup mechanism (NanoCAD-side step 7, regression-derived):
            // We DO NOT use Database.SaveAs here. Database.SaveAs writes the new
            // file AND mutates Database.Filename to point at the new path — which
            // silently re-aliases the user's open
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
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                kind          = input.Kind,
                command       = input.Command,
                confirm       = false,
                executed      = false,
                plan          = new
                {
                    will_execute    = wrapped,
                    safety_model    = "File.Copy backup of the on-disk drawing before dispatch (last-saved state). Wrapped command/LISP runs inside an explicit _.UNDO _Begin / _.UNDO _End group, so one Ctrl+Z (or one _.U) in AutoCAD atomically reverts the whole batch. Backup is the fallback when Ctrl+Z is unreachable (script-driven dispatch, crashed session, etc.).",
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
                plugin        = "ClaudeBridge.AutoCad",
                version       = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                timestamp_utc = DateTime.UtcNow.ToString("O"),
                kind          = input.Kind,
                command       = input.Command,
                confirm       = true,
                executed      = true,
                wrapped_command = wrapped,
                backup_path     = backupPath,
                document_filename = docFilename,
                note = "command dispatched to AutoCAD command queue, wrapped in _.UNDO _Begin/_End "
                     + "(one Ctrl+Z reverts the whole batch). SendStringToExecute is asynchronous, "
                     + "so the drawing may not yet reflect the change at the moment this response is "
                     + "read. Verify results with get_view / query_objects / get_document_summary "
                     + "on the next call.",
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
                plugin        = "ClaudeBridge.AutoCad",
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
                JsonConvert.SerializeObject(payload, Formatting.Indented));
        }
    }
}
