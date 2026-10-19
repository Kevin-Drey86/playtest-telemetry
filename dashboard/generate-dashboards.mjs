// Generates the Grafana dashboard JSON files in provisioning/dashboards/.
// The JSON is committed; edit this file and re-run `npm run generate` rather than editing JSON by hand.
//
// All panels read from the dashboard_reader views (v_issues, v_sessions, v_perf_samples,
// v_perf_by_device, v_issue_events, v_session_timeline) through a datasource *variable*, so the same
// files work with file provisioning (local Docker) and with a manual import into Grafana Cloud.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const outDir = join(dirname(fileURLToPath(import.meta.url)), "provisioning", "dashboards");
mkdirSync(outDir, { recursive: true });

const DS = { type: "grafana-postgresql-datasource", uid: "${ds}" };
const BUILDS = "${build:sqlstring}";
const TAG = "playtest-telemetry";

// ------------------------------------------------------------------ building blocks

const target = (rawSql, format = "table", refId = "A") => ({
  refId, datasource: DS, editorMode: "code", format, rawQuery: true, rawSql,
});

const byName = (name) => ({ id: "byName", options: name });
const override = (name, ...properties) => ({ matcher: byName(name), properties });
const hide = (name) => override(name, { id: "custom.hidden", value: true });
const unit = (name, u) => override(name, { id: "unit", value: u });
const width = (name, w) => override(name, { id: "custom.width", value: w });
const link = (name, title, url) =>
  override(name, { id: "links", value: [{ title, url, targetBlank: false }] });
const colorText = (name, map) =>
  override(
    name,
    { id: "custom.cellOptions", value: { type: "color-text" } },
    {
      id: "mappings",
      value: [{
        type: "value",
        options: Object.fromEntries(Object.entries(map).map(([k, color], index) => [k, { color, index, text: k }])),
      }],
    },
  );
const redWhenAbove = (name, threshold) =>
  override(
    name,
    { id: "custom.cellOptions", value: { type: "color-text" } },
    { id: "thresholds", value: { mode: "absolute", steps: [{ color: "text", value: null }, { color: "red", value: threshold }] } },
  );
const redWhenBelow = (name, threshold) =>
  override(
    name,
    { id: "custom.cellOptions", value: { type: "color-text" } },
    { id: "thresholds", value: { mode: "absolute", steps: [{ color: "red", value: null }, { color: "text", value: threshold }] } },
  );

let nextId = 1;
const panel = (type, title, gridPos, extra) => ({
  id: nextId++, type, title, datasource: DS, gridPos, ...extra,
});

const table = (title, gridPos, sql, { overrides = [], description, filterable = true, sortBy } = {}) =>
  panel("table", title, gridPos, {
    description,
    targets: [target(sql)],
    fieldConfig: { defaults: { custom: { align: "auto", cellOptions: { type: "auto" }, filterable } }, overrides },
    options: { showHeader: true, cellHeight: "sm", sortBy: sortBy ?? [] },
  });

const stat = (title, gridPos, sql, { unit: u, steps, description } = {}) =>
  panel("stat", title, gridPos, {
    description,
    targets: [target(sql)],
    fieldConfig: {
      defaults: {
        unit: u,
        thresholds: { mode: "absolute", steps: steps ?? [{ color: "blue", value: null }] },
        color: { mode: "thresholds" },
      },
      overrides: [],
    },
    options: {
      reduceOptions: { calcs: ["lastNotNull"], fields: "", values: false },
      colorMode: "value", graphMode: "none", justifyMode: "auto", textMode: "auto",
    },
  });

