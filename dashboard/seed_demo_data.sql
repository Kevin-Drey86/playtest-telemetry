-- Demo data for the Grafana dashboards: 1 project, 3 devices, 2 builds, 10 sessions (2 abnormal,
-- 1 still running), ~500 perf samples, a handful of issues, custom events and one bug report.
--
-- Build 0.3.1 has a deliberate regression on the Galaxy A14 (lower FPS, more memory, throttling,
-- crashes) so the Performance dashboard has something to show.
--
-- Data goes through ingest_batch(), so it exercises the same code path as real traffic.
-- Timestamps are relative to NOW(): run it shortly before opening the dashboards.
-- Re-running replaces the previous demo project. NEVER run this against a production project.

do $$
declare
  p          uuid;
  s          record;
  iss        jsonb;
  issues     jsonb := '[
    {"fp":"a1f3c9d2e4b60001","type":"Exception","min_build":"0.3.0","w":{"default":1,"Galaxy A14":3},
     "msg":"NullReferenceException: Object reference not set to an instance of an object",
     "tpl":"NullReferenceException: Object reference not set to an instance of an object",
     "stack":"Enemy.Update\nEnemyManager.Tick\nGameLoop.Update"},
    {"fp":"b7d2e8a1c3f50002","type":"Error","min_build":"0.3.1","w":{"default":1,"Galaxy A14":2},
     "msg":"Failed to load addressable ''Levels/Level_%s''",
     "tpl":"Failed to load addressable <str>",
     "stack":"AssetLoader.LoadAsync\nLevelStreamer.Next\nGameLoop.Update"},
    {"fp":"c4a9f1e7d2b80003","type":"Warning","min_build":"0.3.1","w":{"default":0,"Galaxy A14":6},
     "msg":"Texture memory budget exceeded: %s MB",
     "tpl":"Texture memory budget exceeded: <n> MB",
     "stack":"TextureBudget.Check\nLevelStreamer.Next"},
    {"fp":"d8e3b6c5a1f40004","type":"Error","min_build":"0.3.0","w":{"default":1},
     "msg":"Save failed: disk full (%s KB free)",
     "tpl":"Save failed: disk full (<n> KB free)",
     "stack":"SaveSystem.Write\nSaveSystem.Autosave\nGameLoop.Update"}
  ]';
  ev         jsonb;
  perf       jsonb;
  payload    jsonb;
  t0         timestamptz;
  t_end      timestamptz;
  k          int;
  w          int;
  j          int;
  n_samples  int;
