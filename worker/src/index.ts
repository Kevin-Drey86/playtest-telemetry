import { authenticate } from "./auth";
import { BadEncoding, TooLarge, readBody } from "./body";
import { sendNewIssueAlert } from "./discord";
import { checkRateLimit } from "./ratelimit";
import { BatchRejected, SupabaseUnavailable, ingestBatch, purgeOldData, uploadScreenshot } from "./supabase";
import type { Env } from "./types";
import { validateBatch } from "./validate";

// TODO(webgl): add CORS headers (and an OPTIONS handler) here if a WebGL target is ever added.
// Mobile builds do not need CORS, so it is intentionally not enabled.

function json(body: unknown, status = 200, extra: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...extra },
  });
}

async function handleIngest(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  // 1. Auth
  let project;
  try {
    project = await authenticate(request.headers.get("X-Telemetry-Key"), env);
  } catch (e) {
    console.error(`auth lookup failed: ${(e as Error).message}`);
    return json({ success: false, error: "backend unavailable" }, 503);
  }
  if (!project) return json({ success: false, error: "unauthorized" }, 401);

  // 2. Size guard + gzip
  let bytes: Uint8Array;
  try {
    bytes = await readBody(request);
  } catch (e) {
    if (e instanceof TooLarge) return json({ success: false, error: "payload too large" }, 413);
    if (e instanceof BadEncoding) return json({ success: false, error: "bad content encoding" }, 400);
    throw e;
  }

  let raw: unknown;
  try {
    raw = JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    return json({ success: false, error: "invalid JSON" }, 400);
  }

  // 3. Validation: rebuild every row from whitelisted fields
  const result = validateBatch(raw);
  if (!result.ok) return json({ success: false, error: result.error }, result.status);
  const { payload, screenshots, accepted, dropped } = result;

  // 4. Rate limit per project + device
  const retryAfter = checkRateLimit(`${project.id}:${payload.session.device_id}`);
  if (retryAfter !== null) {
    return json({ success: false, error: "rate limited" }, 429, { "Retry-After": String(retryAfter) });
  }

  // Screenshots (rare): upload first so the row can reference the stored object.
  if (screenshots.some((s) => s !== null)) {
    await Promise.all(
      screenshots.map(async (b64, i) => {
        if (b64) payload.bug_reports[i].screenshot_path = await uploadScreenshot(env, project.id, payload.session.id, b64);
      }),
    );
  }

  // 5. Write: one RPC
  let ingest;
  try {
    ingest = await ingestBatch(env, project.id, payload);
  } catch (e) {
    if (e instanceof BatchRejected) {
      console.error(`batch rejected: ${e.message}`);
      return json({ success: false, error: e.message }, e.status);
    }
    console.error(`ingest failed: ${(e as Error).message}`);
    return json({ success: false, error: "backend unavailable" }, 503);
  }

  // 6. Alerts: only for issues that did not exist before this call
  if (ingest.new_issues.length > 0 && project.discord_webhook_url) {
    ctx.waitUntil(sendNewIssueAlert(project, payload.session, ingest.new_issues));
  }

  return json({ success: true, accepted, dropped });
}

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const { pathname } = new URL(request.url);

    if (pathname === "/v1/ingest") {
      if (request.method !== "POST") return json({ error: "method not allowed" }, 405, { Allow: "POST" });
      try {
        return await handleIngest(request, env, ctx);
      } catch (e) {
        console.error(`unhandled: ${(e as Error).message}`);
        return json({ success: false, error: "internal error" }, 500);
      }
    }
    if (pathname === "/v1/health") {
      if (request.method !== "GET") return json({ error: "method not allowed" }, 405, { Allow: "GET" });
      return json({ ok: true, version: env.APP_VERSION });
    }
    return json({ error: "not found" }, 404);
  },

  async scheduled(_event: ScheduledController, env: Env, ctx: ExecutionContext): Promise<void> {
    ctx.waitUntil(
      purgeOldData(env).then(
        (r) => console.log(`purge_old_data: ${JSON.stringify(r)}`),
        (e) => console.error(`purge_old_data failed: ${(e as Error).message}`),
      ),
    );
  },
} satisfies ExportedHandler<Env>;
