-- ingest_batch: one RPC = one transaction = one Supabase subrequest per Worker invocation.
--
-- Payload shape (the Worker rebuilds it from whitelisted fields, so types are already clean):
-- {
--   "session":      { id, device_id, tester_alias, build_version, platform, device_model, os, gpu,
--                     system_memory_mb, screen, started_at, last_seen_at, ended_at, end_reason },
--   "events":       [ { client_ts, log_type, fingerprint, message, repeat_count, scene, context,
--                       breadcrumbs, event_name, props,
--                       message_template, stack_trace   -- issue data: used for issues, NOT stored on events
--                     } ],
--   "perf_samples": [ { client_ts, scene, fps_avg, fps_p1_low, frame_ms_max, main_thread_ms_avg,
--                       gc_alloc_kb, used_mem_mb, battery_level, battery_status, thermal_state } ],
--   "bug_reports":  [ { client_ts, tester_note, screenshot_path, breadcrumbs, context } ]
-- }
create or replace function public.ingest_batch(p_project_id uuid, p_payload jsonb)
returns jsonb
language plpgsql
set search_path = public, pg_temp
as $$
declare
  v_session     jsonb := p_payload -> 'session';
  v_session_id  uuid;
  v_owner       uuid;
  v_build       text;
  v_events      int := 0;
  v_perf        int := 0;
  v_bugs        int := 0;
  v_new_issues  jsonb;