const timeseries = (title, gridPos, sql, { unit: u, stack = false, description } = {}) =>
  panel("timeseries", title, gridPos, {
    description,
    targets: [target(sql, "time_series")],
    fieldConfig: {
      defaults: {
        unit: u,
        custom: {
          drawStyle: stack ? "bars" : "line", lineWidth: 1, fillOpacity: stack ? 60 : 10, showPoints: "auto",
          spanNulls: true, stacking: { mode: stack ? "normal" : "none", group: "A" },
        },
      },
      overrides: [],
    },
    options: { legend: { displayMode: "list", placement: "bottom", showLegend: true }, tooltip: { mode: "multi", sort: "desc" } },
  });

const barchart = (title, gridPos, sql, xField, { unit: u, description } = {}) =>
  panel("barchart", title, gridPos, {
    description,
    targets: [target(sql)],
    fieldConfig: { defaults: { unit: u, custom: { fillOpacity: 80, lineWidth: 1 } }, overrides: [] },
    options: {
      xField, orientation: "horizontal", stacking: "none", showValue: "auto", barWidth: 0.8, groupWidth: 0.7,
      legend: { displayMode: "list", placement: "bottom", showLegend: true }, tooltip: { mode: "multi", sort: "none" },
    },
  });

const pie = (title, gridPos, sql, { description } = {}) =>
  panel("piechart", title, gridPos, {
    description,
    targets: [target(sql)],
    fieldConfig: { defaults: {}, overrides: [] },
    options: {
      pieType: "pie", displayLabels: ["name", "percent"], legend: { displayMode: "table", placement: "right", showLegend: true, values: ["value"] },
      reduceOptions: { values: true, calcs: [], fields: "/^value$/" }, tooltip: { mode: "single" },
    },
  });

const barGauge = (title, gridPos, sql, { unit: u, max, description } = {}) =>
  panel("bargauge", title, gridPos, {
    description,
    targets: [target(sql)],
    fieldConfig: {
      defaults: {
        unit: u, min: 0, max,
        thresholds: { mode: "absolute", steps: [{ color: "green", value: null }, { color: "orange", value: 15 }, { color: "red", value: 40 }] },
      },
      overrides: [],
    },
    options: {
      orientation: "horizontal", displayMode: "gradient", reduceOptions: { values: true, calcs: [], fields: "/^pct_hot$/" },
      namePlacement: "left", showUnfilled: true,
    },
  });

// ------------------------------------------------------------------ variables

const dsVar = {
  name: "ds", label: "Data source", type: "datasource", query: "grafana-postgresql-datasource",
  refresh: 1, hide: 0, includeAll: false, multi: false, options: [], current: {},
};

const queryVar = (name, label, query, { multi = false, includeAll = false } = {}) => ({
  name, label, type: "query", datasource: DS, query, definition: query, refresh: 1, sort: 0,
  multi, includeAll, hide: 0, options: [], current: {},
  ...(includeAll ? { allValue: null } : {}),
});

const projectVar = queryVar(
  "project", "Project",
  "select project_name as __text, project_id::text as __value from v_sessions group by 1, 2 order by 1",
);
const buildVar = queryVar(
  "build", "Build",
  "select distinct build_version from v_sessions where project_id = '$project' order by 1 desc",
  { multi: true, includeAll: true },
);

const navLinks = [{
  title: "Playtest Telemetry", type: "dashboards", tags: [TAG], asDropdown: true,
  includeVars: true, keepTime: true, icon: "external link", targetBlank: false,
}];

const dashboard = (uid, title, description, templating, panels) => ({
  uid, title, description, tags: [TAG], timezone: "browser", editable: true, graphTooltip: 1,
  schemaVersion: 39, version: 1, refresh: "1m", time: { from: "now-7d", to: "now" },
  templating: { list: templating }, links: navLinks, annotations: { list: [] }, panels,
});

const write = (file, dash) => {
  writeFileSync(join(outDir, file), JSON.stringify(dash, null, 2) + "\n");
  console.log("wrote", file, `(${dash.panels.length} panels)`);
};

const VARS = "var-project=${project}&var-build=${build}&${__url_time_range}";

