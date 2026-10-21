import { readFile } from "node:fs/promises";
import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";

globalThis.crypto ??= webcrypto;
const source = (await readFile(new URL("../worker/worker.js", import.meta.url), "utf8"))
  .replace('import { connect } from "cloudflare:sockets";', "const connect = () => { throw new Error('unexpected TCP'); };");
const { createHandler } = await import("data:text/javascript;base64," + Buffer.from(source).toString("base64"));
const key = "A".repeat(64);
const env = { SHARED_SECRET: key };
const request = (path, extra = {}) => new Request("https://relay.example" + path, {
  headers: { Authorization: "Bearer " + key, ...extra },
});

test("browser status is public, reports configuration and never opens a tunnel or exposes the key", async () => {
  const handler = createHandler(() => { throw new Error("public status must not connect"); });
  for (const path of ["/", "/status"]) {
    for (const settings of [env, {}, { SHARED_SECRET: "short" }]) {
      const response = await handler(new Request("https://relay.example" + path), settings);
      assert.equal(response.status, settings === env ? 200 : 503);
      assert.equal(response.headers.get("cache-control"), "no-store");
      const body = await response.text();
      assert.equal(body.includes(key), false);
      assert.equal(JSON.parse(body).status, settings === env ? "ok" : "configuration-required");
    }
  }
});

test("private routes require a key; no origin/domain checks", async () => {
  const handler = createHandler(() => { throw new Error("must not connect"); });
  for (const path of ["/health", "/probe", "/tunnel"]) {
    assert.equal((await handler(new Request("https://relay.example" + path), env)).status, 401);
    assert.equal((await handler(request(path, { Authorization: "Bearer " + "B".repeat(64) }), env)).status, 401);
  }
  const response = await handler(request("/health", { Origin: "https://arbitrary.example" }), env);
  assert.equal(response.status, 200);
  assert.equal((await response.json()).service, "osu-spectator-relay");
  assert.equal(response.headers.get("cache-control"), "no-store");
});

test("missing Worker secret fails closed", async () => {
  const handler = createHandler(() => {});
  assert.equal((await handler(request("/health"), {})).status, 503);
});

test("invalid destination or missing upgrade never creates sockets", async () => {
  let calls = 0;
  const handler = createHandler(() => calls++);
  assert.equal((await handler(request("/tunnel"), env)).status, 426);
  for (const [host, port] of [["user@host", "443"], ["host/path", "443"], ["example.org", "0"], ["example.org", "443x"]]) {
    const result = await handler(request("/tunnel", { Upgrade: "websocket", "X-Relay-Host": host, "X-Relay-Port": port }), env);
    assert.equal(result.status, 400);
  }
  assert.equal(calls, 0);
});

test("authenticated destination has no domain allowlist; upstream failure is 502", async () => {
  let destination, options;
  const handler = createHandler((address, opts) => { destination = address; options = opts; throw new Error("blocked"); });
  const response = await handler(request("/tunnel", {
    Upgrade: "websocket", "X-Relay-Host": "unrelated.example", "X-Relay-Port": "443",
  }), env);
  assert.deepEqual(destination, { hostname: "unrelated.example", port: 443 });
  assert.equal(options.secureTransport, "off");
  assert.equal(response.status, 502);
  assert.equal(response.headers.get("x-relay-error-stage"), "worker-upstream");
  assert.equal((await response.json()).stage, "worker-upstream");
});

