import type { Env, Project } from "./types";
import { fetchProjectByKeyHash } from "./supabase";

const POSITIVE_TTL_MS = 5 * 60_000;
const NEGATIVE_TTL_MS = 60_000; // unknown keys: avoid hammering the DB with bad keys
const MAX_ENTRIES = 1000;

interface CacheEntry {
  project: Project | null;
  expires: number;
}

// Module-level cache: lives as long as the isolate. After a key rotation or webhook change the
// old values can be served for up to POSITIVE_TTL_MS (see docs/runbook.md).
const cache = new Map<string, CacheEntry>();

export function resetAuthCache(): void {
  cache.clear();
}

export async function sha256Hex(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  let out = "";
  for (const b of new Uint8Array(digest)) out += b.toString(16).padStart(2, "0");
  return out;
}

/** Returns the project for an API key, null for an unknown key. Throws SupabaseUnavailable on lookup failure. */
export async function authenticate(key: string | null, env: Env, now = Date.now()): Promise<Project | null> {
  if (!key || key.length > 200) return null;
  const hash = await sha256Hex(key);
  const hit = cache.get(hash);
  if (hit && hit.expires > now) return hit.project;

  const project = await fetchProjectByKeyHash(env, hash);
  if (cache.size >= MAX_ENTRIES) cache.clear();
  cache.set(hash, { project, expires: now + (project ? POSITIVE_TTL_MS : NEGATIVE_TTL_MS) });
  return project;
}
