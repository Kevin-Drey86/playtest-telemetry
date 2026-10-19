-- Assertions raise an exception on failure. Runs against a database with all migrations applied.
-- Run via `npm test` in supabase/ (PGlite) or paste into the SQL editor of a scratch database.

do $$
declare
  p1 uuid; p2 uuid;
  s1 uuid := '11111111-1111-1111-1111-111111111111';
  payload jsonb;
  r jsonb;
  n bigint;
  st text;
begin
  insert into projects (name, api_key_hash) values ('Game A', 'hash-a') returning id into p1;
  insert into projects (name, api_key_hash) values ('Game B', 'hash-b') returning id into p2;

  payload := jsonb_build_object(
    'session', jsonb_build_object(
      'id', s1, 'device_id', 'dev-1', 'tester_alias', 'alice', 'build_version', '0.1.0',
      'platform', 'Android', 'device_model', 'Pixel 7', 'os', 'Android 14', 'gpu', 'Mali-G710',
      'system_memory_mb', 8192, 'screen', '1080x2400',
      'started_at', now() - interval '10 minutes', 'last_seen_at', now()),
    'events', jsonb_build_array(
      jsonb_build_object('client_ts', now(), 'log_type', 'Error', 'fingerprint', 'fp1',
        'message', 'NullReference at slot 7', 'message_template', 'NullReference at slot <n>',
        'stack_trace', 'at Foo.Bar()', 'repeat_count', 3, 'scene', 'Main',
        'breadcrumbs', jsonb_build_array(jsonb_build_object('m', 'tap'))),
      jsonb_build_object('client_ts', now(), 'log_type', 'Error', 'fingerprint', 'fp1',
        'message', 'NullReference at slot 9', 'message_template', 'NullReference at slot <n>',
        'repeat_count', 2, 'scene', 'Main'),
      jsonb_build_object('client_ts', now(), 'log_type', 'Warning', 'fingerprint', 'fp2',
        'message', 'slow load', 'message_template', 'slow load', 'repeat_count', 1, 'scene', 'Main'),
      jsonb_build_object('client_ts', now(), 'log_type', 'Info', 'message', repeat('x', 2000), 'scene', 'Main'),
      jsonb_build_object('client_ts', now(), 'log_type', 'Event', 'event_name', 'level_start',
        'props', jsonb_build_object('level', 3), 'scene', 'Main')),
    'perf_samples', jsonb_build_array(
      jsonb_build_object('client_ts', now(), 'scene', 'Main', 'fps_avg', 58.5, 'fps_p1_low', 31,
        'frame_ms_max', 60, 'used_mem_mb', 900, 'thermal_state', 'Fair')),
    'bug_reports', jsonb_build_array(
      jsonb_build_object('client_ts', now(), 'tester_note', 'it broke',
        'breadcrumbs', jsonb_build_array('a'), 'context', jsonb_build_object('k', 'v')))
  );

  -- First call: all rows inserted, both fingerprints are new.
  r := ingest_batch(p1, payload);
  assert (r ->> 'inserted')::int = 7, format('inserted should be 7 (5 events+1 perf+1 bug), got %s', r);
  assert jsonb_array_length(r -> 'new_issues') = 2, format('expected 2 new issues, got %s', r);
  assert (r -> 'new_issues' -> 0 ->> 'build') = '0.1.0', 'new_issues carries the build';

  assert (select count(*) from sessions) = 1, 'one session';
  assert (select count(*) from events where project_id = p1) = 5, 'five events';
  assert (select count(*) from perf_samples) = 1, 'one perf sample';
  assert (select count(*) from bug_reports) = 1, 'one bug report';
  assert (select max(length(message)) from events) = 512, 'message truncated to 512';

  select total_count into n from issues where fingerprint = 'fp1';
  assert n = 5, format('fp1 total_count should be 3+2=5, got %s', n);
  select stack_trace into st from issues where fingerprint = 'fp1';
  assert st = 'at Foo.Bar()', 'stack trace stored on the issue';
  assert (select message_template from issues where fingerprint = 'fp1') = 'NullReference at slot <n>', 'template';
  assert (select count(*) from events where fingerprint = 'fp1') = 2, 'events keep fingerprint';

  -- Repeated call: no new issues, counts accumulate, session stays a single row (idempotent upsert).
  r := ingest_batch(p1, payload);
  assert jsonb_array_length(r -> 'new_issues') = 0, format('repeat must return no new issues, got %s', r);
  select total_count into n from issues where fingerprint = 'fp1';
  assert n = 10, format('fp1 total_count should be 10, got %s', n);
  assert (select count(*) from sessions) = 1, 'still one session';
  assert (select count(*) from issues) = 2, 'still two issues';

  -- Session end is recorded and not overwritten by a later batch without ended_at.
  perform ingest_batch(p1, jsonb_build_object('session', jsonb_build_object(
    'id', s1, 'started_at', now(), 'ended_at', now(), 'end_reason', 'clean')));
  perform ingest_batch(p1, jsonb_build_object('session', jsonb_build_object('id', s1)));
  assert (select end_reason from sessions where id = s1) = 'clean', 'end_reason kept';
  assert (select ended_at from sessions where id = s1) is not null, 'ended_at kept';
  assert (select tester_alias from sessions where id = s1) = 'alice', 'alias kept';

  -- Same fingerprint in another project is a separate issue.
  r := ingest_batch(p2, jsonb_set(payload, '{session,id}', to_jsonb('22222222-2222-2222-2222-222222222222'::text)));
  assert jsonb_array_length(r -> 'new_issues') = 2, 'issues are per project';

  -- A session id cannot be hijacked by another project.
  begin
    perform ingest_batch(p2, payload);
    raise exception 'expected session ownership failure';
  exception when insufficient_privilege then
    null;
  end;

  -- Missing session is rejected.
  begin
    perform ingest_batch(p1, '{}'::jsonb);
    raise exception 'expected failure for missing session';
  exception when invalid_parameter_value then
    null;
  end;
