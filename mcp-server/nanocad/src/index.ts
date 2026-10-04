#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { promises as fs } from "node:fs";
import { z } from "zod";
import { fetchBridge, postBridge, BRIDGE_URL } from "./bridge.js";

const server = new McpServer({
  name: "mcp-nano-bridge",
  version: "0.5.0",
});

server.tool(
  "healthz",
  "Sanity check: returns plugin status JSON from the NanoCAD HTTP bridge. Use this first if other tools fail.",
  {},
  async () => {
    const text = await fetchBridge("/healthz");
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "get_document_summary",
  "Returns a JSON summary of the active NanoCAD drawing: layers (name, color, on/off, locked), blocks, entity counts by type, model extents. Read-only.",
  {},
  async () => {
    const text = await fetchBridge("/summary");
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "get_view",
  [
    "Render the active NanoCAD viewport to PNG and return it as an image. Captures the document window (PrintWindow primary, BitBlt fallback). Returns the image plus a JSON metadata text block (capture method, dimensions, source, overlay-cleanup count).",
    "",
    "Overlay annotations are transient: any entities on the `CLAUDE_OVERLAY` layer (HUD, bbox highlights, future grid/markers/scale-bar) are wiped from the drawing immediately AFTER the PNG is captured. The captured image still shows them; the live drawing does not. The metadata's `overlay.cleared_count` reports how many entities were removed.",
    "",
    "Implication for callers: to get an annotated snapshot, set up the overlay (e.g. `overlay_highlight` + `overlay_hud`) → `camera_zoom_*` → `get_view`. No manual `overlay_clear` needed afterwards. If you want a SECOND annotated snapshot, re-draw the overlay first — it was already cleaned up by the previous `get_view`.",
  ].join("\n"),
  {},
  async () => {
    const sidecarText = await fetchBridge("/snapshot");
    let sidecar: { png?: { path?: string } };
    try {
      sidecar = JSON.parse(sidecarText);
    } catch (e) {
      throw new Error(`snapshot.json is not valid JSON: ${(e as Error).message}; body=${sidecarText.slice(0, 200)}`);
    }
    const pngPath = sidecar.png?.path;
    if (!pngPath) {
      throw new Error(`snapshot.json missing 'png.path' field; body=${sidecarText.slice(0, 200)}`);
    }
    const png = await fs.readFile(pngPath);
    const base64 = png.toString("base64");
    return {
      content: [
        { type: "image", data: base64, mimeType: "image/png" },
        { type: "text", text: sidecarText },
      ],
    };
  },
);

server.tool(
  "probe",
  "Reflective dump of the managed Teigha API surface available in the current NanoCAD build. Diagnostic / scouting tool — large text response. Use only when investigating what APIs exist.",
  {},
  async () => {
    const text = await fetchBridge("/probe");
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "execute_command",
  [
    "Execute a NanoCAD command-line command in the active drawing. WRITE OPERATION — two-phase safety contract:",
    "",
    "  Phase 1 (PLAN): call with confirm=false (or omit). NOTHING IS EXECUTED. You receive a plan envelope showing the exact wrapped command that would run, where the auto-backup would be created, and any warnings. You MUST show this plan to the user and get their explicit yes/no before the second call.",
    "",
    "  Phase 2 (COMMIT): call with confirm=true and the SAME 'command' string. The plugin creates an auto-backup .dwg, then dispatches the command via SendStringToExecute. NOTE: NanoCAD 24.1 does NOT support `UNDO BEGIN/END` at the command line, so Ctrl+Z is NOT guaranteed to revert the whole operation in one step. Rollback safety lives in the backup file, not in the undo stack.",
    "",
    "Important: SendStringToExecute is asynchronous — the dispatched command runs after the call returns. The committed response says 'dispatched', not 'completed'. Verify results with get_view / query_objects / get_document_summary on the next call.",
    "",
    "If the document has no on-disk filename (new/unsaved drawing), confirm=true will be refused with an error: the user must save the drawing first.",
    "",
    "Backup semantics: the auto-backup is a File.Copy of the on-disk drawing — it captures the LAST SAVED state, NOT in-memory unsaved edits. If the user has unsaved changes they care about, tell them to Ctrl+S before sending confirm=true.",
    "",
    "Examples of 'command' string: '_LINE 0,0 10,0 ', '_CIRCLE 5,5 2 ', '_ZOOM E '. Prefer underscore-prefixed command names — they work regardless of NanoCAD UI locale.",
  ].join("\n"),
  {
    command: z.string().min(1).describe("The NanoCAD command-line string to execute. Underscore-prefixed names recommended (e.g. '_LINE', '_CIRCLE')."),
    confirm: z.boolean().optional().describe("false (default) = dry-run, returns plan only. true = create backup + wrap in UNDO + dispatch. Always call with confirm=false first."),
  },
  async (args) => {
    const text = await postBridge("/exec", {
      kind: "command",
      command: args.command,
      confirm: args.confirm ?? false,
    });
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "run_lisp",
  [
    "Run a LISP expression in the active drawing. WRITE OPERATION — same two-phase contract as execute_command:",
    "",
    "  Phase 1 (PLAN, confirm=false): returns plan, executes nothing. Show user, get confirmation.",
    "  Phase 2 (COMMIT, confirm=true): auto-backup .dwg, wrap your code in (vl-catch-all-apply ...) to trap LISP errors, dispatch. NOTE: there is no manual undo-grouping — NanoCAD 24.1 doesn't expose one. Rollback safety = backup file, not Ctrl+Z.",
    "",
    "Prefer run_lisp over execute_command when you need non-interactive execution: LISP doesn't pause for user prompts and a vl-catch-all-apply wrapper catches errors instead of dangling the command line.",
    "",
    "The 'code' must be a valid LISP s-expression. Examples:",
    "  (princ \"hello\")",
    "  (command \"._LINE\" '(0 0) '(10 0) \"\")",
    "  (setq lst (list 1 2 3)) (princ lst)",
    "",
    "Asynchronous dispatch (same as execute_command) — verify results after.",
    "",
    "Backup semantics: File.Copy of the on-disk drawing — captures LAST SAVED state, not in-memory unsaved edits. Tell the user to Ctrl+S first if they have unsaved changes worth preserving.",
  ].join("\n"),
  {
    code: z.string().min(1).describe("LISP s-expression(s) to evaluate. The plugin wraps your code in (vl-catch-all-apply ...) inside an UNDO group."),
    confirm: z.boolean().optional().describe("false (default) = dry-run; true = backup + execute. Always plan first."),
  },
  async (args) => {
    const text = await postBridge("/exec", {
      kind: "lisp",
      command: args.code,
      confirm: args.confirm ?? false,
    });
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "camera_zoom_extents",
  [
    "Zoom the active NanoCAD viewport to drawing extents (auto-fit). View-only operation — does NOT modify the drawing.",
    "",
    "Use this to get an overview of the whole drawing before drilling into a specific region with camera_zoom_window.",
    "",
    "Asynchronous dispatch like all command-line ops — call get_view next if you need to confirm the new framing.",
  ].join("\n"),
  {},
  async () => {
    const text = await postBridge("/camera", { op: "extents" });
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "camera_zoom_window",
  [
    "Zoom the active NanoCAD viewport to a specific rectangle in DRAWING (not screen) coordinates. View-only operation — does NOT modify the drawing.",
    "",
    "Use this after get_document_summary (which gives the model extents) or query_objects (which gives per-entity bounds) to frame a specific area before calling get_view.",
    "",
    "Example: to focus on a region around (2210000, 510000) at ~200×200 unit detail level, pass min={x: 2209900, y: 509900}, max={x: 2210100, y: 510100}.",
    "",
    "Asynchronous dispatch — get_view next if you need to see the result.",
  ].join("\n"),
  {
    min: z.object({
      x: z.number().describe("Minimum X (drawing units)"),
      y: z.number().describe("Minimum Y (drawing units)"),
    }).describe("Lower-left corner of the zoom rectangle."),
    max: z.object({
      x: z.number().describe("Maximum X (drawing units)"),
      y: z.number().describe("Maximum Y (drawing units)"),
    }).describe("Upper-right corner of the zoom rectangle."),
  },
  async (args) => {
    const text = await postBridge("/camera", {
      op: "window",
      min: args.min,
      max: args.max,
    });
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "overlay_highlight",
  [
    "Draw numbered red bounding boxes around a list of entities, on the dedicated `CLAUDE_OVERLAY` layer. The annotations appear in subsequent `get_view` snapshots. View-only operation — does NOT modify user geometry.",
    "",
    "Primary use case: when `query_objects` returns multiple ambiguous candidates (e.g., 4 polylines on the same layer), instead of asking the user to identify them by handle, call this with all candidate handles, then `camera_zoom_extents` (or zoom_window to a bounding region) and `get_view`. The user sees the drawing with #1, #2, #3, #4 bbox-marked candidates and can answer 'the third one'. Much better UX than handle tables.",
    "",
    "Numbering follows input order (handles[0] → #1, handles[1] → #2, …). Each box gets a red text label '#N' anchored at its top-left corner; label size auto-scales to ~8% of the larger bbox dimension (so it stays legible on tiny entities and doesn't dominate large ones).",
    "",
    "Skipped entities (bad handle, no geometric extents, sentinel extents) are listed in the response's `skipped` field — you'll know which inputs didn't get drawn.",
    "",
    "No manual cleanup needed when paired with `get_view` — the snapshot tool wipes the overlay layer after capture, so annotations don't leak into the user's drawing. `overlay_clear` is still useful between successive `overlay_*` calls when you want a clean slate without taking a snapshot in between.",
  ].join("\n"),
  {
    handles: z.array(z.string()).min(1).max(100)
      .describe("Entity handles (hex strings, as returned by query_objects) to bbox-highlight. Input order is preserved as label numbering."),
  },
  async (args) => {
    const text = await postBridge("/overlay", { op: "highlight", handles: args.handles });
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "overlay_hud",
  [
    "Draw an informational HUD panel on the `CLAUDE_OVERLAY` layer, anchored at the top-left of the model extents. The panel renders 4 lines of yellow text and appears in subsequent `get_view` snapshots. View-only — does NOT modify user geometry.",
    "",
    "Lines rendered:",
    "  1. doc: <filename> (or '<unsaved>' for new drawings)",
    "  2. ext: WxH @ (origin) — drawing extents size and lower-left origin",
    "  3. layer: <active>  (N/M visible) — active layer name and visible/total layer count",
    "  4. top: Type1:n Type2:n Type3:n — top-3 entity types by count (model space, excluding overlay layer itself)",
    "",
    "Use this when you want orienting context burned into the snapshot itself — handy for first-look snapshots or when handing off a view to someone who needs to know what they're looking at without reading a separate JSON.",
    "",
    "Sentinel extents (~±1e20, typical for empty / freshly-created drawings) fall back to a (0,0)-(1,1) anchor and the first line gets `(no extents)` appended.",
    "",
    "Text height auto-scales to ~2.5% of the larger extents side (with a 0.5-unit floor so tiny sketches still get readable HUD).",
    "",
    "Typical sequence: `overlay_hud` → `camera_zoom_extents` → `get_view`. `get_view` itself wipes the overlay layer after capturing the PNG, so no manual cleanup is needed. Only call `overlay_clear` first if you've stacked overlay ops without an intervening `get_view`.",
  ].join("\n"),
  {},
  async () => {
    const text = await postBridge("/overlay", { op: "hud" });
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "overlay_grid",
  [
    "Draw a coordinate grid (cyan vertical + horizontal lines with axis-value labels) covering the model extents on the `CLAUDE_OVERLAY` layer. The grid appears in the next `get_view` snapshot and is auto-cleared after capture. View-only — does NOT modify user geometry.",
    "",
    "Step selection: pass `step` to fix the spacing (drawing units). Omit it for auto-pick — the plugin chooses a 'nice round' value (1, 2, or 5 × 10^N) that yields ~15 cells across the extents diagonal. So a 1m sketch gets a 0.1m grid, a 100km survey gets a 5km grid, all readable.",
    "",
    "Each gridline gets an axis-value label anchored just inside the bottom (X values) or left (Y values) edge of the extents. Label format is integer when step >= 1 and decimal when smaller.",
    "",
    "Sentinel extents (~±1e20, empty drawings) fall back to a (0,0)-(1,1) frame and auto-step picks 0.1.",
    "",
    "Typical sequence: `overlay_grid` → `camera_zoom_extents` → `get_view`. Pair with `overlay_hud` first if you also want the corner-panel context — both live on the same layer and get cleared together by the snapshot.",
  ].join("\n"),
  {
    step: z.number().positive().optional()
      .describe("Optional grid spacing in drawing units. Omit to auto-pick a nice-round step (1/2/5 × 10^N giving ~15 cells across the extents diagonal)."),
  },
  async (args) => {
    const body: { op: string; step?: number } = { op: "grid" };
    if (args.step !== undefined) body.step = args.step;
    const text = await postBridge("/overlay", body);
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "overlay_scale_bar",
  [
    "Draw a 5-segment cartographic scale bar in a chosen corner of the model extents on the `CLAUDE_OVERLAY` layer. White color; even segments are filled (Solid), odd segments stay outline. Six numeric labels (0, L/5, 2L/5, 3L/5, 4L/5, L) sit under or above the bar. View-only — does NOT modify user geometry; cleared automatically by `get_view`.",
    "",
    "Length: pass `length` (drawing units) to fix the bar size. Omit it for auto-pick — the plugin chooses a nice round value (1/2/5 × 10^N) near 20% of the extents width.",
    "",
    "Position: pass `position` to anchor the bar in a specific corner of the extents. Valid values: `bottom_left` (default), `bottom_right`, `top_left`, `top_right`. Labels sit below the bar for bottom corners, above for top corners.",
    "",
    "Pair with `overlay_grid` and/or `overlay_hud` — all three live on the same layer and get wiped together when you call `get_view`. Typical: `overlay_grid` → `overlay_scale_bar` → `overlay_hud` → `camera_zoom_extents` → `get_view`.",
  ].join("\n"),
  {
    length: z.number().positive().optional()
      .describe("Total scale-bar length in drawing units. Omit to auto-pick a nice-round value near 20% of extents width."),
    position: z.enum(["bottom_left", "bottom_right", "top_left", "top_right"]).optional()
      .describe("Corner anchor inside the extents. Default `bottom_left`."),
  },
  async (args) => {
    const body: { op: string; length?: number; position?: string } = { op: "scale_bar" };
    if (args.length   !== undefined) body.length   = args.length;
    if (args.position !== undefined) body.position = args.position;
    const text = await postBridge("/overlay", body);
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "overlay_markers",
  [
    "Draw small blue + crosses as POINT references on the `CLAUDE_OVERLAY` layer. Markers complement `overlay_highlight`: highlight = area (bbox around an entity), markers = exact point (insertion / anchor). View-only; auto-cleared by `get_view`.",
    "",
    "Two flavors, combine freely:",
    "  - Anchored (set `anchored: true`): four corner crosses at TL/TR/BL/BR of model extents, labeled. Useful as an orientation aid — the snapshot reader can match each anchor with the HUD's reported extents to read the page like a map.",
    "  - Dynamic (`handles: [...]`): a cross at each entity's insertion-point. BlockReference uses InsertPoint; DBPoint/DBText use Position; for anything else the centroid of GeometricExtents. Numbered #1..#N in input order, like overlay_highlight.",
    "",
    "When to prefer markers over highlight: the candidate entity is anchored at a point (block symbol, text label) where the bbox is much larger than the useful 'where it lives' point. A 10×10m block's bbox spans 10m; the marker pin-points the 2-cm insertion location.",
    "",
    "Compose with grid/hud/scale_bar for a richly annotated snapshot — all live on `CLAUDE_OVERLAY` and get cleared together by `get_view`. Skipped entities (bad handle, no extractable position) are reported in the `skipped` field.",
  ].join("\n"),
  {
    anchored: z.boolean().optional()
      .describe("If true, draw 4 anchored corner crosses (TL/TR/BL/BR) at the model extents. Default false."),
    handles: z.array(z.string()).max(100).optional()
      .describe("Entity handles (hex, as returned by query_objects). A cross is drawn at each entity's insertion-point and labeled #1..#N in input order."),
  },
  async (args) => {
    const body: { op: string; anchored?: boolean; handles?: string[] } = { op: "markers" };
    if (args.anchored !== undefined) body.anchored = args.anchored;
    if (args.handles  !== undefined) body.handles  = args.handles;
    const text = await postBridge("/overlay", body);
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "overlay_clear",
  [
    "Erase ALL entities currently on the `CLAUDE_OVERLAY` layer — bounding boxes, labels, HUD panels, anything previously drawn by `overlay_highlight` or `overlay_hud`. View-only operation, does not touch user geometry.",
    "",
    "Mostly redundant in the normal Claude flow: `get_view` already auto-clears the overlay after capturing the snapshot. Use this explicitly only when you want to discard overlay state without taking a snapshot — e.g. after `overlay_highlight` if the user answered the disambiguation question from looking at NanoCAD live, not from a snapshot. The overlay layer itself is preserved.",
  ].join("\n"),
  {},
  async () => {
    const text = await postBridge("/overlay", { op: "clear" });
    return { content: [{ type: "text", text }] };
  },
);

server.tool(
  "query_objects",
  "Search entities in the active drawing's model space. All filter fields are optional and combine with AND-semantics; an empty filter returns every entity up to max_results. Each match returns: handle, .NET type name, layer, color index, linetype, block_name (only for BlockReference), position (only for BlockReference / DBPoint), and geometric bounds (min/max XYZ). Read-only — never modifies the drawing.",
  {
    type: z.string().optional().describe("Exact .NET type name of the entity (e.g. 'Line', 'Circle', 'BlockReference', 'DBText'). Case-sensitive."),
    layer: z.string().optional().describe("Exact layer name."),
    block_name: z.string().optional().describe("Only matches BlockReference entities whose block name equals this value."),
    max_results: z.number().int().positive().max(10000).optional().describe("Truncate matches after N entities. Default 500. The response includes a 'truncated' flag and the total 'match_count' even when truncated."),
  },
  async (args) => {
    const text = await postBridge("/query", args ?? {});
    return { content: [{ type: "text", text }] };
  },
);

const transport = new StdioServerTransport();
await server.connect(transport);
process.stderr.write(`[mcp-nano-bridge] connected via stdio — bridge=${BRIDGE_URL}\n`);
