# Android device test checklist (Phase 3 sign-off)

Everything below was verified in the Editor and in batch-mode tests on Unity 6; an actual device build
has **not** been run by the author. Run this once on a real phone before the first playtest.

## Prerequisites
- Backend deployed and a project key created (see `docs/backend-setup.md`).
- Unity 2021.3 LTS with the Android module, a test project that has the package installed.

## Steps
1. **Install:** Package Manager > Add package from git URL
   `https://github.com/<org>/playtest-telemetry.git?path=/unity-package/com.letiarts.playtest-telemetry`
   (or "Add package from disk" on `unity-package/com.letiarts.playtest-telemetry/package.json`).
   Expect: no console errors; Samples appear in the Package Manager.
2. **Settings:** Project Settings > Playtest Telemetry > *Create settings*. Paste the Worker URL and API key.
3. **Send test event:** click it. Expect `OK: {"success":true,"accepted":1,"dropped":0}`. In Supabase:
   `select * from v_sessions order by started_at desc limit 1;` shows an `editor-test` session.
4. **Validate setup:** click it. Resolve every warning (iOS plugin message only matters for iOS).
5. **Define:** Tools > Playtest Telemetry > *Add PLAYTEST_TELEMETRY define* (Android is the active target).
6. **Sample:** Package Manager > Playtest Telemetry > Samples > import *Basic Setup*. Put `BasicSetupDemo`
   on a GameObject in a scene that is in the build.
7. **Build:** File > Build Settings > Android, **Development Build** on, Scripting Backend **IL2CPP**,
   ARM64. Build and run on the device (USB debugging).
   Expect: no build warnings from `[PlaytestTelemetry]` except the intentional ones.
8. **On the device**, with mobile data or Wi-Fi on:
   - tap *Log error* three times, *Log warning*, *Throw exception*, *Track custom event*, *Report bug*, *Flush now*.
   - Expected in Supabase within ~30 s:
     - `select * from v_issues;` -> one `Demo error: failed to load slot <n>` issue (count 3), one warning issue, one exception issue.
     - `select * from v_sessions;` -> tester `demo-tester`, your device model, a real GPU string, `platform = Android`.
     - `select * from perf_samples order by id desc limit 3;` -> fps / memory filled, `thermal_state` is e.g. `None` (Android 10+), battery set.
     - `select * from bug_reports;` -> note + breadcrumbs, `screenshot_path` set; the file exists in Storage > bug-screenshots.
   - With Discord configured: one message per *new* issue.
9. **Offline / pause:** enable airplane mode, tap *Log error*, press Home. Disable airplane mode, reopen the app.
   Expect the error to arrive (uploaded from disk or the memory queue).
10. **Abnormal end:** swipe the app away from the recents list while it is running. Reopen it.
    Expect the previous session to show `end_reason = 'abnormal'` in `v_sessions`.
11. **Clean end:** use a quit button / `Application.Quit()` (or back out of the app on Android). Expect `end_reason = 'clean'`.
12. **Release build:** remove the define, uncheck Development Build, build. Expect a `[PlaytestTelemetry]` warning only if the
    define was left on, and no telemetry traffic from the app.

## If something is missing
`adb logcat -s Unity` and filter on `[PlaytestTelemetry]`: send failures, 401 (wrong key) and "disabled" modules are all logged there.
