// Smoke-test get_view through MCP. Reports image content size + metadata sidecar.
// REQUIRES the NanoCAD plugin loaded with an open document.
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
    let msg; try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id != null && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
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

async function main() {
  await send("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "smoke-view", version: "0.0.0" } }, 1);
  send("notifications/initialized", {});

  const r = await send("tools/call", { name: "get_view", arguments: {} }, 2);
  const summary = r.content.map((c) => {
    if (c.type === "image") return { type: c.type, mimeType: c.mimeType, base64_length: c.data.length, approx_png_bytes: Math.round(c.data.length * 0.75) };
    if (c.type === "text") return { type: c.type, text: c.text.slice(0, 300) };
    return c;
  });
  console.log("get_view →", JSON.stringify(summary, null, 2));

  child.kill();
  process.exit(0);
}

main().catch((e) => { console.error("smoke_view failed:", e); child.kill(); process.exit(1); });