end $$;

-- Views
do $$
declare
  p uuid;
  rec record;
begin
  select id into p from projects where name = 'Game A';

  select * into rec from v_issues where fingerprint = 'fp1' and project_id = p;
  assert rec.device_count = 1 and rec.session_count = 1, 'v_issues device/session counts';
  assert rec.count_24h = 10 and rec.count_7d = 10, format('v_issues windowed counts, got %s', rec.count_24h);
  assert rec.total_count = 10, 'v_issues total';

  select * into rec from v_sessions where project_id = p;
  assert rec.error_count = 10, format('v_sessions error_count, got %s', rec.error_count);
  assert rec.worst_fps_p1_low = 31 and rec.peak_memory_mb = 900, 'v_sessions perf';
  assert rec.end_reason = 'clean', 'v_sessions end_reason';

  select * into rec from v_perf_by_device where project_id = p;
  assert rec.device_model = 'Pixel 7' and rec.sample_count = 2, 'v_perf_by_device';

  assert (select count(distinct kind) from v_session_timeline where project_id = p) = 4, 'timeline has log/event/perf/bug_report';
  assert (select count(*) from v_perf_samples where project_id = p) = 2, 'v_perf_samples';
  assert (select count(*) from v_issue_events where project_id = p and fingerprint = 'fp1') = 4, 'v_issue_events';
end $$;

-- Retention: events/perf older than retention_days are purged; issues, sessions, bug reports stay.
do $$
declare
  p uuid;
  r jsonb;
begin
  select id into p from projects where name = 'Game A';
  update projects set retention_days = 7 where id = p;
  update events set received_at = now() - interval '8 days' where project_id = p and log_type = 'Warning';
  update perf_samples set received_at = now() - interval '8 days' where project_id = p;

  r := purge_old_data();
  assert (r ->> 'events_deleted')::int = 2, format('expected 2 old warning events deleted, got %s', r);
  assert (r ->> 'perf_samples_deleted')::int = 2, format('expected 2 perf deleted, got %s', r);
  assert (select count(*) from issues) = 4, 'issues kept';
  assert (select count(*) from sessions) = 2, 'sessions kept';
  assert (select count(*) from bug_reports where project_id = p) = 2, 'bug reports kept';
end $$;

-- Security: dashboard_reader reads views only.
do $$
begin
  set local role dashboard_reader;
  perform 1 from v_issues limit 1;
  begin
    perform 1 from events limit 1;
    raise exception 'dashboard_reader must not read base tables';
  exception when insufficient_privilege then
    null;
  end;
  begin
    perform 1 from projects limit 1;
    raise exception 'dashboard_reader must not read projects';
  exception when insufficient_privilege then
    null;
  end;
  reset role;
end $$;
