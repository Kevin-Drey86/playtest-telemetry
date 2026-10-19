import type { CleanPayload, Env, IngestResult, Project } from "./types";

const TIMEOUT_MS = 8000;

/** Supabase could not be reached or answered with a server-side/config error: the client should retry. */
export class SupabaseUnavailable extends Error {}

/** Supabase rejected the batch itself (bad data / ownership conflict): retrying will not help. */
export class BatchRejected extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

function headers(env: Env, extra: Record<string, string> = {}): Record<string, string> {
  return {
    apikey: env.SUPABASE_SERVICE_ROLE_KEY,
    Authorization: `Bearer ${env.SUPABASE_SERVICE_ROLE_KEY}`,
    ...extra,
  };
}

async function call(url: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(url, { ...init, signal: AbortSignal.timeout(TIMEOUT_MS) });
  } catch (e) {
    throw new SupabaseUnavailable(`fetch failed: ${(e as Error).message}`);
  }
}

export async function fetchProjectByKeyHash(env: Env, hash: string): Promise<Project | null> {
  const url = `${env.SUPABASE_URL}/rest/v1/projects?api_key_hash=eq.${encodeURIComponent(hash)}` +
    `&select=id,name,discord_webhook_url&limit=1`;
  const res = await call(url, { headers: headers(env) });
  if (!res.ok) throw new SupabaseUnavailable(`project lookup failed: ${res.status}`);
  const rows = (await res.json()) as Project[];
  return rows.length > 0 ? rows[0] : null;
}

export async function rpc<T>(env: Env, fn: string, args: unknown): Promise<T> {
  const res = await call(`${env.SUPABASE_URL}/rest/v1/rpc/${fn}`, {
    method: "POST",
    headers: headers(env, { "Content-Type": "application/json" }),
    body: JSON.stringify(args),
  });
  if (res.ok) return (await res.json()) as T;

  let code = "";
  let message = "";
  try {
    const body = (await res.json()) as { code?: string; message?: string };
    code = body.code ?? "";
    message = body.message ?? "";
  } catch { /* non-JSON error body */ }

  // Data errors (class 22/23) and a session-ownership conflict are the batch's fault: don't retry.
  if (code.startsWith("22") || code.startsWith("23")) throw new BatchRejected(`database rejected batch: ${code}`, 422);
  if (code === "42501" && message.includes("another project")) throw new BatchRejected("session belongs to another project", 409);
  throw new SupabaseUnavailable(`rpc ${fn} failed: ${res.status} ${code}`);
}

export function ingestBatch(env: Env, projectId: string, payload: CleanPayload): Promise<IngestResult> {
  return rpc<IngestResult>(env, "ingest_batch", { p_project_id: projectId, p_payload: payload });
}

export function purgeOldData(env: Env): Promise<unknown> {
  return rpc(env, "purge_old_data", {});
}

function base64ToBytes(b64: string): Uint8Array | null {
  try {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  } catch {
    return null;
  }
}

/**
 * Stores a base64 JPEG in the private `bug-screenshots` bucket.
 * Returns the object path, or null if the image is invalid or the upload failed (the bug report
 * is still ingested, just without a screenshot).
 */
export async function uploadScreenshot(env: Env, projectId: string, sessionId: string, b64: string): Promise<string | null> {
  const bytes = base64ToBytes(b64);
  // JPEG magic number; also guards against arbitrary content being stored.
  if (!bytes || bytes.length < 4 || bytes[0] !== 0xff || bytes[1] !== 0xd8 || bytes[2] !== 0xff) return null;
  const path = `${projectId}/${sessionId}/${Date.now()}-${crypto.randomUUID().slice(0, 8)}.jpg`;
  try {
    const res = await call(`${env.SUPABASE_URL}/storage/v1/object/bug-screenshots/${path}`, {
      method: "POST",
      headers: headers(env, { "Content-Type": "image/jpeg", "x-upsert": "false" }),
      body: bytes,
    });
    return res.ok ? path : null;
  } catch {
    return null;
  }
}
