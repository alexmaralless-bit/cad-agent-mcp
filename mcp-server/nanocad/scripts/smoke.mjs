// Minimal MCP stdio smoke-test: spawns the server, runs the JSON-RPC initialize
// handshake, lists declared tools, exits. Does NOT call any tool (so the NanoCAD
// plugin does not need to be running). Run with: node scripts/smoke.mjs
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const serverPath = resolve(here, "..", "dist", "index.js");

const child = spawn(process.execPath, [serverPath], {
  stdio: ["pipe", "pipe", "pipe"],
});

let buf = "";
const pending = new Map(); // id -> { resolve, reject }

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
  if (id != null) {
    return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
  }
}

async function main() {
  const initRes = await send(
    "initialize",
    {
      protocolVersion: "2024-11-05",
      capabilities: {},
      clientInfo: { name: "smoke", version: "0.0.0" },
    },
    1,
  );
  console.log("initialize.result:", JSON.stringify(initRes, null, 2));

  send("notifications/initialized", {});

  const tools = await send("tools/list", {}, 2);
  console.log("tools/list.result:", JSON.stringify(tools, null, 2));

  child.kill();
  process.exit(0);
}

main().catch((err) => {
  console.error("smoke failed:", err);
  child.kill();
  process.exit(1);
});
