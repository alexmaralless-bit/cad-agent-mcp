---
name: check-drawing-health
description: Diagnose common pathologies in an AutoCAD drawing — sentinel-extents geometry, mis-coordinated outliers, frozen-layer leftovers, dead layers, orphan BlockReferences. Triggers on requests like "проверь чертёж", "найди проблемы", "что в чертеже не так", "audit the drawing", "check drawing health". Read-only — never modifies geometry; optionally highlights found issues visually so the user can see where they are.
---

# check-drawing-health

You are running a **read-only diagnostic pass** on the user's active AutoCAD drawing. Your job is to find common pathologies that distort views, break tools, or quietly bloat the file, then report them — and if the user wants, highlight the offending entities visually with `overlay_highlight` so they can be located by eye.

You do **not** fix anything. Some "issues" (frozen layers, dead layers) may be intentional. The skill outputs a report and the user decides what to do.

## When to use

User says something like:
- "проверь чертёж" / "найди проблемы" / "что в чертеже не так"
- "audit the drawing" / "check drawing health" / "what's wrong with this file"
- "почему не работает zoom extents" / "почему чертёж выглядит пустым"

Don't trigger on:
- A specific edit task ("удали слой X") — this skill diagnoses, doesn't fix
- A pure-content question ("сколько труб на чертеже") — that's `query_objects`, not health

## Prerequisites — verify first

Run `healthz`. If the bridge is not up, stop and tell the user: *"AutoCAD не запущен или плагин не загружен — запустите AutoCAD 2020 с подключённым ClaudeBridge.AutoCad (NETLOAD)."*

Run `get_document_summary` and **keep the response** — every subsequent check uses it:
- `document.extents` — for the mis-coordinated outlier check
- `layers[]` with `name`, `is_frozen`, `is_off` — for the frozen-layer and dead-layer checks
- `blocks[]` with `name` — for the orphan-BlockReference check
- `entity_counts_model_space` — total entity count (helps you decide whether `query_objects` will truncate)

If `entity_counts_model_space` totals > 9000, warn the user that the audit will be approximate because the per-entity scan caps at 10000 results.

## Workflow

### Step 1: pull all model-space entities once

Call `query_objects` with `max_results: 10000` and no other filter. This returns every entity with its handle, type, layer, block_name (for BlockReference), bounds. **One bulk call, then run all the checks below in-memory** — don't make N round-trips per check.

Note `truncated: true` in the response if it appeared. If so, report at the end that the audit covered N of M total entities.

### Step 2: check sentinel-extents geometry

For each entity in the response, flag it if **any** coordinate of `bounds.min` or `bounds.max` has absolute value > 1e19. These are entities AutoCAD reports as living at infinity — usually attribute-definition residue, broken text inserts, or import artifacts.

Why it matters: such entities break `zoom_extents` (the view zooms out to infinity), and `overlay_highlight` skips them with `reason: "sentinel extents (±1e20)"`. They are functionally invisible but burn through tool cycles.

Collect: list of {handle, type, layer}. Report count + first 5 handles.

### Step 3: check mis-coordinated huge entities

For each entity whose bounds are NOT sentinel, compute `bound_size = max(bounds.max - bounds.min)` (largest XYZ dimension). Compare to the **document extents diagonal** `D = sqrt((extents.max.x - extents.min.x)^2 + (extents.max.y - extents.min.y)^2)`.

Flag entities where `bound_size > 5 × D`. These are geometry items bigger than the *whole drawing's diagonal* by 5× or more — almost always import-glue lines or dimscale residue. They distort `zoom_extents` by pulling the visible area way out.

Collect: list of {handle, type, layer, bound_size, ratio = bound_size/D}. Report count + first 5 with their ratios.

### Step 4: check frozen-layer (or off-layer) entities

From the summary, build a set of layer names where `is_frozen == true` OR `is_off == true`. Then for each entity in the bulk query, flag those whose `layer` ∈ this set.

This is **informational, not necessarily a bug** — frozen layers often hold intentionally hidden content. But if a user is confused why entities aren't showing or being included by tools, this report tells them how much is hidden and where.

