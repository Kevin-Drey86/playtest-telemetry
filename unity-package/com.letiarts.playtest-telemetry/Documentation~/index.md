# Playtest Telemetry

- **Integration guide:** `docs/integration-guide.md` in the repository
- **Wire protocol:** `docs/ingest-protocol.md`
- **Backend setup / runbook / privacy:** `docs/backend-setup.md`, `docs/runbook.md`, `docs/privacy.md`

## Public API (`LetiArts.PlaytestTelemetry.PlaytestTelemetry`)

| Call | Purpose |
|---|---|
| `Initialize()` | Start manually (when Auto Initialize is off) |
| `SetTesterAlias(alias)` | Name shown on the session |
| `SetContext(key, value)` / `RemoveContext(key)` | Key/values attached to errors and bug reports |
| `AddBreadcrumb(message, category)` | On-device ring buffer, attached to errors and bug reports |
| `TrackEvent(name, props)` | Custom event with flat props |
| `ReportBug(note, includeScreenshot)` | Bug report with breadcrumbs, context, optional screenshot |
| `Flush()` | Send queued data now |
| `SetConsent(bool)` | Consent gate (when Require Consent is on) |
| `RegisterModule(module)` | Add your own `ITelemetryModule` |
| `TrackSceneLoad(op, name)` | Time an async scene load |
| `SessionId` | Current session GUID |
