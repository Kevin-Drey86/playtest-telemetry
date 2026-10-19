export interface Env {
  SUPABASE_URL: string;
  SUPABASE_SERVICE_ROLE_KEY: string;
  APP_VERSION: string;
}

export interface Project {
  id: string;
  name: string;
  discord_webhook_url: string | null;
}

export type LogType = "Info" | "Warning" | "Error" | "Exception" | "Event";

export interface CleanSession {
  id: string;
  device_id: string;
  tester_alias: string | null;
  build_version: string;
  platform: string;
  device_model: string;
  os: string;
  gpu: string;
  system_memory_mb: number | null;
  screen: string;
  started_at: string;
  last_seen_at: string;
  ended_at: string | null;
  end_reason: "clean" | "abnormal" | "unknown" | null;
}

export interface Breadcrumb {
  ts: number;
  category: string;
  message: string;
}

export type Scalar = string | number | boolean | null;

export interface CleanEvent {
  client_ts: string;
  log_type: LogType;
  fingerprint: string | null;
  message: string;
  repeat_count: number;
  scene: string;
  context: Record<string, string> | null;
  breadcrumbs: Breadcrumb[] | null;
  event_name: string | null;
  props: Record<string, Scalar> | null;
  message_template: string | null;
  stack_trace: string | null;
}

export interface CleanPerfSample {
  client_ts: string;
  scene: string;
  fps_avg: number;
  fps_p1_low: number;
  frame_ms_max: number;
  main_thread_ms_avg: number | null;
  gc_alloc_kb: number | null;
  used_mem_mb: number;
  battery_level: number | null;
  battery_status: string | null;
  thermal_state: string | null;
}

export interface CleanBugReport {
  client_ts: string;
  tester_note: string;
  screenshot_path: string | null;
  breadcrumbs: Breadcrumb[];
  context: Record<string, string>;
}

export interface CleanPayload {
  session: CleanSession;
  events: CleanEvent[];
  perf_samples: CleanPerfSample[];
  bug_reports: CleanBugReport[];
}

export interface NewIssue {
  fingerprint: string;
  log_type: string;
  message_template: string;
  build: string;
}

export interface IngestResult {
  inserted: number;
  new_issues: NewIssue[];
}
