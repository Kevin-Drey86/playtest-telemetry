import { env } from "cloudflare:workers";
import { createExecutionContext, waitOnExecutionContext } from "cloudflare:test";
import { vi } from "vitest";
import { resetAuthCache, sha256Hex } from "../src/auth";
import worker from "../src/index";
import { resetRateLimiter } from "../src/ratelimit";
import type { IngestResult, Project } from "../src/types";

export const GOOD_KEY = "good-key";
export const WEBHOOK = "https://discord.com/api/webhooks/123456789/abc-DEF_ghi";
export const SESSION_ID = "11111111-1111-4111-8111-111111111111";

export interface Mock {
  calls: { url: string; init?: RequestInit }[];
  project: Project | null;
  projectStatus: number;
  rpcStatus: number;
  rpcBody: unknown;
  rpcResult: IngestResult;
  discordStatus: number;
  storageStatus: number;
  rpcErrorBody?: unknown;
}

export function defaultProject(): Project {
  return { id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", name: "Test Game", discord_webhook_url: WEBHOOK };
}

/** Installs a fetch mock that plays Supabase (REST, RPC, Storage) and Discord. */
export function mockBackend(): Mock {
  const mock: Mock = {
    calls: [],
    project: defaultProject(),
    projectStatus: 200,
    rpcStatus: 200,
    rpcBody: null,
    rpcResult: { inserted: 1, new_issues: [] },
    discordStatus: 204,
    storageStatus: 200,
  };
  const keyHashPromise = sha256Hex(GOOD_KEY);

  vi.spyOn(globalThis, "fetch").mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    mock.calls.push({ url, init });
    if (url.includes("/rest/v1/projects")) {
      if (mock.projectStatus !== 200) return new Response("err", { status: mock.projectStatus });
      const ok = url.includes(`api_key_hash=eq.${await keyHashPromise}`) && mock.project;
      return Response.json(ok ? [mock.project] : []);
    }
    if (url.includes("/rest/v1/rpc/ingest_batch")) {
      mock.rpcBody = JSON.parse(init!.body as string);
      if (mock.rpcStatus !== 200) {
        return Response.json(mock.rpcErrorBody ?? { code: "XX000", message: "boom" }, { status: mock.rpcStatus });
      }
      return Response.json(mock.rpcResult);
    }
    if (url.includes("/rest/v1/rpc/purge_old_data")) return Response.json({ events_deleted: 0 });
    if (url.includes("/storage/v1/object/")) return new Response("{}", { status: mock.storageStatus });
    if (url.startsWith("https://discord.com/")) return new Response(null, { status: mock.discordStatus });
    throw new Error(`unexpected fetch: ${url}`);
  });
  return mock;
}

export function resetState(): void {
  resetAuthCache();
  resetRateLimiter();
}

export function discordCalls(mock: Mock) {
  return mock.calls.filter((c) => c.url.startsWith("https://discord.com/"));
}
export function rpcCalls(mock: Mock) {
  return mock.calls.filter((c) => c.url.includes("/rest/v1/rpc/ingest_batch"));
}
export function projectLookups(mock: Mock) {
  return mock.calls.filter((c) => c.url.includes("/rest/v1/projects"));
}

export function session(overrides: Record<string, unknown> = {}) {
  return {
    id: SESSION_ID,
    device_id: "device-1",
    tester_alias: "alice",
    build_version: "0.1.0",
    platform: "Android",
    device_model: "Pixel 7",
    started_at: Date.now() - 60_000,
    ...overrides,
  };
}

export function errorEvent(overrides: Record<string, unknown> = {}) {
  return {
    client_ts: Date.now(),
    log_type: "Error",
    fingerprint: "abcdef0123456789",
    message: "NullReference at slot 7",
    message_template: "NullReference at slot <n>",
    stack_trace: "at Foo.Bar()",
    repeat_count: 2,
    scene: "Main",
    ...overrides,
  };
}

export function perfSample(overrides: Record<string, unknown> = {}) {
  return { client_ts: Date.now(), scene: "Main", fps_avg: 59, fps_p1_low: 30, frame_ms_max: 50, used_mem_mb: 800, ...overrides };
}

export function body(overrides: Record<string, unknown> = {}) {
  return { session: session(), events: [errorEvent()], perf_samples: [], bug_reports: [], ...overrides };
}

export async function gzip(text: string | Uint8Array): Promise<Uint8Array> {
  const data = typeof text === "string" ? new TextEncoder().encode(text) : text;
  const stream = new Blob([data]).stream().pipeThrough(new CompressionStream("gzip"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

export async function send(
  payload: unknown,
  opts: { key?: string | null; method?: string; path?: string; raw?: BodyInit; headers?: Record<string, string> } = {},
): Promise<{ res: Response; json: any }> {
  const headers: Record<string, string> = { ...opts.headers };
  const key = opts.key === undefined ? GOOD_KEY : opts.key;
  if (key !== null) headers["X-Telemetry-Key"] = key;
  const request = new Request(`https://telemetry.test${opts.path ?? "/v1/ingest"}`, {
    method: opts.method ?? "POST",
    headers,
    body: opts.method === "GET" ? undefined : (opts.raw ?? JSON.stringify(payload)),
  });
  const ctx = createExecutionContext();
  const res = await worker.fetch(request, env as any, ctx);
  await waitOnExecutionContext(ctx);
  const text = await res.text();
  let json: any = null;
  try { json = JSON.parse(text); } catch { /* not json */ }
  return { res, json };
}

export async function runScheduled() {
  const ctx = createExecutionContext();
  await worker.scheduled!({ cron: "17 3 * * *", scheduledTime: Date.now(), type: "scheduled" } as any, env as any, ctx);
  await waitOnExecutionContext(ctx);
}
