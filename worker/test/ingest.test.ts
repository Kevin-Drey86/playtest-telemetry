import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  GOOD_KEY, SESSION_ID, body, discordCalls, errorEvent, gzip, mockBackend, perfSample,
  projectLookups, resetState, rpcCalls, runScheduled, send, session,
  type Mock,
} from "./helpers";

let mock: Mock;

beforeEach(() => {
  resetState();
  mock = mockBackend();
});
afterEach(() => {
  vi.restoreAllMocks();
});

describe("routing", () => {
  it("health returns ok and version", async () => {
    const { res, json } = await send(null, { method: "GET", path: "/v1/health", key: null });
    expect(res.status).toBe(200);
    expect(json).toEqual({ ok: true, version: "0.1.0" });
  });
  it("unknown path is 404", async () => {
    expect((await send(null, { method: "GET", path: "/nope" })).res.status).toBe(404);
  });
  it("wrong method is 405 with Allow", async () => {
    const a = await send(null, { method: "GET", path: "/v1/ingest" });
    expect(a.res.status).toBe(405);
    expect(a.res.headers.get("Allow")).toBe("POST");
    expect((await send({}, { method: "POST", path: "/v1/health" })).res.status).toBe(405);
  });
});

describe("auth", () => {
  it("accepts a valid key", async () => {
    const { res, json } = await send(body());
    expect(res.status).toBe(200);
    expect(json).toEqual({ success: true, accepted: 1, dropped: 0 });
  });
  it("rejects an unknown key with 401 and no ingest", async () => {
    expect((await send(body(), { key: "wrong" })).res.status).toBe(401);
    expect(rpcCalls(mock)).toHaveLength(0);
  });
  it("rejects a missing key with 401", async () => {
    expect((await send(body(), { key: null })).res.status).toBe(401);
  });
  it("caches key -> project so only the first request looks it up", async () => {
    await send(body());
    await send(body({ session: session({ device_id: "device-2" }) }));
    await send(body({ session: session({ device_id: "device-3" }) }));
    expect(projectLookups(mock)).toHaveLength(1);
    expect(rpcCalls(mock)).toHaveLength(3);
  });
  it("negative-caches unknown keys briefly", async () => {
    await send(body(), { key: "wrong" });
    await send(body(), { key: "wrong" });
    expect(projectLookups(mock)).toHaveLength(1);
  });
  it("returns 503 when the project lookup fails", async () => {
    mock.projectStatus = 500;
    expect((await send(body())).res.status).toBe(503);
  });
});

describe("size limits and gzip", () => {
  it("rejects bodies over 512 KB with 413 (no content-length)", async () => {
    const big = JSON.stringify(body({ events: [errorEvent({ message: "x".repeat(300_000) }), errorEvent({ message: "y".repeat(300_000) })] }));
    expect(big.length).toBeGreaterThan(512 * 1024);
    const { res } = await send(null, { raw: big });
    expect(res.status).toBe(413);
    expect(rpcCalls(mock)).toHaveLength(0);
  });
  it("rejects a declared Content-Length over 512 KB", async () => {
    const { res } = await send(null, { raw: "{}", headers: { "Content-Length": String(600 * 1024) } });
    expect(res.status).toBe(413);
  });
  it("accepts gzip bodies", async () => {
    const { res, json } = await send(null, {
      raw: await gzip(JSON.stringify(body())),
      headers: { "Content-Encoding": "gzip" },
    });
    expect(res.status).toBe(200);
    expect(json.accepted).toBe(1);
    expect((mock.rpcBody as any).p_payload.events).toHaveLength(1);
  });
  it("rejects corrupt gzip with 400", async () => {
    const { res } = await send(null, { raw: new Uint8Array([1, 2, 3, 4, 5]), headers: { "Content-Encoding": "gzip" } });
    expect(res.status).toBe(400);
  });
  it("rejects a gzip bomb (small wire size, huge decoded size) with 413", async () => {
    const bomb = await gzip(new Uint8Array(5 * 1024 * 1024));
    expect(bomb.length).toBeLessThan(512 * 1024);
    const { res } = await send(null, { raw: bomb, headers: { "Content-Encoding": "gzip" } });
    expect(res.status).toBe(413);
  });
  it("rejects unsupported encodings", async () => {
    const { res } = await send(null, { raw: "{}", headers: { "Content-Encoding": "br" } });
    expect(res.status).toBe(400);
  });
  it("rejects invalid JSON", async () => {
    expect((await send(null, { raw: "{not json" })).res.status).toBe(400);
  });
});

