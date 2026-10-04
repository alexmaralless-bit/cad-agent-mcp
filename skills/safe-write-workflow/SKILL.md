---
name: safe-write-workflow
description: Two-phase safety contract for the hands tools (`execute_command`, `run_lisp`) in the autocad-bridge plugin. Triggers any time the user asks to modify the drawing — "удали слой X", "переименуй блок", "сдвинь маршрут", "delete the dimension layer", "rename block", "move polyline", "run this LISP", "execute command ERASE", "fix this", "fix the drawing", "change this" — and any time a tool call to `execute_command` or `run_lisp` is about to be made. Encodes: dry-run plan → user confirmation → auto-`.dwg` backup → execution → verification. Refuses to run on an unsaved drawing.
---

# safe-write-workflow

You are about to **modify the user's AutoCAD drawing**. Read this skill in full before any `execute_command` or `run_lisp` call — these tools mutate live state, and the user's protection comes entirely from the contract below.

The project's safety invariant (from the repo's CLAUDE.md): *dry-run by default, auto-backup .dwg before each write, write-ops refuse to run on an unsaved drawing*. This skill operationalises that.

## When to use

- User asks for any drawing modification: delete, move, rotate, scale, rename, replace, insert, purge, layer change, block-attribute edit, dimension edit, LISP eval, command-line command run, anything that ends in *"do it"* with a side effect.
- Anyone (skill or user) is about to call `execute_command` or `run_lisp`.

Don't trigger on:
- Read-only requests — `query_objects`, `get_document_summary`, `get_view`, `extract_so_blocks_from_route` all run without confirmation.
- Pure overlay calls — `overlay_*` writes to `CLAUDE_OVERLAY` only and is auto-cleared by the next `get_view`; treat them as transient annotations, not drawing modifications.

## Pre-flight checks

Before composing the plan, verify the three preconditions. If any fails, **stop and tell the user** — do not paper over.

1. **Bridge is up.** Call `healthz`. If it does not return `ok`, route to `getting-started` (Branch A) instead of trying to proceed.
2. **Drawing is saved to disk.** From `get_document_summary` check `document.path`. If it is empty / null / `Drawing1.dwg`-style untitled, refuse: *"Чертёж не сохранён на диск — write-операции отказываются работать без файла, потому что некуда писать резервную копию. Сохраните чертёж и повторите."* Do not call `execute_command` or `run_lisp` from this state.
3. **Drawing has a sane state.** If `entity_counts_model_space` is suspiciously high or low compared to what the user described, surface that as a sanity-check question before proceeding. Mismatched expectations are the most common reason post-hoc rollback gets requested.

## Phase 1 — dry-run plan

Compose a written plan with **all four** of these elements. Present it to the user and **wait for explicit confirmation** before continuing. Do not call `execute_command` / `run_lisp` yet.

1. **Intent in plain language.** One sentence: what will the drawing look like after this runs that it does not look like now?
2. **Exact payload.** The literal command string or LISP form that will be sent. Verbatim, in a fenced code block. No abbreviation, no `...`.
3. **Scope and affected entities.** Best-effort enumeration: how many entities, which layers, which blocks, which handles. If you cannot enumerate without running a `query_objects`, do it now (it is read-only) and cite the count in the plan.
4. **Backup name and rollback procedure.** State that an auto-`.dwg` backup will be written before the modification (the bridge does this, you do not need to script it), name the path it will land at (`%TEMP%\claude_bridge\backups\<UTC-timestamp>_<drawing>.dwg` — the dry-run plan returns the exact projected path), and tell the user the one-line rollback procedure: close the drawing without saving, rename the backup over the original.

The user must reply with explicit consent: *"да"*, *"ок, выполняй"*, *"go"*, *"yes"*. **Anything ambiguous** — *"наверное"*, *"может попробуем"*, *"а это безопасно"* — counts as *no*. Reply to the ambiguous answer by re-stating the plan more clearly and asking again. Do not interpret silence or topic change as consent.

If the user pushes back ("это не то, что я хотел"), revise the plan and re-confirm. The two-phase contract resets — you are back in Phase 1.

## Phase 2 — execution

Once the user has consented:

