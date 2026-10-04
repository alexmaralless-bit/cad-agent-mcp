---
name: extract-pipe-spec-sheet
description: Build a parts specification (block-reference tally) for a pipeline route in an AutoCAD drawing — counts each block-symbol type that sits along a chosen route polyline. Triggers on requests like "сделай спецификацию", "посчитай фитинги на маршруте", "extract pipe spec sheet", "сколько каких блоков на трубе", "сделай ведомость по трубопроводу". Output is a grouped Markdown table plus a JSON block for tooling. Read-only — never modifies geometry.
---

# extract-pipe-spec-sheet

You are tallying **block references** (fittings, valves, supports, markers) that sit along a pipeline route the user identifies. Output is a parts list grouped by `block_name`, count, and a representative layer.

This skill *catalogues what's already there* along a route. Useful for procurement specs, drawing audits, and "did I miss a fitting" checks.

## When to use

User says something like:
- "сделай спецификацию [по этому маршруту]" / "посчитай фитинги" / "сколько каких блоков"
- "extract pipe spec sheet" / "tally block references along the route"
- "сделай ведомость" / "что у меня на трубе"

Don't trigger on:
- Requests asking to *create* fittings or insert blocks — this skill only counts.
- Requests for entities other than BlockReference (e.g. "сколько полилиний"). For non-block tallies use `query_objects` directly.
- Generic drawing audits — that's `check-drawing-health`.

## Prerequisites — verify first

Run `healthz`. If the bridge is not up, stop and tell the user: *"AutoCAD не запущен или плагин не загружен — запустите AutoCAD 2020 с подключённым ClaudeBridge.AutoCad (NETLOAD)."*