describe("validation and whitelisting", () => {
  it("rebuilds rows from whitelisted fields only", async () => {
    const evil = {
      ...errorEvent(),
      project_id: "steal-me",
      id: 999,
      received_at: "2000-01-01",
      __proto__: { polluted: true },
      extra: "nope",
    };
    const { json } = await send(body({
      session: session({ project_id: "other", is_admin: true }),
      events: [evil],
      evil_top_level: 1,
    }));
    expect(json.success).toBe(true);
    const p = (mock.rpcBody as any).p_payload;
    expect(Object.keys(p).sort()).toEqual(["bug_reports", "events", "perf_samples", "session"]);
    expect(p.session).not.toHaveProperty("project_id");
    expect(p.session).not.toHaveProperty("is_admin");
    expect(Object.keys(p.events[0]).sort()).toEqual([
      "breadcrumbs", "client_ts", "context", "event_name", "fingerprint", "log_type", "message",
      "message_template", "props", "repeat_count", "scene", "stack_trace",
    ]);
    expect((mock.rpcBody as any).p_project_id).toBe("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
  });
  it("truncates strings and clamps numbers", async () => {
    await send(body({
      events: [errorEvent({ message: "m".repeat(5000), scene: "s".repeat(1000), repeat_count: 99_999_999, client_ts: Date.now() + 86_400_000 })],
      perf_samples: [perfSample({ fps_avg: 1e9, used_mem_mb: -5, battery_level: -1 })],
    }));
    const p = (mock.rpcBody as any).p_payload;
    expect(p.events[0].message).toHaveLength(512);
    expect(p.events[0].scene).toHaveLength(128);
    expect(p.events[0].repeat_count).toBe(1_000_000);
    expect(Date.parse(p.events[0].client_ts)).toBeLessThanOrEqual(Date.now());
    expect(p.perf_samples[0].fps_avg).toBe(10_000);
    expect(p.perf_samples[0].used_mem_mb).toBe(0);
    expect(p.perf_samples[0].battery_level).toBeNull();
  });
  it("drops malformed items and counts them instead of failing the batch", async () => {
    const { res, json } = await send(body({
      events: [errorEvent(), "garbage", null, { log_type: "Nope", message: "x" }, { log_type: "Error" }],
      perf_samples: [perfSample(), { fps_avg: "fast" }],
      bug_reports: [{}],
    }));
    expect(res.status).toBe(200);
    expect(json).toEqual({ success: true, accepted: 2, dropped: 6 });
    const p = (mock.rpcBody as any).p_payload;
    expect(p.events).toHaveLength(1);
    expect(p.perf_samples).toHaveLength(1);
  });
  it("only keeps context and breadcrumbs on Error/Exception events", async () => {
    await send(body({
      events: [
        errorEvent({ context: { level: "3" }, breadcrumbs: [{ ts: Date.now(), category: "ui", message: "tap" }] }),
        { client_ts: Date.now(), log_type: "Info", message: "hi", context: { a: "b" }, breadcrumbs: [{ message: "x" }] },
      ],
    }));
    const [err, info] = (mock.rpcBody as any).p_payload.events;
    expect(err.context).toEqual({ level: "3" });
    expect(err.breadcrumbs).toHaveLength(1);
    expect(info.context).toBeNull();
    expect(info.breadcrumbs).toBeNull();
  });
  it("drops fingerprint and issue data from non-error events and rejects non-hex fingerprints", async () => {
    await send(body({
      events: [
        { client_ts: Date.now(), log_type: "Info", message: "hi", fingerprint: "abcd", stack_trace: "trace" },
        errorEvent({ fingerprint: "not hex!!" }),
      ],
    }));
    const [info, err] = (mock.rpcBody as any).p_payload.events;
    expect(info.fingerprint).toBeNull();
    expect(info.stack_trace).toBeNull();
    expect(err.fingerprint).toBeNull();
  });
  it("custom events become Event rows with scalar props only", async () => {
    await send(body({
      events: [{ client_ts: Date.now(), event_name: "level_start", log_type: "Error", props: { level: 3, name: "x", nested: { a: 1 }, ok: true } }],
    }));
    const e = (mock.rpcBody as any).p_payload.events[0];
    expect(e.log_type).toBe("Event");
    expect(e.props).toEqual({ level: 3, name: "x", ok: true });
  });
  it("rejects batches with more than 500 items with 413", async () => {
    const events = Array.from({ length: 501 }, () => ({ client_ts: Date.now(), log_type: "Info", message: "x" }));
    const { res } = await send(body({ events }));
    expect(res.status).toBe(413);
    expect(rpcCalls(mock)).toHaveLength(0);
  });
  it("accepts exactly 500 items", async () => {
    const events = Array.from({ length: 500 }, () => ({ client_ts: Date.now(), log_type: "Info", message: "x" }));
    expect((await send(body({ events }))).json.accepted).toBe(500);
  });
  it("rejects a missing or invalid session with 400", async () => {
    expect((await send({ events: [] })).res.status).toBe(400);
    expect((await send(body({ session: session({ id: "not-a-uuid" }) }))).res.status).toBe(400);
    expect((await send(body({ session: session({ device_id: "" }) }))).res.status).toBe(400);
  });
});

describe("rate limiting", () => {
  it("returns 429 with Retry-After after 30 requests/min per device, other devices unaffected", async () => {
    for (let i = 0; i < 30; i++) expect((await send(body())).res.status).toBe(200);
    const limited = await send(body());
    expect(limited.res.status).toBe(429);
    expect(Number(limited.res.headers.get("Retry-After"))).toBeGreaterThan(0);
    expect((await send(body({ session: session({ device_id: "device-other" }) }))).res.status).toBe(200);
  });
});

describe("supabase failures", () => {
  it("returns 503 when the RPC fails so the client retries", async () => {
    mock.rpcStatus = 500;
    expect((await send(body())).res.status).toBe(503);
  });
  it("returns 503 when Supabase is unreachable", async () => {
    mock.rpcStatus = 503;
    mock.rpcErrorBody = "upstream down";
    expect((await send(body())).res.status).toBe(503);
  });
  it("returns 422 (do not retry) for data errors", async () => {
    mock.rpcStatus = 400;
    mock.rpcErrorBody = { code: "22P02", message: "invalid input syntax" };
    expect((await send(body())).res.status).toBe(422);
  });
  it("returns 409 when the session belongs to another project", async () => {
    mock.rpcStatus = 403;
    mock.rpcErrorBody = { code: "42501", message: "session x belongs to another project" };
    expect((await send(body())).res.status).toBe(409);
  });
});

describe("discord alerts", () => {
  const issue = (n: number) => ({ fingerprint: `f${n}`, log_type: "Error", message_template: `Boom ${n}`, build: "0.1.0" });

  it("is not called when there are no new issues", async () => {
    mock.rpcResult = { inserted: 1, new_issues: [] };
    await send(body());
    expect(discordCalls(mock)).toHaveLength(0);
  });
  it("sends one message with all new issues and the required fields", async () => {
    mock.rpcResult = { inserted: 3, new_issues: [issue(1), issue(2)] };
    await send(body());
    const calls = discordCalls(mock);
    expect(calls).toHaveLength(1);
    const msg = JSON.parse(calls[0].init!.body as string);
    expect(msg.embeds).toHaveLength(2);
    expect(msg.allowed_mentions).toEqual({ parse: [] });
    const text = JSON.stringify(msg);
    for (const needle of ["Test Game", "0.1.0", "Error", "Boom 1", "Pixel 7", "alice", SESSION_ID]) {
      expect(text).toContain(needle);
    }
  });
  it("caps embeds at 10", async () => {
    mock.rpcResult = { inserted: 12, new_issues: Array.from({ length: 12 }, (_, i) => issue(i)) };
    await send(body());
    const msg = JSON.parse(discordCalls(mock)[0].init!.body as string);
    expect(msg.embeds).toHaveLength(10);
    expect(msg.content).toContain("+2 more");
  });
  it("is skipped silently when the project has no webhook", async () => {
    mock.project!.discord_webhook_url = null;
    mock.rpcResult = { inserted: 1, new_issues: [issue(1)] };
    expect((await send(body())).res.status).toBe(200);
    expect(discordCalls(mock)).toHaveLength(0);
  });
  it("refuses webhooks that are not Discord URLs", async () => {
    mock.project!.discord_webhook_url = "https://evil.example.com/hook";
    mock.rpcResult = { inserted: 1, new_issues: [issue(1)] };
    await send(body());
    expect(mock.calls.some((c) => c.url.includes("evil.example.com"))).toBe(false);
  });
  it("drops the alert on a Discord 429 without retrying and still returns 200", async () => {
    mock.discordStatus = 429;
    mock.rpcResult = { inserted: 1, new_issues: [issue(1)] };
    const { res } = await send(body());
    expect(res.status).toBe(200);
    expect(discordCalls(mock)).toHaveLength(1);
  });
});

describe("screenshots", () => {
  const jpegB64 = btoa(String.fromCharCode(0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 4));

  it("stores a JPEG in Storage and records its path", async () => {
    const { res } = await send(body({ bug_reports: [{ client_ts: Date.now(), tester_note: "broken", screenshot_b64: jpegB64 }] }));
    expect(res.status).toBe(200);
    const upload = mock.calls.find((c) => c.url.includes("/storage/v1/object/bug-screenshots/"))!;
    expect(upload).toBeTruthy();
    const path = (mock.rpcBody as any).p_payload.bug_reports[0].screenshot_path as string;
    expect(path).toMatch(/^aaaaaaaa-.*\/11111111-.*\.jpg$/);
    expect(upload.url.endsWith(path)).toBe(true);
    expect(JSON.stringify(mock.rpcBody)).not.toContain(jpegB64);
  });
  it("ignores non-JPEG data but keeps the bug report", async () => {
    const { res } = await send(body({ bug_reports: [{ client_ts: Date.now(), tester_note: "broken", screenshot_b64: btoa("not a jpeg") }] }));
    expect(res.status).toBe(200);
    expect(mock.calls.some((c) => c.url.includes("/storage/"))).toBe(false);
    expect((mock.rpcBody as any).p_payload.bug_reports[0].screenshot_path).toBeNull();
  });
  it("keeps the bug report when the upload fails", async () => {
    mock.storageStatus = 500;
    const { res } = await send(body({ bug_reports: [{ client_ts: Date.now(), tester_note: "broken", screenshot_b64: jpegB64 }] }));
    expect(res.status).toBe(200);
    expect((mock.rpcBody as any).p_payload.bug_reports[0].screenshot_path).toBeNull();
  });
});

describe("scheduled", () => {
  it("calls purge_old_data", async () => {
    await runScheduled();
    expect(mock.calls.some((c) => c.url.includes("/rest/v1/rpc/purge_old_data"))).toBe(true);
  });
});

describe("misc", () => {
  it("never forwards the raw body", async () => {
    await send(body({ events: [errorEvent({ secret: "TOPSECRET" })], secret_top: "TOPSECRET" }));
    expect(JSON.stringify(mock.rpcBody)).not.toContain("TOPSECRET");
    expect(GOOD_KEY).toBeTruthy();
  });
});
