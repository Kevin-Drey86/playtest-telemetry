// Payload validation. Every row is REBUILT from whitelisted fields: the raw body is never forwarded.
// Strings are truncated to fixed limits and numbers clamped. Malformed items are dropped and
// counted instead of failing the whole batch; only a missing/invalid session fails the request.

import type {
  Breadcrumb,
  CleanBugReport,
  CleanEvent,
  CleanPayload,
  CleanPerfSample,
  CleanSession,
  LogType,
  Scalar,
} from "./types";

export const MAX_ITEMS = 500;

const LIMITS = {
  message: 512,
  template: 512,
  stack: 8192,
  scene: 128,
  name: 64,
  shortStr: 128,
  note: 2000,
  ctxKeys: 32,
  ctxKey: 64,
  ctxValue: 256,
  propsKeys: 20,
  propValue: 256,
  crumbs: 100,
  crumbCategory: 32,
  crumbMessage: 256,
  screenshotB64: 600_000, // ~450 KB of JPEG
} as const;

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const FINGERPRINT_RE = /^[0-9a-f]{1,32}$/i;
const LOG_TYPES: ReadonlySet<string> = new Set(["Info", "Warning", "Error", "Exception", "Event"]);
const END_REASONS: ReadonlySet<string> = new Set(["clean", "abnormal", "unknown"]);
const FUTURE_SKEW_MS = 60_000;

type Obj = Record<string, unknown>;

export interface ValidationOk {
  ok: true;
  payload: CleanPayload;
  /** base64 JPEG per bug report (same index as payload.bug_reports), or null. */
  screenshots: (string | null)[];
  accepted: number;
  dropped: number;
}
export interface ValidationError {
  ok: false;
  status: number;
  error: string;
}

