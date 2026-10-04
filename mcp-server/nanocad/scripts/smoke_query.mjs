// End-to-end smoke for query_objects via MCP: runs four filters against the active
// drawing. Requires the plugin loaded with POST /query route (step 3).
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const serverPath = resolve(here, "..", "dist", "index.js");

const child = spawn(process.execPath, [serverPath], { stdio: ["pipe", "pipe", "pipe"] });
let buf = "";
const pending = new Map();
child.stdout.on("data", (chunk) => {
  buf += chunk.toString("utf8");
  let idx;
  while ((idx = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, idx).trim();
    buf = buf.slice(idx + 1);
    if (!line) continue;
    let m; try { m = JSON.parse(line); } catch { continue; }
    if (m.id != null && pending.has(m.id)) {
      const { resolve, reject } = pending.get(m.id);
      pending.delete(m.id);
      if (m.error) reject(new Error(JSON.stringify(m.error))); else resolve(m.result);
    }
  }
});
child.stderr.on("data", (c) => process.stderr.write(`[server] ${c}`));
function send(method, params, id) {
  const p = { jsonrpc: "2.0", method, params };
  if (id != null) p.id = id;
  child.stdin.write(JSON.stringify(p) + "\n");
  if (id != null) return new Promise((res, rej) => pending.set(id, { resolve: res, reject: rej }));
}

function summarize(label, mcpResult) {
  const text = mcpResult.content.find((c) => c.type === "text")?.text ?? "";
  let parsed;
  try { parsed = JSON.parse(text); } catch { console.log(`${label}: <non-JSON>`); return; }
  console.log(`${label}:`);
  console.log(`  filter         = ${JSON.stringify(parsed.filter)}`);
  console.log(`  total_examined = ${parsed.total_examined}`);
  console.log(`  match_count    = ${parsed.match_count}`);
  console.log(`  returned       = ${parsed.results.length}`);
  console.log(`  truncated      = ${parsed.truncated}`);
  if (parsed.error) console.log(`  error          = ${parsed.error}`);
  const sample = parsed.results.slice(0, 2).map((r) => ({
    handle: r.handle, type: r.type, layer: r.layer,
    pos: r.position, block: r.block_name,
  }));
  console.log(`  sample[0..2]   = ${JSON.stringify(sample)}`);
  console.log("");
}

async function main() {
  let nextId = 1;
  const id = () => nextId++;

  await send("initialize", {
    protocolVersion: "2024-11-05", capabilities: {},
    clientInfo: { name: "smoke-query", version: "0.0.0" },
  }, id());
  send("notifications/initialized", {});

  const empty = await send("tools/call", { name: "query_objects", arguments: { max_results: 5 } }, id());
  summarize("[1] empty filter (max_results=5)", empty);

  const points = await send("tools/call", { name: "query_objects", arguments: { type: "DBPoint", max_results: 5 } }, id());
  summarize("[2] type=DBPoint", points);

  const layerPiket = await send("tools/call", { name: "query_objects", arguments: { layer: "ПИКЕТ", max_results: 5 } }, id());
  summarize("[3] layer=ПИКЕТ", layerPiket);

  const blocks = await send("tools/call", { name: "query_objects", arguments: { type: "BlockReference", max_results: 5 } }, id());
  summarize("[4] type=BlockReference", blocks);

  const nothing = await send("tools/call", { name: "query_objects", arguments: { type: "NoSuchType", max_results: 5 } }, id());
  summarize("[5] type=NoSuchType (should match 0)", nothing);

  child.kill();
  process.exit(0);
}

main().catch((e) => { console.error("smoke_query failed:", e); child.kill(); process.exit(1); });
