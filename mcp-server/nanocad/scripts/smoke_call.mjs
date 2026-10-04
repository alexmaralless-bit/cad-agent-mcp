// End-to-end smoke: initialize → tools/call healthz → tools/call get_document_summary.
// REQUIRES the NanoCAD plugin to be loaded and HTTP bridge running.
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
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
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
  const payload = { jsonrpc: "2.0", method, params };
  if (id != null) payload.id = id;
  child.stdin.write(JSON.stringify(payload) + "\n");
  if (id != null) return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

async function main() {
  await send(
    "initialize",
    { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "smoke-call", version: "0.0.0" } },
    1,
  );
  send("notifications/initialized", {});

  const h = await send("tools/call", { name: "healthz", arguments: {} }, 2);
  console.log("healthz →", JSON.stringify(h, null, 2));

  const s = await send("tools/call", { name: "get_document_summary", arguments: {} }, 3);
  // The summary body can be huge — truncate text for log readability.
  const trimmed = { ...s, content: s.content?.map((c) => c.type === "text" ? { ...c, text: c.text.slice(0, 400) + (c.text.length > 400 ? "… [truncated]" : "") } : c) };
  console.log("get_document_summary →", JSON.stringify(trimmed, null, 2));

  child.kill();
  process.exit(0);
}

main().catch((err) => {
  console.error("smoke_call failed:", err);
  child.kill();
  process.exit(1);
});