1. **Pre-snapshot.** Call `overlay_hud` with a caption like `"BEFORE: <intent summary>"`, `camera_zoom_extents`, `get_view`. Ship the PNG to the user. This is the visual diff baseline.
2. **Execute.** Call `execute_command` or `run_lisp` with the exact payload from the plan, passing `confirm: true`. The bridge writes the `.dwg` backup before sending the payload to AutoCAD — you do not need to manually trigger backup.
3. **Verify.** Call `get_document_summary` again. Diff the new totals against what you expected from the plan — entity count delta, layer count delta, block count delta. If the diff matches the intent: continue. If it does not match: **stop and tell the user**, recommend rollback, do not proceed with follow-on calls.
4. **Post-snapshot.** Call `overlay_hud` with `"AFTER: <intent summary>"`, `camera_zoom_extents`, `get_view`. Ship the PNG. The before/after pair lets the user see the change visually.

## Rollback procedure

The bridge writes the backup `.dwg`, but it does not auto-restore. To roll back the user must:

1. In AutoCAD, close the active drawing **without saving** (`CLOSE` → "Discard changes").
2. In Windows Explorer, locate the backup at `%TEMP%\claude_bridge\backups\<UTC-timestamp>_<drawing>.dwg`.
3. Rename the backup over the original (or open it directly via `OPEN`).

If the user already pressed `QSAVE` after the modification, the rollback `.dwg` is still on disk — they can still recover from it; they just need to open the backup `.dwg` and re-save under the original name.

Tell the user the backup path explicitly in Phase 1, so when something goes wrong they already know where to look.

## Common write patterns

These are the shapes of payload most users actually ask for. Use them as templates.

### Delete entities by handle (one or several)

Prefer `execute_command` with `ERASE` over LISP for this case — `ERASE` is well-understood and an entry in AutoCAD's undo stack will show "ERASE" rather than a custom LISP form.

```
ERASE (handent "H1") (handent "H2") ... ;
```

Plan line: *"удалю N сущностей по handles H1…HN с слоя L"*.

### Rename a block definition

```
(command "_RENAME" "Block" "OLDNAME" "NEWNAME")
```

Scope note in the plan: *"переименую один блок-определение, не трогая instances — все BlockReferences в чертеже автоматически начнут указывать на новое имя"*. List the count of BlockReferences that will pick up the rename, from `get_document_summary`.

### Purge unused block definitions

```
(command "_-PURGE" "B" "" "_N")
```

Plan must enumerate which block definitions will be purged. Cross-check with `query_objects` filtered by `block_name` to confirm each candidate has zero references — refuse if any candidate still has references (in which case `_-PURGE` is a no-op for it anyway, but flag the mismatch to the user).

### Move / rotate / scale by handle

Use `run_lisp` with a `vlax-ename->vla-object` workflow. Carefully form the payload; the dry-run plan must include the exact coordinates / angle / factor so the user can sanity-check the numbers.

## Refusal triggers (hard "no")

Refuse to run, even if the user insists:

- The drawing is unsaved (Pre-flight #2 fails).
- The user has not given explicit Phase 1 consent.
- The plan would `ERASE *` or otherwise wipe the model space without a per-entity enumeration. If they truly want to clear modelspace, they can do it manually in AutoCAD; the bridge does not need to be the conduit for that kind of bulk destruction.
- The payload references an external file path that contains user-visible secrets (passwords, tokens, etc.) — these will land in the `.dwg`'s undo / journal data.
- The user is in a Cowork session as a non-host participant and is asking *another* participant's AutoCAD to be modified. The single-host topology guarantees that does not happen, but if the request is phrased that way, surface the misunderstanding instead of running silently against the host.

## Composition with other skills

- After `check-drawing-health` finds ⚠ issues and the user says *"исправь это"* — this skill takes over for the fixing pass.
- After `extract-pipe-spec-sheet` produces a tally and the user says *"замени все блоки X на Y"* — this skill takes over for the renaming pass.
- For pure inspection follow-ups (*"сколько ещё таких"*, *"покажи мне их"*), do **not** enter this skill — those are `query_objects` + `overlay_highlight`, no write involved.

## Limits

- The bridge writes one backup `.dwg` per write operation, named with a UTC timestamp. There is no automatic cleanup — backup files accumulate in `%TEMP%\claude_bridge\backups\`. Mention this to the user the first time you run a write operation in a session.
- Backup is byte-equivalent at the moment of write, not a full `.bak`-with-history. Multiple writes in a session produce multiple backups; rollback uses the most recent **pre-mutation** one, which is the second-to-last by timestamp if a mutation just ran.
- The `verify` step (Phase 2.3) compares `get_document_summary` totals only. It does not catch silent successes — e.g. a `RENAME` that succeeded against a block name typo (renaming nothing) returns `confirm: true` with no error. Always include the expected delta in the plan and check it explicitly.
