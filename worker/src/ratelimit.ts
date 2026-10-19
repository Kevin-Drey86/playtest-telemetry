// Best-effort fixed-window rate limiter, keyed by project_id + device_id.
// State is held in memory PER ISOLATE: Cloudflare may run several isolates (and restarts them
// at will), so the effective limit is approximate. That is acceptable here: it only has to stop
// a misbehaving client from burning the 100k requests/day free budget.

export const LIMIT_PER_WINDOW = 30;
export const WINDOW_MS = 60_000;
const MAX_KEYS = 5000;

const windows = new Map<string, { start: number; count: number }>();

export function resetRateLimiter(): void {
  windows.clear();
}

/** Returns null if allowed, otherwise the number of seconds to wait. */
export function checkRateLimit(key: string, now = Date.now()): number | null {
  if (windows.size > MAX_KEYS) {
    for (const [k, w] of windows) if (now - w.start >= WINDOW_MS) windows.delete(k);
    if (windows.size > MAX_KEYS) windows.clear();
  }
  const w = windows.get(key);
  if (!w || now - w.start >= WINDOW_MS) {
    windows.set(key, { start: now, count: 1 });
    return null;
  }
  if (w.count >= LIMIT_PER_WINDOW) {
    return Math.max(1, Math.ceil((w.start + WINDOW_MS - now) / 1000));
  }
  w.count++;
  return null;
}
