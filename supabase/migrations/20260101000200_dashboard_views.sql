-- Dashboard views. They run with the owner's privileges (default), so they bypass RLS on the
-- base tables; dashboard_reader gets SELECT on these views only. None of them expose
-- api_key_hash or discord_webhook_url.

create view public.v_issues as
select
  i.id                as issue_id,
  i.project_id,
  p.name              as project_name,
  i.fingerprint,
  i.log_type,
  i.message_template,
  i.stack_trace,
  i.first_seen_at,
  i.last_seen_at,
  i.first_seen_build,
  i.last_seen_build,
  i.total_count,
  coalesce(a.device_count, 0)   as device_count,
  coalesce(a.session_count, 0)  as session_count,
  coalesce(a.count_24h, 0)      as count_24h,
  coalesce(a.count_7d, 0)       as count_7d
from public.issues i
join public.projects p on p.id = i.project_id
left join lateral (
  -- device/session/window counts only cover events still inside the retention window
  select
    count(distinct s.device_id)  as device_count,
    count(distinct e.session_id) as session_count,
    sum(e.repeat_count) filter (where e.received_at > now() - interval '24 hours') as count_24h,
    sum(e.repeat_count) filter (where e.received_at > now() - interval '7 days')   as count_7d
  from public.events e
  join public.sessions s on s.id = e.session_id
  where e.project_id = i.project_id
    and e.fingerprint = i.fingerprint
) a on true;

create view public.v_sessions as
select
  s.id                as session_id,
  s.project_id,
  p.name              as project_name,
  s.tester_alias,
  s.device_id,
  s.device_model,
  s.os,
  s.platform,
  s.build_version,
  s.started_at,
  s.last_seen_at,
  s.ended_at,
  s.end_reason,
  extract(epoch from (coalesce(s.ended_at, s.last_seen_at) - s.started_at))::int as duration_s,
  coalesce(ev.error_count, 0)  as error_count,
  pf.worst_fps_p1_low,
  pf.peak_memory_mb
from public.sessions s
join public.projects p on p.id = s.project_id
left join lateral (
  select sum(e.repeat_count) as error_count
  from public.events e
  where e.session_id = s.id and e.log_type in ('Error', 'Exception')
) ev on true
left join lateral (
  select min(f.fps_p1_low) as worst_fps_p1_low, max(f.used_mem_mb) as peak_memory_mb
  from public.perf_samples f
  where f.session_id = s.id
) pf on true;

-- Sample-level perf data with session context; used for charts and thermal distribution.
create view public.v_perf_samples as
select
  f.id                as sample_id,
  f.project_id,
  p.name              as project_name,
  f.session_id,
  s.build_version,
  s.device_model,
  s.tester_alias,
  f.client_ts,
  f.scene,
  f.fps_avg,
  f.fps_p1_low,
  f.frame_ms_max,
  f.main_thread_ms_avg,
  f.gc_alloc_kb,
  f.used_mem_mb,
  f.battery_level,
  f.battery_status,
  f.thermal_state
from public.perf_samples f
join public.sessions s on s.id = f.session_id
join public.projects p on p.id = f.project_id;

create view public.v_perf_by_device as
select
  f.project_id,
  p.name              as project_name,
  s.build_version,
  s.device_model,
  count(*)                                    as sample_count,
  count(distinct f.session_id)                as session_count,
  percentile_cont(0.5)  within group (order by f.fps_avg)      as fps_avg_p50,
  percentile_cont(0.5)  within group (order by f.fps_p1_low)   as fps_p1_low_p50,
  percentile_cont(0.05) within group (order by f.fps_p1_low)   as fps_p1_low_p05,
  percentile_cont(0.5)  within group (order by f.used_mem_mb)  as mem_mb_p50,
  percentile_cont(0.95) within group (order by f.used_mem_mb)  as mem_mb_p95,
  max(f.used_mem_mb)                          as mem_mb_max
from public.perf_samples f
join public.sessions s on s.id = f.session_id
join public.projects p on p.id = f.project_id
group by f.project_id, p.name, s.build_version, s.device_model;

-- Events of one issue or one session, with session context (drill-down from v_issues).
create view public.v_issue_events as
select
  e.id                as event_id,
  e.project_id,
  e.fingerprint,
  e.session_id,
  e.client_ts,
  e.received_at,
  e.log_type,
  e.message,
  e.repeat_count,
  e.scene,
  e.context,
  e.breadcrumbs,
  s.build_version,
  s.device_model,
  s.tester_alias
from public.events e
join public.sessions s on s.id = e.session_id
where e.fingerprint is not null;

-- Everything that happened in a session, one time-ordered list.
create view public.v_session_timeline as
select
  e.project_id, e.session_id, e.client_ts as ts,
  case when e.event_name is not null then 'event' else 'log' end as kind,
  e.log_type,
  coalesce(e.event_name, e.log_type) as title,
  e.message                          as detail,
  e.repeat_count,
  e.scene,
  null::real as fps_avg, null::real as fps_p1_low, null::int as used_mem_mb,
  coalesce(e.props, e.context)       as payload
from public.events e
union all
select
  f.project_id, f.session_id, f.client_ts,
  'perf', null,
  'perf',
  format('fps %s (p1 low %s), mem %s MB%s',
         round(f.fps_avg::numeric, 1), round(f.fps_p1_low::numeric, 1), f.used_mem_mb,
         case when f.thermal_state is null then '' else ', thermal ' || f.thermal_state end),
  null, f.scene,
  f.fps_avg, f.fps_p1_low, f.used_mem_mb,
  null
from public.perf_samples f
union all
select
  b.project_id, b.session_id, b.client_ts,
  'bug_report', null,
  'bug report',
  b.tester_note,
  null, null,
  null, null, null,
  b.context
from public.bug_reports b;

-- Read-only role for Grafana. Set its password out of band (see docs/backend-setup.md):
--   alter role dashboard_reader password '<strong password>';
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'dashboard_reader') then
    create role dashboard_reader login;
  end if;
end $$;

alter role dashboard_reader set default_transaction_read_only = on;
alter role dashboard_reader set statement_timeout = '15s';

revoke all on all tables in schema public from dashboard_reader;
grant usage on schema public to dashboard_reader;
grant select on
  public.v_issues, public.v_sessions, public.v_perf_samples, public.v_perf_by_device,
  public.v_issue_events, public.v_session_timeline
to dashboard_reader;
