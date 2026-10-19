# Changelog

## [0.1.0] - 2026-10-19
### Added
- Log, exception and breadcrumb capture with stable fingerprinting and per-flush aggregation.
- Performance sampling (FPS avg / 1% low, frame time, memory, battery, thermal state).
- Session lifecycle with abnormal-end detection, scene events, bug reports with screenshots.
- Gzip HTTP transport with exponential backoff, priority queues and a size-capped disk queue.
- Optional Firebase Crashlytics bridge (custom keys only).
- Project Settings page, build-time checks, `PLAYTEST_TELEMETRY` define menu items.