// ------------------------------------------------------------------ 1. Issues
nextId = 1;
write("issues.json", dashboard(
  "pt-issues", "Issues", "Unique errors, warnings and exceptions. Click an issue to see its events.",
  [dsVar, projectVar, buildVar],
  [
    stat("Issues seen in range", { x: 0, y: 0, w: 6, h: 4 },
      `select count(*) as issues from v_issues where project_id = '$project' and $__timeFilter(last_seen_at)`),
    stat("New issues in range", { x: 6, y: 0, w: 6, h: 4 },
      `select count(*) as new_issues from v_issues where project_id = '$project' and $__timeFilter(first_seen_at)`,
      { steps: [{ color: "green", value: null }, { color: "orange", value: 1 }, { color: "red", value: 5 }] }),
    stat("Occurrences (24h)", { x: 12, y: 0, w: 6, h: 4 },
      `select coalesce(sum(count_24h), 0) as occurrences from v_issues where project_id = '$project'`),
    stat("Sessions with errors (24h)", { x: 18, y: 0, w: 6, h: 4 },
      `select count(*) as sessions from v_sessions where project_id = '$project' and error_count > 0 and started_at > now() - interval '24 hours'`),
    table("Issues", { x: 0, y: 4, w: 24, h: 18 },
      `select
  message_template as "Issue", log_type as "Type", total_count as "Total",
  count_24h as "24h", count_7d as "7d", device_count as "Devices", session_count as "Sessions",
  first_seen_build as "First build", last_seen_build as "Last build",
  first_seen_at as "First seen", last_seen_at as "Last seen", fingerprint
from v_issues
where project_id = '$project'
  and (first_seen_build in (${BUILDS}) or last_seen_build in (${BUILDS}))
  and $__timeFilter(last_seen_at)
order by last_seen_at desc`,
      {
        description: "Click an issue to drill into its events. Counts for devices/sessions/24h/7d only cover events still inside the retention window.",
        overrides: [
          hide("fingerprint"),
          width("Issue", 420),
          colorText("Type", { Exception: "red", Error: "orange", Warning: "yellow" }),
          link("Issue", "Open issue events", "/d/pt-issue-detail?var-project=${project}&var-fingerprint=${__data.fields.fingerprint}&${__url_time_range}"),
        ],
      }),
  ],
));

// ------------------------------------------------------------------ 2. Sessions
nextId = 1;
const sessionLink = "/d/pt-timeline?var-project=${project}&var-session=${__data.fields.session_id}&${__url_time_range}";
write("sessions.json", dashboard(
  "pt-sessions", "Sessions", "One row per app launch. Abnormal ends (crash, kill, OOM) are highlighted. Click a row to open its timeline.",
  [dsVar, projectVar, buildVar],
  [
    stat("Sessions", { x: 0, y: 0, w: 6, h: 4 },
      `select count(*) as sessions from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(started_at)`),
    stat("Abnormal ends", { x: 6, y: 0, w: 6, h: 4 },
      `select count(*) as abnormal from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and end_reason = 'abnormal' and $__timeFilter(started_at)`,
      { steps: [{ color: "green", value: null }, { color: "red", value: 1 }] }),
    stat("Avg session length", { x: 12, y: 0, w: 6, h: 4 },
      `select coalesce(avg(duration_s), 0) as avg_duration from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(started_at)`,
      { unit: "s" }),
    stat("Testers", { x: 18, y: 0, w: 6, h: 4 },
      `select count(distinct coalesce(tester_alias, device_id)) as testers from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(started_at)`),
    table("Sessions", { x: 0, y: 4, w: 24, h: 18 },
      `select
  started_at as "Started", coalesce(tester_alias, '(none)') as "Tester", device_model as "Device", os as "OS",
  build_version as "Build", duration_s as "Duration", error_count as "Errors",
  worst_fps_p1_low as "Worst FPS (1% low)", peak_memory_mb as "Peak memory (MB)",
  coalesce(end_reason, 'running') as "End", session_id
from v_sessions
where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(started_at)
order by started_at desc`,
      {
        overrides: [
          hide("session_id"),
          unit("Duration", "s"),
          redWhenAbove("Errors", 1),
          redWhenBelow("Worst FPS (1% low)", 20),
          colorText("End", { abnormal: "red", clean: "green", running: "blue", unknown: "orange" }),
          link("Started", "Open session timeline", sessionLink),
          link("Tester", "Open session timeline", sessionLink),
        ],
      }),
  ],
));