Run `get_document_summary`. Note `document.extents` for tolerance scaling and `blocks[]` for the block-definitions list (you'll reference it to verify any unexpected names).

## Workflow

### Step 1: find the route polyline

Ask the user which layer the pipe is on if they haven't said:

> "На каком слое лежит трубопровод, по которому считаем спецификацию?"

Run `query_objects` with `type: "Polyline"` and that layer.
- **0 hits**: ask for the right layer.
- **1 hit**: use it.
- **>1 hits**: pass all candidate handles to `overlay_highlight`, then `camera_zoom_extents` and `get_view`. Ask "Какой обведённый — нужный?" The user replies "первый" / "третий", you pick the matching handle. `overlay_highlight` annotations are auto-cleared by `get_view`, no manual cleanup needed.

### Step 2: pull the route's vertices

Build the path `<TEMP>/claude_bridge/route_probe.txt`, where `<TEMP>` is the host's `%TEMP%` written with forward slashes (the bridge already creates that directory). Dispatch via `run_lisp`, `confirm: true`:

```
(setq f (open "<TEMP>/claude_bridge/route_probe.txt" "w"))
(setq ent (entget (handent "<ROUTE_HANDLE>")))
(princ "{\"vertices\":[" f)
(setq first T)
(foreach pair ent
  (if (= (car pair) 10)
    (progn
      (if (not first) (princ "," f))
      (princ (strcat "[" (rtos (cadr pair) 2 6) "," (rtos (caddr pair) 2 6) "]") f)
      (setq first nil))))
(princ "]}" f)
(close f)
(princ)
```

After dispatch, read `%TEMP%\claude_bridge\route_probe.txt` with the `Read` tool. Parse the JSON `{"vertices":[[x,y],[x,y],...]}`.

(Polyline lightweight uses group code 10 with `(x . y)` cons-pair; the `cadr`/`caddr` handles both cons-pair and 3-element list forms safely with `rtos` formatting.)

### Step 3: compute the search band

From the vertices compute:
- `aabb_min = (min(x), min(y))`, `aabb_max = (max(x), max(y))` — axis-aligned bounding box of the polyline.
- `route_length` ≈ sum of segment lengths (for reporting).
- `route_diag` = sqrt((max.x-min.x)² + (max.y-min.y)²).

Pick a tolerance:
- If the user gave one explicitly, use it.
- Default: **0.5% of route_diag**, clamped to [50, 1000] drawing units. For typical Russian gas-pipe drawings in millimetres that's 50–1000 mm around the centerline, which captures fittings normally snapped to the pipe.
- Tell the user the tolerance you picked: *"Использую полосу поиска ±N единиц от bounding-box маршрута. Если фитинги стоят дальше — увеличь tolerance."*

Expanded AABB: `band_min = aabb_min - (tol, tol)`, `band_max = aabb_max + (tol, tol)`.

### Step 4: pull every BlockReference

`query_objects` with `type: "BlockReference"`, `max_results: 10000`. If `truncated: true`, warn the user that the tally is approximate and ask if they want to narrow by layer.

### Step 5: filter by the search band

For each BlockReference in the result, check whether `position.x ∈ [band_min.x, band_max.x]` and `position.y ∈ [band_min.y, band_max.y]`. Keep only those inside.

This is an **AABB filter** — fast but coarse: for an L-shaped polyline it includes blocks in the "inside corner" that aren't actually near a segment. Document this in the report (see Limits). For real-world gas-pipe routes which run roughly linearly, the AABB is tight to the polyline and the filter is accurate enough for MVP procurement specs.

### Step 6: group by block_name

Tally `Map<block_name, { count, sample_layer, sample_handle }>`. Sort descending by count. Use the **first** entity per block_name as the "sample" (its layer + handle).

Cross-check each `block_name` against `summary.blocks[]` — if missing from the summary list, that's an anonymous or system block; tag it `(anonymous)` in the table for clarity.

### Step 7: emit the report

Markdown table first (for the engineer), JSON block second (for tooling / Cowork pipelines that consume the structure).

```
## Спецификация блоков: маршрут <ROUTE_HANDLE>

Длина маршрута: ~N единиц, M вершин
Полоса поиска: AABB ± T единиц (приблизительный — см. Limits)
Всего в полосе: K BlockReferences

| Block name   | Count | Sample layer |
|--------------|------:|--------------|
| g206_2       |    18 | ГАЗОПРОВОД   |
| g117kb       |     7 | КАНАЛИЗАЦИЯ  |
| g108a        |     4 | ОПОРЫ        |
| ...          |   ... | ...          |

​```json
{
  "route_handle": "...",
  "route_length": <N>,
  "vertex_count": <M>,
  "tolerance": <T>,
  "total_in_band": <K>,
  "groups": [
    { "block_name": "g206_2", "count": 18, "sample_layer": "ГАЗОПРОВОД", "sample_handle": "19BB79" },
    ...
  ]
}
​```
```

(The triple-backtick JSON block lets a downstream skill / agent re-consume the spec without re-parsing the Markdown table.)

### Step 8: offer a visual sanity check

If `total_in_band` is non-zero, ask:

> *"Показать на снэпшоте полосу с подсвеченными блоками (markers)? Полезно убедиться, что AABB-фильтр не выловил лишнего."*

If yes, build a handles list of up to **30** representative blocks (top-3 from each group; cap total at 30 to keep markers readable). Call:
1. `overlay_markers` with `anchored: true, handles: <those 30>` — anchored corners give the spatial frame, markers pinpoint each block at its insertion point.
2. `camera_zoom_extents`
3. `get_view`

The user sees the route region with #1..#N marker crosses over the counted fittings, anchored to TL/TR/BL/BR for orientation. Annotations auto-clear by `get_view`.

## Rollback

Read-only skill — nothing to roll back. The `run_lisp` call in Step 2 writes a temp file but does not mutate the drawing; `confirm: true` is required by the safety pipeline regardless.

## Limits

- **AABB filter is approximate.** For straight or near-straight gas-pipe routes the AABB hugs the polyline and the filter is accurate. For L-shaped or U-shaped routes the AABB includes the "inside" of the bend — block references inside that area but actually 50+ m from any segment get falsely counted. To do an exact per-segment distance filter, dispatch `vlax-curve-getclosestpointto` per candidate via `run_lisp` and read the distance back — TBD as a follow-up if users need the precision.
- **BlockReference position only**, not the full block geometry. A 5 m block whose insertion point is just outside the AABB but whose geometry extends across the route line will not be counted. Mention this if the user asks why a visually-obvious fitting is missing from the tally.
- **Anonymous blocks** (names starting with `*`) are usually AutoCAD's anonymous-block instances or copy-paste artifacts. They are reported but tagged so the user can decide whether to include them in the procurement spec.
- **Max 10000 BlockReferences scanned** in one query. Larger drawings get a "truncated" warning in the report.

## Annex: tolerance heuristics by drawing unit

| Drawing unit | Typical tolerance |
|---|---|
| Millimetres (most Russian gas drawings) | 200–500 mm |
| Metres (some survey-aligned drawings) | 0.2–0.5 m |
| Sub-unit (CAD design drawings) | 0.005–0.05 |

The default 0.5% of route diagonal scales correctly across all of these.
