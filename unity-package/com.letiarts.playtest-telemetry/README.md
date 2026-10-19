# Playtest Telemetry (Unity)

Remote logging and performance telemetry for Unity **mobile playtests** (Android + iOS, IL2CPP).
Requires Unity 2021.3 or newer. No third-party dependencies.

## Install

Package Manager > **Add package from git URL**:

```
https://github.com/<org>/playtest-telemetry.git?path=/unity-package/com.letiarts.playtest-telemetry
```

## Quick start

1. **Project Settings > Playtest Telemetry** > *Create settings*, paste the endpoint URL and project API key.
2. **Tools > Playtest Telemetry > Add PLAYTEST_TELEMETRY define**.
3. Click **Send test event** on the settings page.
4. Play. Logs, exceptions and performance samples are captured automatically; no prefab or setup code.

Only the define (or the Editor / Development-build options in the settings) activates telemetry, and
calls to `PlaytestTelemetry.*` compile out when the define is missing, so production builds pay nothing.

See `Documentation~/index.md` and the repository's `docs/` folder for the full integration guide.
