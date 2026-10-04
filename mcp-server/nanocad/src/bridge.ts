const DEFAULT_TIMEOUT_MS = Number(process.env.BRIDGE_TIMEOUT_MS ?? 35000);

export const BRIDGE_URL = (process.env.BRIDGE_URL ?? "http://127.0.0.1:7890").replace(/\/+$/, "");

export async function fetchBridge(path: string, timeoutMs = DEFAULT_TIMEOUT_MS): Promise<string> {
  return requestBridge("GET", path, undefined, timeoutMs);
}

export async function postBridge(
  path: string,
  body: unknown,
  timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<string> {
  return requestBridge("POST", path, body, timeoutMs);
}

async function requestBridge(
  method: "GET" | "POST",
  path: string,
  body: unknown,
  timeoutMs: number,
): Promise<string> {
  const url = `${BRIDGE_URL}${path}`;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const init: RequestInit = { method, signal: ctrl.signal };
    if (method === "POST") {
      init.headers = { "content-type": "application/json" };
      init.body = body === undefined ? "{}" : JSON.stringify(body);
    }
    const res = await fetch(url, init);
    const text = await res.text();
    if (!res.ok) {
      throw new Error(`bridge ${res.status} ${res.statusText} at ${method} ${url}: ${text.slice(0, 500)}`);
    }
    return text;
  } catch (err) {
    const e = err as { name?: string; message?: string; cause?: { code?: string } };
    if (e.name === "AbortError") {
      throw new Error(`bridge request timed out after ${timeoutMs} ms: ${method} ${url}`);
    }
    if (e.cause?.code === "ECONNREFUSED") {
      throw new Error(
        `bridge unreachable at ${url} — is the NanoCAD plugin loaded and is CLAUDE_HTTP_START running?`,
      );
    }
    throw err;
  } finally {
    clearTimeout(timer);
  }
}