test("tunnel errors report the failing leg with distinct close codes", async () => {
  const NativeResponse = globalThis.Response;
  const oldPair = globalThis.WebSocketPair;
  globalThis.Response = class {
    constructor(body, init) { Object.assign(this, init); this.body = body; }
  };
  try {
    for (const [mode, expected] of [["read", 4001], ["write", 4002], ["tcp", 4003], ["send", 4004], ["client", 4005], ["eof", 1000]]) {
      const events = new Map(), closes = [];
      let controller, rejectClosed;
      let socketClosed = false;
      const server = {
        accept() {},
        addEventListener(name, callback) { events.set(name, callback); },
        send() { if (mode === "send") throw new Error("client transport failed"); },
        close(code, reason) { closes.push({ code, reason }); },
      };
      globalThis.WebSocketPair = class { constructor() { this[0] = {}; this[1] = server; } };
      const handler = createHandler(() => ({
        opened: Promise.resolve(), closed: new Promise((_, reject) => { rejectClosed = reject; }),
        readable: new ReadableStream({ start(value) { controller = value; } }),
        writable: new WritableStream({ write() { if (mode === "write") throw new Error("upstream write failed"); } }),
        close: async () => { socketClosed = true; },
      }));
      const response = await handler(request("/tunnel", {
        Upgrade: "websocket", "X-Relay-Host": "spectator.osu.ppy.sh", "X-Relay-Port": "443",
      }), env);
      assert.equal(response.status, 101);
      assert.equal(response.headers["x-relay-protocol"], "2");
      if (mode === "read") controller.error(new Error("upstream read failed"));
      if (mode === "write") events.get("message")({ data: new Uint8Array([1]).buffer });
      if (mode === "tcp") rejectClosed(new Error("upstream TCP failed"));
      if (mode === "send") controller.enqueue(new Uint8Array([1]));
      if (mode === "client") events.get("error")();
      if (mode === "eof") controller.close();
      for (let attempt = 0; attempt < 10 && closes.length === 0; attempt++)
        await new Promise(resolve => setTimeout(resolve, 5));
      assert.equal(closes.length, 1, mode + " closes once");
      assert.equal(closes[0].code, expected, mode);
      assert.equal(socketClosed, true, mode + " releases TCP");
    }
  } finally { globalThis.Response = NativeResponse; globalThis.WebSocketPair = oldPair; }
});

test("binary tunnel preserves ordered data and closes resources", async () => {
  const NativeResponse = globalThis.Response;
  const oldPair = globalThis.WebSocketPair;
  const writes = [], sent = [];
  let socketClosed = false, writerAborted = false;
  let readerController;
  const events = new Map();
  const server = {
    accept() {},
    addEventListener(name, callback) { events.set(name, callback); },
    send(value) { sent.push(new Uint8Array(value)); },
    close() {},
  };
  globalThis.WebSocketPair = class { constructor() { this[0] = {}; this[1] = server; } };
  globalThis.Response = class {
    constructor(body, init) { Object.assign(this, init); this.body = body; }
  };
  const readable = new ReadableStream({ start(controller) { readerController = controller; } });
  const writable = new WritableStream({
    async write(value) { await new Promise(resolve => setTimeout(resolve, 1)); writes.push([...value]); },
    abort() { writerAborted = true; },
  });
  try {
    const handler = createHandler(() => ({
      opened: Promise.resolve(), closed: new Promise(() => {}), readable, writable,
      close: async () => { socketClosed = true; },
    }));
    const response = await handler(request("/tunnel", {
      Upgrade: "websocket", "X-Relay-Host": "spectator.osu.ppy.sh", "X-Relay-Port": "443",
    }), env);
    assert.equal(response.status, 101);
    events.get("message")({ data: new Uint8Array([22, 3, 3]).buffer });
    events.get("message")({ data: new Uint8Array([0, 255]).buffer });
    readerController.enqueue(new Uint8Array([9, 0, 8]));
    await new Promise(resolve => setTimeout(resolve, 25));
    assert.deepEqual(writes, [[22, 3, 3], [0, 255]]);
    assert.deepEqual([...sent[0]], [9, 0, 8]);
    events.get("close")();
    await new Promise(resolve => setTimeout(resolve, 10));
    assert.equal(socketClosed, true);
    assert.equal(writerAborted, true);
  } finally { globalThis.Response = NativeResponse; globalThis.WebSocketPair = oldPair; }
});
