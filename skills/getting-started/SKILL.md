---
name: getting-started
description: Onboarding skill for the autocad-bridge Cowork plugin — first-run handshake with the AutoCAD HTTP bridge, recipe for the first useful snapshot, and a triage flow when something goes wrong. Triggers on first-time users ("how do I use this plugin", "how to start", "with what do I begin", "как начать", "как пользоваться"), on bridge-unreachable errors ("bridge unreachable", "ECONNREFUSED", "127.0.0.1:7890", "tool failed"), and on requests that mention installing or configuring the plugin. Read-only — never modifies the drawing.
---

# getting-started

You are walking a new user through their first successful interaction with the `autocad-bridge` Cowork plugin. The plugin sits between Claude (in Cowork) and a running AutoCAD 2020 session on the user's own machine; if the chain is intact the user sees `healthz: ok` and a working snapshot, if not the user sees `bridge unreachable` or a 5xx and gets confused.

Your job: confirm the chain is up, ship the user a working `get_view` snapshot of their active drawing, and — only if a step fails — diagnose where the chain broke.

## When to use

- User says: "как начать", "how do I use this plugin", "что я могу делать", "с чего начать", "how to start".
- Any tool call returned `bridge unreachable`, `ECONNREFUSED 127.0.0.1:7890`, `ETIMEDOUT`, or `fetch failed`.
- User asks about installation or configuration of the plugin.
- User says the snapshot came back empty/black or the drawing isn't what they expected (this is usually a viewport problem, not a bridge problem — Step 4 covers it).

Don't trigger on:
- A specific working task ("посчитай блоки", "проверь чертёж") — let the specialised skills (`extract-pipe-spec-sheet`, `check-drawing-health`) take it. Only run `getting-started` if those skills themselves report a handshake failure.

## Step 1 — handshake

Call `healthz` once. Three possible outcomes:

1. **`{"status": "ok", ...}`** — bridge is up. Tell the user briefly: *"Мост к AutoCAD на связи, версия плагина <X>, активный документ <name>. Что хотите посмотреть?"*. Skip to Step 2.
2. **`bridge unreachable` / `ECONNREFUSED`** — there is no listener on `127.0.0.1:7890`. The AutoCAD `.NET` plugin is not loaded (or the HTTP listener is stopped). Go to Step 3 (host-side triage).
3. **HTTP 5xx or unexpected JSON** — the listener exists but is faulting. Go to Step 3, branch B (plugin loaded but bridge sick).

## Step 2 — first useful snapshot

This is the canonical first-look recipe. Run **all four** in sequence; do not pause for confirmation between them — they are read-only and overlay annotations auto-clear after `get_view`:

1. `get_document_summary` — note `document.name`, `document.extents`, `entity_counts_model_space` total, count of `layers[]` and `blocks[]`. You'll cite these in the reply.
2. `overlay_hud` with a one-line caption like `"autocad-bridge · <document.name> · entities=<N>"`.
3. `camera_zoom_extents` — guarantee the snapshot shows the whole drawing, not the user's current zoom-in.
4. `get_view` — capture PNG. The overlay HUD is wiped from the live drawing automatically.

Then reply to the user with:
- The PNG.
- A 3-line summary citing document name, entity count, layer count, block-definition count, extents diagonal.
- A short menu of next moves the user can pick: *"Хотите аудит чертежа (`check-drawing-health`), спецификацию по маршруту (`extract-pipe-spec-sheet`), или изменить что-то через `execute_command` / `run_lisp` (тогда я загружу `safe-write-workflow`)?"*

## Step 3 — host-side triage when Step 1 fails

The chain is **AutoCAD 2020 → ClaudeBridge.AutoCad .NET plugin → HTTP listener on 127.0.0.1:7890 → Node MCP server bundled in this plugin → Claude.** A failure can be at any node. Walk the user through it from the AutoCAD side outward — the most common failure is the `.NET` plugin not being loaded.

### Branch A: bridge unreachable / ECONNREFUSED

Ask the user, one item at a time (don't dump the whole list — go step by step and wait):

1. **Is AutoCAD 2020 running on this machine, with a drawing open?** If not, ask them to launch it. The bridge does not run as a Windows service — it is hosted in-process by the `.NET` plugin.
2. **Has `ClaudeBridge.AutoCad.dll` been loaded into AutoCAD?** Tell them to type `NETLOAD` in the AutoCAD command line, browse to `artifacts\ClaudeBridge.AutoCad\current\ClaudeBridge.AutoCad.dll` in the repo (built by `scripts\autocad\build.ps1`), and load it. Then type `CLAUDE_PING` — if the command echoes "pong", the assembly is loaded.
3. **Is the HTTP listener bound?** They type `CLAUDE_HTTP_STATUS` in the AutoCAD command line. Expected: `listener is RUNNING on http://127.0.0.1:7890`. If it says STOPPED, they type `CLAUDE_HTTP_START`. The listener auto-starts at `PluginApp.Initialize`, but a previous `CLAUDE_HTTP_STOP` will leave it down.
4. **Firewall / antivirus blocking loopback?** Rare — but if their corporate AV intercepts localhost HTTP, the listener will start but Claude's fetch will fail. Have them run `curl http://127.0.0.1:7890/healthz` from the same machine in PowerShell; if curl works but Claude's tool call doesn't, the Node MCP subprocess is being blocked from outbound localhost, not the listener.

After each step, run `healthz` again. As soon as it returns `ok`, jump back to Step 2.

### Branch B: HTTP 5xx, malformed JSON, or timeouts

The listener is up but the request faulted server-side. Ask the user to:

1. Look at the AutoCAD command line — a `.NET` exception will have echoed there with stack trace.
2. Try `CLAUDE_HTTP_STOP` then `CLAUDE_HTTP_STATUS` then `CLAUDE_HTTP_START` — restarts the listener without reloading the assembly.
3. If that does not fix it, the workaround is to close and reopen AutoCAD, then `NETLOAD` the assembly again. The bridge does not survive a `Document::OnClose` cleanly in all cases — known issue.

## Step 4 — empty / black / unexpected snapshot

`healthz` is ok and `get_view` returned a PNG, but the user says it does not look like their drawing. Three common causes:

1. **The active document in AutoCAD is not the drawing they think.** Show them `document.name` from `get_document_summary` and ask if that is the file they expected. If they have multiple drawings open, AutoCAD's `Document.MdiActiveDocument` is whichever has focus — they may have tabbed away.
2. **Viewport is zoomed into empty space.** This is why Step 2 runs `camera_zoom_extents` before `get_view`. If you skipped it, redo with the camera step included.
3. **The whole drawing is on a frozen or off layer.** Run `check-drawing-health` — its "Frozen/off-layer content" check will surface this.

## Limits

- This skill assumes single-host topology. If the user is a non-host participant in a Cowork session, every tool call will fail with `bridge unreachable` and no host-side triage will fix it because there is no AutoCAD on their machine; tell them they need to be the host or have the host drive the conversation.
- The skill does not call any write tools (`execute_command`, `run_lisp`). If the user, after onboarding, asks to *change* the drawing, switch to `safe-write-workflow`.
