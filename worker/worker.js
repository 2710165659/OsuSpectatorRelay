import { connect } from "cloudflare:sockets";

// No origin/domain allowlist. Authentication is exclusively the SHARED_SECRET binding.
// Never log the Authorization header or the user's TCP payload.
export function createHandler(connectTcp) {
  return async function fetchHandler(request, env) {
    const json = (value, status = 200) => new Response(JSON.stringify(value), {
      status,
      headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
    });
    const path = new URL(request.url).pathname;
    // Public browser checks only describe this deployment. Tunnel authentication
    // and the authenticated HTTP/WebSocket probes remain unchanged.
    if (request.method === "GET" && (path === "/" || path === "/status")) {
      const configured = typeof env.SHARED_SECRET === "string" && env.SHARED_SECRET.length >= 32;
      return new Response(JSON.stringify({
        service: "osu-spectator-relay",
        status: configured ? "ok" : "configuration-required",
        message: configured ? "Worker 运行正常，密钥已配置。" : "Worker 已启动，请配置 SHARED_SECRET。",
        note: "此地址只检查 Worker 部署状态；密钥是否匹配及完整隧道请使用客户端检查。",
      }, null, 2), {
        status: configured ? 200 : 503,
        headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" },
      });
    }
    if (typeof env.SHARED_SECRET !== "string" || env.SHARED_SECRET.length < 32)
      return json({ error: "SHARED_SECRET is not configured" }, 503);
    const supplied = request.headers.get("Authorization") || "";
    if (!supplied.startsWith("Bearer ") || supplied.length > 1024 ||
        !(await secretEquals(supplied.slice(7), env.SHARED_SECRET)))
      return json({ error: "Unauthorized" }, 401);

    if (request.method !== "GET") return json({ error: "Method not allowed" }, 405);
    if (path === "/health") return json({ service: "osu-spectator-relay", version: 2 });
    if (path !== "/tunnel" && path !== "/probe") return json({ error: "Not found" }, 404);
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
      return json({ error: "WebSocket upgrade required" }, 426);

    if (path === "/probe") {
      const pair = new WebSocketPair();
      const [client, server] = Object.values(pair);
      server.binaryType = "arraybuffer";
      server.accept();
      const timer = setTimeout(() => { try { server.close(1000, "Probe finished"); } catch {} }, 5000);
      server.addEventListener("message", event => {
        if (!(event.data instanceof ArrayBuffer) || event.data.byteLength > 256) {
          clearTimeout(timer); server.close(1008, "Invalid probe"); return;
        }
        server.send(event.data);
      });
      server.addEventListener("close", () => clearTimeout(timer));
      server.addEventListener("error", () => clearTimeout(timer));
      return new Response(null, { status: 101, webSocket: client });
    }

    const hostname = request.headers.get("X-Relay-Host") || "";
    const portText = request.headers.get("X-Relay-Port") || "";
    const port = Number(portText);
    // Validate syntax only; do not restrict destinations to a domain allowlist.
    if (hostname.length < 1 || hostname.length > 253 || !/^[a-z0-9.:-]+$/i.test(hostname) ||
        !/^\d{1,5}$/.test(portText) || port < 1 || port > 65535)
      return json({ error: "Invalid destination" }, 400);

    let tcp;
    let connectTimer;
    try {
      // Inner TLS belongs to osu. Enabling TLS here would wrap it a second time incorrectly.
      tcp = connectTcp({ hostname, port }, { secureTransport: "off", allowHalfOpen: false });
      void tcp.closed.catch(() => {});
      await Promise.race([
        tcp.opened,
        new Promise((_, reject) => { connectTimer = setTimeout(() => reject(new Error("TCP timeout")), 5000); }),
      ]);
    } catch {
      try { await tcp?.close(); } catch {}
      return new Response(JSON.stringify({ error: "Upstream TCP connection failed", stage: "worker-upstream" }), {
        status: 502,
        headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", "x-relay-error-stage": "worker-upstream" },
      });
    } finally { clearTimeout(connectTimer); }

    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    server.binaryType = "arraybuffer";
    const writer = tcp.writable.getWriter();
    const reader = tcp.readable.getReader();
    let closed = false;
    let queuedBytes = 0;
    let queue = Promise.resolve();
    // Private close codes identify the observed failing leg without inspecting inner TLS.
    const shutdown = (code = 1000, reason = "Closed") => {
      if (closed) return;
      closed = true;
      try { server.close(code, reason); } catch {}
      void reader.cancel().catch(() => {});
      void writer.abort().catch(() => {});
      void tcp.close().catch(() => {});
    };
    server.addEventListener("message", event => {
      if (closed) return;
      if (!(event.data instanceof ArrayBuffer)) { shutdown(1003); return; }
      const bytes = new Uint8Array(event.data);
      queuedBytes += bytes.byteLength;
      if (queuedBytes > 4 * 1024 * 1024) { shutdown(1009); return; }
      // Serialize writes to preserve byte order and bound buffered client data.
      queue = queue.then(async () => {
        try { if (!closed) await writer.write(bytes); }
        finally { queuedBytes -= bytes.byteLength; }
      }).catch(() => shutdown(4002, "UPSTREAM_WRITE_FAILED"));
    });
    server.addEventListener("close", () => shutdown());
    server.addEventListener("error", () => shutdown(4005, "CLIENT_WS_ERROR"));
    server.accept({ allowHalfOpen: true });
    void (async () => {
      while (!closed) {
        let item;
        try { item = await reader.read(); }
        catch { shutdown(4001, "UPSTREAM_READ_FAILED"); return; }
        if (item.done) { shutdown(1000, "UPSTREAM_EOF"); return; }
        if (item.value.byteLength > 0) {
          try { server.send(item.value); }
          catch { shutdown(4004, "CLIENT_SEND_FAILED"); return; }
        }
      }
    })();
    void tcp.closed.catch(() => shutdown(4003, "UPSTREAM_TCP_FAILED"));
    return new Response(null, { status: 101, webSocket: client, headers: { "x-relay-protocol": "2" } });
  };
}

async function secretEquals(left, right) {
  const encoder = new TextEncoder();
  const [a, b] = await Promise.all([
    crypto.subtle.digest("SHA-256", encoder.encode(left)),
    crypto.subtle.digest("SHA-256", encoder.encode(right)),
  ]);
  const x = new Uint8Array(a), y = new Uint8Array(b);
  let difference = 0;
  for (let i = 0; i < x.length; i++) difference |= x[i] ^ y[i];
  return difference === 0;
}

export default { fetch: createHandler(connect) };