begin
  perform setseed(0.42);
  delete from projects where name = 'Demo Game (seed)';
  insert into projects (name, api_key_hash, retention_days)
  values ('Demo Game (seed)', 'seed-' || gen_random_uuid()::text, 365)
  returning id into p;

  for s in
    select * from (values
      -- n, alias,   model,        platform, os,           gpu,          mem,  screen,      build,   hours_ago, minutes, end_reason, base_fps, base_mem
      (1,  'alice', 'Pixel 7',     'Android', 'Android 14', 'Mali-G710',  8192, '1080x2400', '0.3.0', 70.0, 25, 'clean',    60, 700),
      (2,  'bob',   'Galaxy A14',  'Android', 'Android 13', 'Mali-G52',   4096, '1080x2408', '0.3.0', 66.0, 18, 'clean',    42, 650),
      (3,  'carol', 'iPhone 12',   'iOS',     'iOS 17.2',   'Apple A14',  4096, '1170x2532', '0.3.0', 52.0, 30, 'clean',    58, 900),
      (4,  'alice', 'Pixel 7',     'Android', 'Android 14', 'Mali-G710',  8192, '1080x2400', '0.3.1', 30.0, 22, 'clean',    59, 760),
      (5,  'bob',   'Galaxy A14',  'Android', 'Android 13', 'Mali-G52',   4096, '1080x2408', '0.3.1', 28.0, 15, 'abnormal', 31, 1100),
      (6,  'carol', 'iPhone 12',   'iOS',     'iOS 17.2',   'Apple A14',  4096, '1170x2532', '0.3.1', 26.0, 28, 'clean',    57, 980),
      (7,  'dave',  'Galaxy A14',  'Android', 'Android 13', 'Mali-G52',   4096, '1080x2408', '0.3.1', 12.0, 20, 'abnormal', 28, 1150),
      (8,  'alice', 'Pixel 7',     'Android', 'Android 14', 'Mali-G710',  8192, '1080x2400', '0.3.1',  6.0, 35, 'clean',    58, 790),
      (9,  'carol', 'iPhone 12',   'iOS',     'iOS 17.2',   'Apple A14',  4096, '1170x2532', '0.3.1',  3.0, 12, 'clean',    56, 1000),
      (10, 'bob',   'Galaxy A14',  'Android', 'Android 13', 'Mali-G52',   4096, '1080x2408', '0.3.1',  0.4, 20, null,       30, 1120)
    ) as v(n, alias, model, platform, os, gpu, mem, screen, build, hours_ago, minutes, end_reason, base_fps, base_mem)
  loop
    t0    := now() - make_interval(secs => s.hours_ago * 3600);
    t_end := least(t0 + make_interval(mins => s.minutes), now());
    n_samples := s.minutes * 2;                        -- one sample per 30 s

    -- perf samples: noisy FPS, slowly growing memory, thermal throttling on the regressed device
    select jsonb_agg(jsonb_build_object(
      'client_ts', to_jsonb(t0 + make_interval(secs => i * 30)),
      'scene', case when i < n_samples * 0.1 then 'MainMenu' when i < n_samples * 0.6 then 'Level1' else 'Level2' end,
      'fps_avg', round((s.base_fps + (random() * 8 - 4))::numeric, 1),
      'fps_p1_low', round(((s.base_fps + (random() * 8 - 4)) * (0.45 + random() * 0.2))::numeric, 1),
      'frame_ms_max', round((40 + random() * 60 + case when s.model = 'Galaxy A14' and s.build = '0.3.1' then 80 else 0 end)::numeric, 1),
      'main_thread_ms_avg', round((1000.0 / s.base_fps * 0.7 + random() * 2)::numeric, 2),
      'gc_alloc_kb', round((20 + random() * 60)::numeric, 1),
      'used_mem_mb', (s.base_mem + i * (case when s.build = '0.3.1' then 3 else 1 end) + (random() * 20)::int),
      'battery_level', round(greatest(0.2, 0.95 - i * 0.004 - s.n * 0.01)::numeric, 2),
      'battery_status', 'Discharging',
      'thermal_state', case
         when s.platform = 'iOS' then case when i > n_samples * 0.7 then 'Fair' else 'Nominal' end
         when s.model = 'Galaxy A14' and s.build = '0.3.1' then case when i > n_samples * 0.6 then 'Severe' when i > n_samples * 0.3 then 'Moderate' else 'Light' end
         else case when i > n_samples * 0.8 then 'Light' else 'None' end
       end
    )) into perf
    from generate_series(1, n_samples) as i
    where t0 + make_interval(secs => i * 30) <= now();

    -- issue occurrences (aggregated like the real client: repeat_count per event)
    ev := '[]'::jsonb;
    for iss in select value from jsonb_array_elements(issues)
    loop
      continue when s.build < (iss ->> 'min_build');
      w := coalesce((iss -> 'w' ->> s.model)::int, (iss -> 'w' ->> 'default')::int, 0);
      k := floor(random() * (w + 1))::int;
      for j in 1..k loop
        ev := ev || jsonb_build_array(jsonb_strip_nulls(jsonb_build_object(
          'client_ts', to_jsonb(t0 + make_interval(secs => random() * s.minutes * 60)),
          'log_type', iss ->> 'type',
          'fingerprint', iss ->> 'fp',
          'message', replace(iss ->> 'msg', '%s', (1 + floor(random() * 900))::text),
          'message_template', iss ->> 'tpl',
          'stack_trace', iss ->> 'stack',
          'repeat_count', 1 + floor(random() * 4)::int,
          'scene', case when random() < 0.5 then 'Level1' else 'Level2' end,
          'context', case when iss ->> 'type' in ('Error', 'Exception')
                          then jsonb_build_object('level', (1 + floor(random() * 5))::text, 'difficulty', 'normal') end,
          'breadcrumbs', case when iss ->> 'type' in ('Error', 'Exception')
                          then jsonb_build_array(
                                 jsonb_build_object('ts', 0, 'category', 'scene', 'message', 'loading Level1'),
                                 jsonb_build_object('ts', 0, 'category', 'ui', 'message', 'tapped play'),
                                 jsonb_build_object('ts', 0, 'category', 'Info', 'message', 'wave 3 started')) end
        )));
      end loop;
    end loop;

    -- a few custom game events per session
    ev := ev || coalesce((
      select jsonb_agg(jsonb_build_object(
        'client_ts', to_jsonb(t0 + make_interval(secs => lvl * 240)),
        'event_name', case when lvl % 2 = 0 then 'level_complete' else 'level_start' end,
        'props', jsonb_build_object('level', lvl, 'difficulty', 'normal'),
        'scene', 'Level1'))
      from generate_series(1, least(6, s.minutes / 4)) as lvl
      where t0 + make_interval(secs => lvl * 240) <= now()
    ), '[]'::jsonb);

    payload := jsonb_build_object(
      'session', jsonb_strip_nulls(jsonb_build_object(
        'id', gen_random_uuid(),
        'device_id', encode(sha256(convert_to('demo-' || s.alias || s.model, 'UTF8')), 'hex'),
        'tester_alias', s.alias,
        'build_version', s.build,
        'platform', s.platform,
        'device_model', s.model,
        'os', s.os,
        'gpu', s.gpu,
        'system_memory_mb', s.mem,
        'screen', s.screen,
        'started_at', to_jsonb(t0),
        'last_seen_at', to_jsonb(t_end),
        'ended_at', case when s.end_reason is not null then to_jsonb(t_end) end,
        'end_reason', s.end_reason)),
      'events', ev,
      'perf_samples', coalesce(perf, '[]'::jsonb),
      'bug_reports', case when s.n = 5 then jsonb_build_array(jsonb_build_object(
          'client_ts', to_jsonb(t0 + interval '12 minutes'),
          'tester_note', 'Game froze on the loading screen, then the app closed.',
          'breadcrumbs', jsonb_build_array(jsonb_build_object('ts', 0, 'category', 'scene', 'message', 'loading Level2')),
          'context', jsonb_build_object('level', '4')))
        else '[]'::jsonb end
    );

    perform ingest_batch(p, payload);
  end loop;

  raise notice 'Demo data created for project %', p;
end $$;

-- Sanity summary
select (select count(*) from v_sessions where project_name = 'Demo Game (seed)')          as sessions,
       (select count(*) from v_sessions where project_name = 'Demo Game (seed)' and end_reason = 'abnormal') as abnormal,
       (select count(*) from v_issues   where project_name = 'Demo Game (seed)')          as issues,
       (select count(*) from v_perf_samples where project_name = 'Demo Game (seed)')      as perf_samples;
