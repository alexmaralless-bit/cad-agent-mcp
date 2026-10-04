// Smoke for execute_command + run_lisp through MCP. Tests dry-run (plan)
// + commit (confirm=true) for SAFE operations only:
//   - _ZOOM E  — re-fits view, doesn't modify entities
//   - (princ "hello")  — pure side-channel print, no DWG mutation
// REQUIRES the new plugin (artifact ≥20260513_214427) loaded — CLAUDE_EXEC
// + POST /exec route.
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const serverPath = resolve(here, "..", "dist", "index.js");

const child = spawn(process.execPath, [serverPath], { stdio: ["pipe", "pipe", "pipe"] });
let buf = ""; const pending = new Map();
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
function pretty(label, result) {
  const text = result.content.find((c) => c.type === "text")?.text ?? "";
  let parsed; try { parsed = JSON.parse(text); } catch { console.log(`${label}: <non-JSON>\n${text.slice(0,400)}`); return; }
  console.log(`\n=== ${label} ===`);
  console.log(`  kind            = ${parsed.kind}`);
  console.log(`  command         = ${JSON.stringify(parsed.command)}`);
  console.log(`  confirm         = ${parsed.confirm}`);
  console.log(`  executed        = ${parsed.executed}`);
  if (parsed.plan) {
    console.log(`  plan.will_execute       = ${JSON.stringify(parsed.plan.will_execute)}`);
    console.log(`  plan.backup_path_if_confirmed = ${parsed.plan.backup_path_if_confirmed}`);
    console.log(`  plan.document_unsaved   = ${parsed.plan.document_unsaved}`);
  }
  if (parsed.wrapped_command) console.log(`  wrapped_command = ${JSON.stringify(parsed.wrapped_command)}`);
  if (parsed.backup_path)     console.log(`  backup_path     = ${parsed.backup_path}`);
  if (parsed.warnings?.length)console.log(`  warnings        = ${JSON.stringify(parsed.warnings)}`);
  if (parsed.error)           console.log(`  ERROR           = ${parsed.error}`);
}

async function main() {
  let nid = 0; const id = () => ++nid;
  await send("initialize", {
    protocolVersion: "2024-11-05", capabilities: {},
    clientInfo: { name: "smoke-exec", version: "0.0.0" },
  }, id());
  send("notifications/initialized", {});

  // 1. execute_command — dry-run
  const r1 = await send("tools/call", {
    name: "execute_command",
    arguments: { command: "_ZOOM E " }, // confirm defaults to false
  }, id());
  pretty("[1] execute_command dry-run (_ZOOM E)", r1);

  // 2. execute_command — commit
  const r2 = await send("tools/call", {
    name: "execute_command",
    arguments: { command: "_ZOOM E ", confirm: true },
  }, id());
  pretty("[2] execute_command commit (_ZOOM E)", r2);

  // 3. run_lisp — dry-run
  const r3 = await send("tools/call", {
    name: "run_lisp",
    arguments: { code: '(princ "hello from claude")' },
  }, id());
  pretty("[3] run_lisp dry-run ((princ \"hello\"))", r3);

  // 4. run_lisp — commit
  const r4 = await send("tools/call", {
    name: "run_lisp",
    arguments: { code: '(princ "hello from claude")', confirm: true },
  }, id());
  pretty("[4] run_lisp commit", r4);

  child.kill();
  process.exit(0);
}
main().catch((e) => { console.error("smoke_exec failed:", e); child.kill(); process.exit(1); });