begin
  if v_session is null or jsonb_typeof(v_session) <> 'object' then
    raise exception 'payload.session is required' using errcode = '22023';
  end if;

  v_session_id := (v_session ->> 'id')::uuid;
  v_build      := coalesce(v_session ->> 'build_version', '');

  -- 1. Session upsert (idempotent: the client sends the session block on every request).
  insert into sessions as s (
    id, project_id, device_id, tester_alias, build_version, platform, device_model, os, gpu,
    system_memory_mb, screen, started_at, last_seen_at, ended_at, end_reason
  )
  values (
    v_session_id,
    p_project_id,
    coalesce(v_session ->> 'device_id', ''),
    nullif(v_session ->> 'tester_alias', ''),
    v_build,
    coalesce(v_session ->> 'platform', ''),
    coalesce(v_session ->> 'device_model', ''),
    coalesce(v_session ->> 'os', ''),
    coalesce(v_session ->> 'gpu', ''),
    (v_session ->> 'system_memory_mb')::int,
    coalesce(v_session ->> 'screen', ''),
    coalesce((v_session ->> 'started_at')::timestamptz, now()),
    least(coalesce((v_session ->> 'last_seen_at')::timestamptz, now()), now()),
    (v_session ->> 'ended_at')::timestamptz,
    nullif(v_session ->> 'end_reason', '')
  )
  on conflict (id) do update set
    last_seen_at = greatest(s.last_seen_at, excluded.last_seen_at),
    tester_alias = coalesce(excluded.tester_alias, s.tester_alias),
    ended_at     = coalesce(excluded.ended_at, s.ended_at),
    end_reason   = coalesce(excluded.end_reason, s.end_reason);

  -- A session id may only ever belong to one project.
  select project_id into v_owner from sessions where id = v_session_id;
  if v_owner is distinct from p_project_id then
    raise exception 'session % belongs to another project', v_session_id using errcode = '42501';
  end if;

  -- 2. Issues: one upsert per fingerprint in this batch (ON CONFLICT cannot touch a row twice,
  --    so aggregate first). (xmax = 0) is true only for rows inserted by this statement.
  with ev as (
    select *
    from jsonb_to_recordset(coalesce(p_payload -> 'events', '[]'::jsonb)) as x(
      client_ts timestamptz, log_type text, fingerprint text, message text, repeat_count int,
      message_template text, stack_trace text
    )
    where coalesce(fingerprint, '') <> ''
  ),
  agg as (
    select
      fingerprint,
      max(log_type)                                          as log_type,
      coalesce(max(nullif(message_template, '')), left(max(message), 512), '') as message_template,
      coalesce(max(nullif(stack_trace, '')), '')             as stack_trace,
      least(min(coalesce(client_ts, now())), now())          as first_ts,
      least(max(coalesce(client_ts, now())), now())          as last_ts,
      sum(greatest(coalesce(repeat_count, 1), 1))::bigint    as cnt
    from ev
    group by fingerprint
  ),
  up as (
    insert into issues as i (
      project_id, fingerprint, log_type, message_template, stack_trace,
      first_seen_at, last_seen_at, first_seen_build, last_seen_build, total_count
    )
    select p_project_id, fingerprint, coalesce(log_type, 'Error'), message_template, stack_trace,
           first_ts, last_ts, v_build, v_build, cnt
    from agg
    on conflict (project_id, fingerprint) do update set
      total_count      = i.total_count + excluded.total_count,
      first_seen_build = case when excluded.first_seen_at < i.first_seen_at
                              then excluded.first_seen_build else i.first_seen_build end,
      last_seen_build  = case when excluded.last_seen_at >= i.last_seen_at
                              then excluded.last_seen_build else i.last_seen_build end,
      first_seen_at    = least(i.first_seen_at, excluded.first_seen_at),
      last_seen_at     = greatest(i.last_seen_at, excluded.last_seen_at),
      stack_trace      = case when i.stack_trace = '' then excluded.stack_trace else i.stack_trace end
    returning i.fingerprint, i.log_type, i.message_template, (i.xmax = 0) as is_new
  )
  select coalesce(
           jsonb_agg(jsonb_build_object(
             'fingerprint', fingerprint,
             'log_type', log_type,
             'message_template', message_template,
             'build', v_build
           )) filter (where is_new),
           '[]'::jsonb)
  into v_new_issues
  from up;

  -- 3. Events, perf samples, bug reports.
  insert into events (
    project_id, session_id, client_ts, log_type, fingerprint, message, repeat_count, scene,
    context, breadcrumbs, event_name, props
  )
  select
    p_project_id, v_session_id, coalesce(client_ts, now()), coalesce(log_type, 'Info'),
    nullif(fingerprint, ''), left(coalesce(message, ''), 512), greatest(coalesce(repeat_count, 1), 1),
    coalesce(scene, ''), context, breadcrumbs, event_name, props
  from jsonb_to_recordset(coalesce(p_payload -> 'events', '[]'::jsonb)) as x(
    client_ts timestamptz, log_type text, fingerprint text, message text, repeat_count int,
    scene text, context jsonb, breadcrumbs jsonb, event_name text, props jsonb
  );
  get diagnostics v_events = row_count;

  insert into perf_samples (
    project_id, session_id, client_ts, scene, fps_avg, fps_p1_low, frame_ms_max,
    main_thread_ms_avg, gc_alloc_kb, used_mem_mb, battery_level, battery_status, thermal_state
  )
  select
    p_project_id, v_session_id, coalesce(client_ts, now()), coalesce(scene, ''),
    coalesce(fps_avg, 0), coalesce(fps_p1_low, 0), coalesce(frame_ms_max, 0),
    main_thread_ms_avg, gc_alloc_kb, coalesce(used_mem_mb, 0), battery_level, battery_status, thermal_state
  from jsonb_to_recordset(coalesce(p_payload -> 'perf_samples', '[]'::jsonb)) as x(
    client_ts timestamptz, scene text, fps_avg real, fps_p1_low real, frame_ms_max real,
    main_thread_ms_avg real, gc_alloc_kb real, used_mem_mb int, battery_level real,
    battery_status text, thermal_state text
  );
  get diagnostics v_perf = row_count;

  insert into bug_reports (
    project_id, session_id, client_ts, tester_note, screenshot_path, breadcrumbs, context
  )
  select
    p_project_id, v_session_id, coalesce(client_ts, now()), coalesce(tester_note, ''),
    screenshot_path, coalesce(breadcrumbs, '[]'::jsonb), coalesce(context, '{}'::jsonb)
  from jsonb_to_recordset(coalesce(p_payload -> 'bug_reports', '[]'::jsonb)) as x(
    client_ts timestamptz, tester_note text, screenshot_path text, breadcrumbs jsonb, context jsonb
  );
  get diagnostics v_bugs = row_count;

  return jsonb_build_object(
    'inserted', v_events + v_perf + v_bugs,
    'new_issues', v_new_issues
  );
end;
$$;

-- Retention: events and perf samples only. Issues, sessions and bug reports are kept.
create or replace function public.purge_old_data()
returns jsonb
language plpgsql
set search_path = public, pg_temp
as $$
declare
  v_events int;
  v_perf   int;
begin
  delete from events e
  using projects p
  where p.id = e.project_id
    and e.received_at < now() - make_interval(days => p.retention_days);
  get diagnostics v_events = row_count;

  delete from perf_samples s
  using projects p
  where p.id = s.project_id
    and s.received_at < now() - make_interval(days => p.retention_days);
  get diagnostics v_perf = row_count;

  return jsonb_build_object('events_deleted', v_events, 'perf_samples_deleted', v_perf);
end;
$$;

-- Only the service role may call these (Supabase grants EXECUTE to anon/authenticated by default).
revoke all on function public.ingest_batch(uuid, jsonb) from public;
revoke all on function public.purge_old_data() from public;
do $$
declare
  r text;
begin
  foreach r in array array['anon', 'authenticated'] loop
    if exists (select 1 from pg_roles where rolname = r) then
      execute format('revoke all on function public.ingest_batch(uuid, jsonb) from %I', r);
      execute format('revoke all on function public.purge_old_data() from %I', r);
    end if;
  end loop;
end $$;