Collect: count per (layer_name, frozen_or_off). Report compact `frozen N · "LAYER_A" 12 · "LAYER_B" 5` etc.

### Step 5: check dead layers

Tally distinct `layer` values from the bulk query → this is the set of layers with at least one entity. Compare to the LayerTable from the summary. Any layer in `summary.layers[]` not in the tally has **zero entities** — a dead layer, just a row in the LayerTable.

Common cause: layers copied from a template that were never used in this drawing.

Report names + count. **Do not** flag the special layers `0`, `Defpoints`, or anything starting with `CLAUDE_` (those are infrastructure layers; they may be empty by design).

### Step 6: check orphan BlockReferences

For each BlockReference in the bulk query, look up its `block_name` in the summary's `blocks[]` list. If the name is **not found**, that BlockReference points at a block definition that has been deleted or was never present — an orphan.

This is rare but serious — AutoCAD will sometimes still render orphans, sometimes throw errors when you select them.

Collect: list of {handle, block_name}. Report.

### Step 7: format the report

Emit a structured Markdown report:

```
## Drawing health: <document.name>

Total entities scanned: <N> (of <M> in model space; truncated: <bool>)

| Check                        | Result               |
|------------------------------|----------------------|
| Sentinel-extents geometry    | ⚠ 3 entities  | ✓ none |
| Mis-coordinated outliers     | ⚠ 1 entity (1880× extents) | ✓ none |
| Frozen / off-layer content   | ℹ 47 entities on 3 layers | ✓ none |
| Dead layers (no entities)    | ℹ 5 layers | ✓ none |
| Orphan BlockReferences       | ⚠ 2 entities | ✓ none |
```

Use ⚠ for actual pathologies (sentinel, outliers, orphans), ℹ for informational findings (frozen, dead), ✓ for clean.

After the table, expand each non-empty section with **first 5 example handles + types + layer names**. For dead layers, list the layer names.

### Step 8: offer visual highlight

If any of the ⚠ checks found entities, ask:

> *"Подсветить найденные проблемные сущности (sentinel + outliers + orphans) на снэпшоте? Бывает легче увидеть, где они физически висят, чем читать список handles."*

If the user says yes, build a deduplicated handles list (cap at 50 — `overlay_highlight` accepts up to 100, but past ~50 the labels get crowded), call `overlay_highlight` with all the handles, then `camera_zoom_extents`, then `get_view`. Note that sentinel-extents entities will appear in the `skipped` field of the highlight response — that's expected; you tell the user *"sentinel-extents сущности нельзя обвести (у них нет видимых границ), они просто перечислены выше"*.

Do **not** ask about highlighting if all ⚠ checks were clean — there's nothing visible to highlight.

## Rollback

This skill never mutates the drawing — no rollback needed. All entities created by an optional `overlay_highlight` step live on `CLAUDE_OVERLAY` and are auto-cleared by the next `get_view`.

## Limits

- The bulk `query_objects` caps at 10000 results. On larger drawings the audit is approximate. The report says how many were scanned vs the total `entity_counts_model_space` sum.
- The "huge outlier" check uses 5× document diagonal as the threshold. Legitimately oversized title-block frames in paper space might trip it; this skill scans **model space only** which usually avoids that.
- Orphan-BlockReference detection assumes the summary's `blocks[]` list is complete. Anonymous and layout blocks are excluded from that list by `CLAUDE_SUMMARY` design, so anonymous-block references won't be detected as orphans (they have block_name like `*U23` and are normally fine).
- "Dead layer" excludes `0`, `Defpoints`, and `CLAUDE_*`. If the user has their own infrastructure prefix they want excluded, they can ask and you adjust the filter in the report.

## Annex: composing with other overlay tools

If the user wants the health report alongside the visual reference frame, sequence:

```
overlay_hud → overlay_grid → overlay_highlight(problem_handles) → camera_zoom_extents → get_view
```

All four overlay categories live on `CLAUDE_OVERLAY` and get cleared together when the snapshot is captured.