// ------------------------------------------------------------------ 3. Session timeline
nextId = 1;
const sessionVar = queryVar(
  "session", "Session",
  `select to_char(started_at, 'MM-DD HH24:MI') || ' · ' || coalesce(tester_alias, '?') || ' · ' || device_model || ' · ' || build_version as __text, session_id::text as __value from v_sessions where project_id = '$project' order by started_at desc limit 200`,
);
write("session-timeline.json", dashboard(
  "pt-timeline", "Session timeline",
  "Everything that happened in one session in time order: logs, custom events, perf samples and bug reports. Set the time range to cover the session.",
  [dsVar, projectVar, sessionVar],
  [
    table("Session", { x: 0, y: 0, w: 24, h: 4 },
      `select started_at as "Started", coalesce(tester_alias, '(none)') as "Tester", device_model as "Device", os as "OS", platform as "Platform", build_version as "Build", duration_s as "Duration", error_count as "Errors", coalesce(end_reason, 'running') as "End" from v_sessions where session_id = '$session'`,
      { filterable: false, overrides: [unit("Duration", "s"), colorText("End", { abnormal: "red", clean: "green", running: "blue", unknown: "orange" })] }),
    timeseries("FPS", { x: 0, y: 4, w: 12, h: 8 },
      `select ts as time, fps_avg as "FPS (avg)", fps_p1_low as "FPS (1% low)" from v_session_timeline where session_id = '$session' and kind = 'perf' order by 1`,
      { unit: "none" }),
    timeseries("Memory", { x: 12, y: 4, w: 12, h: 8 },
      `select ts as time, used_mem_mb as "Used memory (MB)" from v_session_timeline where session_id = '$session' and kind = 'perf' order by 1`,
      { unit: "decmbytes" }),
    table("Timeline", { x: 0, y: 12, w: 24, h: 16 },
      `select ts as "Time", kind as "Kind", coalesce(log_type, '') as "Level", title as "What", detail as "Detail", repeat_count as "Repeats", scene as "Scene"
from v_session_timeline where session_id = '$session' order by ts`,
      {
        description: "Use the column filters to focus on one kind (log, event, perf, bug_report).",
        overrides: [
          width("Time", 190), width("Kind", 100), width("Level", 90), width("What", 200),
          colorText("Kind", { log: "orange", event: "blue", perf: "green", bug_report: "purple" }),
          colorText("Level", { Exception: "red", Error: "orange", Warning: "yellow" }),
        ],
        sortBy: [{ displayName: "Time", desc: false }],
      }),
  ],
));