function isObj(v: unknown): v is Obj {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function str(v: unknown, max: number): string {
  if (typeof v === "string") return v.length > max ? v.slice(0, max) : v;
  if (typeof v === "number" || typeof v === "boolean") return String(v).slice(0, max);
  return "";
}

function optStr(v: unknown, max: number): string | null {
  const s = str(v, max);
  return s === "" ? null : s;
}

function num(v: unknown, min: number, max: number): number | null {
  if (typeof v !== "number" || !Number.isFinite(v)) return null;
  return Math.min(max, Math.max(min, v));
}

function int(v: unknown, min: number, max: number): number | null {
  const n = num(v, min, max);
  return n === null ? null : Math.round(n);
}

/** Accepts epoch milliseconds or an ISO string. Missing/invalid -> now. Future values are clamped. */
function ts(v: unknown, now: number): string {
  let ms: number;
  if (typeof v === "number" && Number.isFinite(v)) ms = v;
  else if (typeof v === "string" && v.length < 40) ms = Date.parse(v);
  else ms = NaN;
  if (!Number.isFinite(ms) || ms < 0) ms = now;
  if (ms > now + FUTURE_SKEW_MS) ms = now;
  return new Date(ms).toISOString();
}

function optTs(v: unknown, now: number): string | null {
  return v === undefined || v === null || v === "" ? null : ts(v, now);
}

function stringMap(v: unknown): Record<string, string> | null {
  if (!isObj(v)) return null;
  const out: Record<string, string> = {};
  let n = 0;
  for (const k of Object.keys(v)) {
    if (n >= LIMITS.ctxKeys) break;
    out[k.slice(0, LIMITS.ctxKey)] = str(v[k], LIMITS.ctxValue);
    n++;
  }
  return out;
}

function scalarMap(v: unknown): Record<string, Scalar> | null {
  if (!isObj(v)) return null;
  const out: Record<string, Scalar> = {};
  let n = 0;
  for (const k of Object.keys(v)) {
    if (n >= LIMITS.propsKeys) break;
    const val = v[k];
    if (typeof val === "string") out[k.slice(0, LIMITS.ctxKey)] = val.slice(0, LIMITS.propValue);
    else if (typeof val === "number" && Number.isFinite(val)) out[k.slice(0, LIMITS.ctxKey)] = val;
    else if (typeof val === "boolean" || val === null) out[k.slice(0, LIMITS.ctxKey)] = val;
    else continue; // nested objects/arrays are not supported in props
    n++;
  }
  return out;
}

function breadcrumbs(v: unknown, now: number): Breadcrumb[] {
  if (!Array.isArray(v)) return [];
  const out: Breadcrumb[] = [];
  for (const c of v.slice(-LIMITS.crumbs)) {
    if (!isObj(c)) continue;
    const message = str(c.message, LIMITS.crumbMessage);
    if (message === "") continue;
    out.push({
      ts: Date.parse(ts(c.ts, now)),
      category: str(c.category, LIMITS.crumbCategory),
      message,
    });
  }
  return out;
}

function validateSession(raw: unknown, now: number): CleanSession | null {
  if (!isObj(raw)) return null;
  const id = typeof raw.id === "string" ? raw.id.toLowerCase() : "";
  if (!UUID_RE.test(id)) return null;
  const deviceId = str(raw.device_id, LIMITS.shortStr);
  if (deviceId === "") return null;
  const endReason = typeof raw.end_reason === "string" && END_REASONS.has(raw.end_reason) ? raw.end_reason : null;
  const started = ts(raw.started_at, now);
  return {
    id,
    device_id: deviceId,
    tester_alias: optStr(raw.tester_alias, LIMITS.name),
    build_version: str(raw.build_version, LIMITS.shortStr),
    platform: str(raw.platform, 32),
    device_model: str(raw.device_model, LIMITS.shortStr),
    os: str(raw.os, LIMITS.shortStr),
    gpu: str(raw.gpu, LIMITS.shortStr),
    system_memory_mb: int(raw.system_memory_mb, 0, 1_000_000),
    screen: str(raw.screen, 32),
    started_at: started,
    last_seen_at: raw.last_seen_at === undefined ? new Date(now).toISOString() : ts(raw.last_seen_at, now),
    ended_at: optTs(raw.ended_at, now),
    end_reason: endReason as CleanSession["end_reason"],
  };
}

function validateEvent(raw: unknown, now: number): CleanEvent | null {
  if (!isObj(raw)) return null;
  const eventName = optStr(raw.event_name, LIMITS.name);
  let logType: string = typeof raw.log_type === "string" ? raw.log_type : "";
  if (eventName !== null) logType = "Event";
  if (!LOG_TYPES.has(logType)) return null;
  if (logType === "Event" && eventName === null) return null;

  const message = str(raw.message, LIMITS.message);
  if (message === "" && eventName === null) return null;

  const isIssueType = logType === "Warning" || logType === "Error" || logType === "Exception";
  const isError = logType === "Error" || logType === "Exception";
  const fp = isIssueType && typeof raw.fingerprint === "string" && FINGERPRINT_RE.test(raw.fingerprint)
    ? raw.fingerprint.toLowerCase()
    : null;

  return {
    client_ts: ts(raw.client_ts, now),
    log_type: logType as LogType,
    fingerprint: fp,
    message,
    repeat_count: int(raw.repeat_count, 1, 1_000_000) ?? 1,
    scene: str(raw.scene, LIMITS.scene),
    context: isError ? stringMap(raw.context) : null,
    breadcrumbs: isError ? breadcrumbs(raw.breadcrumbs, now) : null,
    event_name: eventName,
    props: eventName !== null ? scalarMap(raw.props) : null,
    // Issue data: consumed by ingest_batch for the issues table, never stored on events.
    message_template: fp !== null ? optStr(raw.message_template, LIMITS.template) : null,
    stack_trace: fp !== null ? optStr(raw.stack_trace, LIMITS.stack) : null,
  };
}

function validatePerf(raw: unknown, now: number): CleanPerfSample | null {
  if (!isObj(raw)) return null;
  const fpsAvg = num(raw.fps_avg, 0, 10_000);
  const fpsLow = num(raw.fps_p1_low, 0, 10_000);
  const frameMax = num(raw.frame_ms_max, 0, 3_600_000);
  const mem = int(raw.used_mem_mb, 0, 1_000_000);
  if (fpsAvg === null || fpsLow === null || frameMax === null || mem === null) return null;
  const battery = num(raw.battery_level, -1, 1);
  return {
    client_ts: ts(raw.client_ts, now),
    scene: str(raw.scene, LIMITS.scene),
    fps_avg: fpsAvg,
    fps_p1_low: fpsLow,
    frame_ms_max: frameMax,
    main_thread_ms_avg: num(raw.main_thread_ms_avg, 0, 3_600_000),
    gc_alloc_kb: num(raw.gc_alloc_kb, 0, 1_000_000_000),
    used_mem_mb: mem,
    battery_level: battery === null || battery < 0 ? null : battery, // Unity reports -1 when unknown
    battery_status: optStr(raw.battery_status, 32),
    thermal_state: optStr(raw.thermal_state, 32),
  };
}

function validateBug(raw: unknown, now: number): { report: CleanBugReport; screenshot: string | null } | null {
  if (!isObj(raw)) return null;
  const note = str(raw.tester_note, LIMITS.note);
  const shot =
    typeof raw.screenshot_b64 === "string" && raw.screenshot_b64.length > 0 &&
    raw.screenshot_b64.length <= LIMITS.screenshotB64
      ? raw.screenshot_b64
      : null;
  if (note === "" && shot === null) return null;
  return {
    report: {
      client_ts: ts(raw.client_ts, now),
      tester_note: note,
      screenshot_path: null, // set by the Worker after a successful Storage upload, never by the client
      breadcrumbs: breadcrumbs(raw.breadcrumbs, now),
      context: stringMap(raw.context) ?? {},
    },
    screenshot: shot,
  };
}

export function validateBatch(raw: unknown, now: number = Date.now()): ValidationOk | ValidationError {
  if (!isObj(raw)) return { ok: false, status: 400, error: "body must be a JSON object" };

  const session = validateSession(raw.session, now);
  if (!session) return { ok: false, status: 400, error: "invalid or missing session" };

  const rawEvents = Array.isArray(raw.events) ? raw.events : [];
  const rawPerf = Array.isArray(raw.perf_samples) ? raw.perf_samples : [];
  const rawBugs = Array.isArray(raw.bug_reports) ? raw.bug_reports : [];
  if (rawEvents.length + rawPerf.length + rawBugs.length > MAX_ITEMS) {
    return { ok: false, status: 413, error: `batch exceeds ${MAX_ITEMS} items` };
  }

  const events: CleanEvent[] = [];
  const perf: CleanPerfSample[] = [];
  const bugs: CleanBugReport[] = [];
  const screenshots: (string | null)[] = [];
  let dropped = 0;

  for (const e of rawEvents) {
    const v = validateEvent(e, now);
    if (v) events.push(v);
    else dropped++;
  }
  for (const p of rawPerf) {
    const v = validatePerf(p, now);
    if (v) perf.push(v);
    else dropped++;
  }
  for (const b of rawBugs) {
    const v = validateBug(b, now);
    if (v) {
      bugs.push(v.report);
      screenshots.push(v.screenshot);
    } else dropped++;
  }

  return {
    ok: true,
    payload: { session, events, perf_samples: perf, bug_reports: bugs },
    screenshots,
    accepted: events.length + perf.length + bugs.length,
    dropped,
  };
}
