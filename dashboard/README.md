# Dashboards (Grafana)

Six dashboards over the read-only views of the Supabase database. Each has **Project** and **Build**
variables (Build is multi-select) and a *Playtest Telemetry* dropdown in the top bar to jump between them.

| Dashboard | uid | What it answers |
|---|---|---|
| Playtest overview | `pt-overview` | Active testers today, new issues today, abnormal session rate, median FPS, sessions/errors per day |
| Issues | `pt-issues` | Every unique error with counts (total / 24h / 7d), devices, sessions, first/last build. **Click an issue** to drill into its events |
| Issue detail | `pt-issue-detail` | Stack trace (stored once) and every occurrence of one issue. Click a row to open its session |
| Sessions | `pt-sessions` | One row per launch: tester, device, duration, errors, worst FPS, end reason (**abnormal in red**). Click to open the timeline |
| Session timeline | `pt-timeline` | FPS and memory charts above a time-ordered list of logs, events, perf samples and bug reports |
| Performance | `pt-performance` | FPS 1% low and memory by device and build, build-over-build deltas, thermal distribution |

The dashboards only read `v_issues`, `v_sessions`, `v_perf_samples`, `v_perf_by_device`,
`v_issue_events` and `v_session_timeline`, as the `dashboard_reader` role. They cannot see API key
hashes or webhook URLs.

## Prerequisite (both setups): the read-only role

In the Supabase SQL editor:

```sql
alter role dashboard_reader password '<a strong password>';
```

Connection values (Supabase dashboard > **Connect**): use the **Session pooler** (IPv4; the direct
connection is IPv6-only on the free plan). The user name is `dashboard_reader.<project-ref>`.

## Option A: local Docker

```bash
cd dashboard
cp .env.example .env        # fill in the Supabase pooler host, user, password and a Grafana admin password
docker compose up -d
```

Open http://localhost:3000 (login with the admin values from `.env`). The data source and all dashboards
are provisioned from `provisioning/`; the home page is the overview.

## Option B: Grafana Cloud (free tier)

1. **Connections > Add new connection > PostgreSQL.** Host `<pooler host>:5432`, database `postgres`,
   user `dashboard_reader.<project-ref>`, the password, **TLS/SSL mode: require**, version 15+.
   Save & test.
2. **Dashboards > New > Import**, and upload each file from `provisioning/dashboards/*.json`
   (`overview.json`, `issues.json`, `issue-detail.json`, `sessions.json`, `session-timeline.json`, `performance.json`).
   Import all six: they link to each other by uid.
3. Open a dashboard and pick the PostgreSQL data source in the **Data source** dropdown (the JSON uses a
   data source variable, so no uid has to match).

The Grafana Cloud free tier stores dashboards for 3 users and has no data limits that matter here; Supabase
free tier must not be paused (see `docs/runbook.md`).

## Try it with demo data

`seed_demo_data.sql` creates a project **Demo Game (seed)** with 3 devices (Pixel 7, Galaxy A14,
iPhone 12), 2 builds (0.3.0, 0.3.1), 10 sessions (2 abnormal, 1 still running), ~450 perf samples,
4 issues and a bug report. Build 0.3.1 has a deliberate regression on the Galaxy A14: lower FPS, more
memory, thermal throttling and crashes.

Run it in the Supabase SQL editor **of a scratch project, not production**. Timestamps are relative to
now, so run it shortly before opening the dashboards. Re-running replaces the demo project; delete it with
`delete from projects where name = 'Demo Game (seed)';`.

## Notes and limits

- **Time range:** most panels filter on the dashboard time range (default: last 7 days). The timeline's
  list ignores it; set the range to cover the session so the charts show it.
- **Retention:** device/session/24h/7d counts on issues are computed from events still inside the
  retention window (default 30 days). Issue totals are kept forever.
- **"Today"** on the overview means since 00:00 UTC.
- **Free-tier friendliness:** panels refresh every minute and each query is a small aggregate; the
  `dashboard_reader` role has a 15 s statement timeout.

## Changing a dashboard

The JSON is generated: edit `generate-dashboards.mjs`, run `npm run generate`, then `npm test`.
`npm test` loads the migrations and the demo seed into an in-memory Postgres, runs every panel and
variable query **as `dashboard_reader`** with Grafana's variables and macros expanded, and checks that
each query returns rows, that table column overrides refer to real columns, and that drill-down links
point at existing dashboards.

> **Not verified:** the dashboards have not been loaded into a running Grafana (no Docker on the author's
> machine). The SQL is tested; the panel layout and Grafana-specific JSON were written against the
> Grafana 11 schema and may need small tweaks on first load. Please tell me what looks off.