// ------------------------------------------------------------------ 4. Performance
nextId = 1;
write("performance.json", dashboard(
  "pt-performance", "Performance",
  "Compare builds and devices. FPS 1% low and memory are percentiles over all retained perf samples.",
  [dsVar, projectVar, buildVar],
  [
    barchart("FPS (1% low) by device and build", { x: 0, y: 0, w: 12, h: 9 },
      `select device_model || ' · ' || build_version as "Device · Build", round(fps_p1_low_p50::numeric, 1) as "median 1% low", round(fps_p1_low_p05::numeric, 1) as "p5 1% low", round(fps_avg_p50::numeric, 1) as "median avg FPS"
from v_perf_by_device where project_id = '$project' and build_version in (${BUILDS}) order by 1`,
      "Device · Build", { description: "Higher is better. A drop between builds of the same device is a regression." }),
    barchart("Memory by device and build", { x: 12, y: 0, w: 12, h: 9 },
      `select device_model || ' · ' || build_version as "Device · Build", round(mem_mb_p50::numeric) as "median", round(mem_mb_p95::numeric) as "p95", mem_mb_max as "max"
from v_perf_by_device where project_id = '$project' and build_version in (${BUILDS}) order by 1`,
      "Device · Build", { unit: "decmbytes", description: "Lower is better." }),
    table("Build-over-build changes (per device)", { x: 0, y: 9, w: 24, h: 8 },
      `select device_model as "Device", build_version as "Build", round(fps_p1_low_p50::numeric, 1) as "FPS 1% low",
  round((fps_p1_low_p50 - lag(fps_p1_low_p50) over w)::numeric, 1) as "FPS change",
  round(mem_mb_p95::numeric) as "Memory p95 (MB)", round((mem_mb_p95 - lag(mem_mb_p95) over w)::numeric) as "Memory change (MB)",
  sample_count as "Samples"
from v_perf_by_device where project_id = '$project'
window w as (partition by device_model order by build_version)
order by device_model, build_version`,
      {
        description: "Change versus the previous build of the same device (builds sorted by version string). Red = worse.",
        overrides: [redWhenBelow("FPS change", 0), redWhenAbove("Memory change (MB)", 1)],
      }),
    timeseries("FPS (1% low) over time, by build", { x: 0, y: 17, w: 12, h: 8 },
      `select $__timeGroupAlias(client_ts, '1h'), build_version as metric, avg(fps_p1_low) as "FPS 1% low"
from v_perf_samples where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(client_ts) group by 1, 2 order by 1`),
    pie("Thermal state distribution", { x: 12, y: 17, w: 6, h: 8 },
      `select coalesce(thermal_state, 'unknown') as metric, count(*) as value
from v_perf_samples where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(client_ts) group by 1 order by 2 desc`),
    barGauge("Time spent hot (Serious or worse)", { x: 18, y: 17, w: 6, h: 8 },
      `select device_model || ' · ' || build_version as label,
  round(100.0 * count(*) filter (where thermal_state in ('Serious', 'Severe', 'Critical', 'Emergency', 'Shutdown')) / count(*), 1) as pct_hot
from v_perf_samples where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(client_ts) group by 1 order by 1`,
      { unit: "percent", max: 100, description: "Share of perf samples where the device reported Serious/Severe/Critical thermal state (Android: Severe+, iOS: Serious+)." }),
  ],
));

// ------------------------------------------------------------------ 5. Playtest overview
nextId = 1;
write("overview.json", dashboard(
  "pt-overview", "Playtest overview", "The at-a-glance view for a running playtest.",
  [dsVar, projectVar, buildVar],
  [
    stat("Active testers today", { x: 0, y: 0, w: 6, h: 5 },
      `select count(distinct coalesce(tester_alias, device_id)) as testers from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and last_seen_at >= date_trunc('day', now())`,
      { description: "Distinct testers with a session that was active since 00:00 UTC." }),
    stat("New issues today", { x: 6, y: 0, w: 6, h: 5 },
      `select count(*) as new_issues from v_issues where project_id = '$project' and first_seen_at >= date_trunc('day', now())`,
      { steps: [{ color: "green", value: null }, { color: "orange", value: 1 }, { color: "red", value: 5 }] }),
    stat("Abnormal session rate", { x: 12, y: 0, w: 6, h: 5 },
      `select coalesce(round(100.0 * count(*) filter (where end_reason = 'abnormal') / nullif(count(*), 0), 1), 0) as abnormal_pct
from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(started_at)`,
      { unit: "percent", steps: [{ color: "green", value: null }, { color: "orange", value: 5 }, { color: "red", value: 15 }], description: "Sessions in the selected time range that ended in a crash, kill or OOM." }),
    stat("Median FPS", { x: 18, y: 0, w: 6, h: 5 },
      `select round((percentile_cont(0.5) within group (order by fps_avg))::numeric, 1) as median_fps
from v_perf_samples where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(client_ts)`,
      { steps: [{ color: "red", value: null }, { color: "orange", value: 30 }, { color: "green", value: 55 }] }),
    timeseries("Sessions per day, by build", { x: 0, y: 5, w: 12, h: 8 },
      `select $__timeGroupAlias(started_at, '1d'), build_version as metric, count(*) as sessions
from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(started_at) group by 1, 2 order by 1`,
      { stack: true }),
    timeseries("Errors per day, by build", { x: 12, y: 5, w: 12, h: 8 },
      `select $__timeGroupAlias(started_at, '1d'), build_version as metric, sum(error_count) as errors
from v_sessions where project_id = '$project' and build_version in (${BUILDS}) and $__timeFilter(started_at) group by 1, 2 order by 1`,
      { stack: true }),
    table("Newest issues", { x: 0, y: 13, w: 24, h: 9 },
      `select message_template as "Issue", log_type as "Type", total_count as "Total", first_seen_build as "First build", first_seen_at as "First seen", fingerprint
from v_issues where project_id = '$project' order by first_seen_at desc limit 10`,
      {
        filterable: false,
        overrides: [
          hide("fingerprint"), width("Issue", 420),
          colorText("Type", { Exception: "red", Error: "orange", Warning: "yellow" }),
          link("Issue", "Open issue events", "/d/pt-issue-detail?var-project=${project}&var-fingerprint=${__data.fields.fingerprint}&${__url_time_range}"),
        ],
      }),
  ],
));

// ------------------------------------------------------------------ 6. Issue detail (drill-down target)
nextId = 1;
const fingerprintVar = queryVar(
  "fingerprint", "Issue",
  `select left(message_template, 90) || ' (' || log_type || ')' as __text, fingerprint as __value from v_issues where project_id = '$project' order by last_seen_at desc limit 300`,
);
write("issue-detail.json", dashboard(
  "pt-issue-detail", "Issue detail", "Every occurrence of one issue, with the stack trace stored once on the issue. Click a row to open its session.",
  [dsVar, projectVar, fingerprintVar],
  [
    table("Issue", { x: 0, y: 0, w: 24, h: 5 },
      `select message_template as "Message", log_type as "Type", total_count as "Total", device_count as "Devices", session_count as "Sessions", first_seen_build as "First build", last_seen_build as "Last build", first_seen_at as "First seen", last_seen_at as "Last seen"
from v_issues where project_id = '$project' and fingerprint = '$fingerprint'`,
      { filterable: false, overrides: [colorText("Type", { Exception: "red", Error: "orange", Warning: "yellow" })] }),
    table("Stack trace", { x: 0, y: 5, w: 24, h: 7 },
      `select regexp_split_to_table(stack_trace, E'\\n') as "Frame" from v_issues where project_id = '$project' and fingerprint = '$fingerprint'`,
      { filterable: false, description: "Stored once per issue (first occurrence), not per event." }),
    table("Occurrences", { x: 0, y: 12, w: 24, h: 16 },
      `select client_ts as "Time", message as "Message", repeat_count as "Repeats", build_version as "Build", device_model as "Device", coalesce(tester_alias, '(none)') as "Tester", scene as "Scene",
  context::text as "Context", breadcrumbs::text as "Breadcrumbs", session_id
from v_issue_events where project_id = '$project' and fingerprint = '$fingerprint' order by client_ts desc limit 500`,
      {
        overrides: [
          hide("session_id"), width("Time", 190), width("Message", 380),
          link("Time", "Open session timeline", "/d/pt-timeline?var-project=${project}&var-session=${__data.fields.session_id}&${__url_time_range}"),
        ],
      }),
  ],
));
