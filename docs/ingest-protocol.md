# Ingest protocol (`POST /v1/ingest`)

The contract between the Unity client and the Worker. The Worker rebuilds every row from the
fields listed here; anything else is ignored.

**Headers:** `X-Telemetry-Key: <project api key>`, `Content-Type: application/json`,
optional `Content-Encoding: gzip`.

**Limits:** 512 KB on the wire, 2 MB after gzip decoding, max 500 items
(`events + perf_samples + bug_reports`) per request.

```jsonc
{
  "session": {                       // required on EVERY request (server upsert is idempotent)
    "id": "uuid", "device_id": "salted-hash", "tester_alias": "alice",
    "build_version": "0.3.1", "platform": "Android", "device_model": "Pixel 7",
    "os": "Android 14", "gpu": "Mali-G710", "system_memory_mb": 8192, "screen": "1080x2400",
    "started_at": 1760000000000,     // epoch ms or ISO-8601; future values are clamped to "now"
    "last_seen_at": 1760000300000,
    "ended_at": null,                // set on clean quit, or when reporting a previous abnormal session
    "end_reason": null               // "clean" | "abnormal" | "unknown"
  },
  "events": [{
    "client_ts": 1760000100000,
    "log_type": "Error",             // Info | Warning | Error | Exception | Event
    "fingerprint": "a1b2c3d4e5f60718", // hex, Warning/Error/Exception only
    "message": "NullReference at slot 7",   // <= 512 chars, the concrete message
    "message_template": "NullReference at slot <n>", // issue data (first sight only is fine)
    "stack_trace": "at Foo.Bar() ...",       // issue data: send once per fingerprint per session
    "repeat_count": 3,
    "scene": "Main",
    "context": {"level": "3"},       // Error/Exception only, string -> string
    "breadcrumbs": [{"ts": 1760000099000, "category": "ui", "message": "tap play"}], // Error/Exception only
    "event_name": null, "props": null // custom events: event_name set => log_type becomes "Event";
                                      // props are flat scalars (string/number/bool/null), max 20 keys
  }],
  "perf_samples": [{
    "client_ts": 1760000100000, "scene": "Main",
    "fps_avg": 58.2, "fps_p1_low": 31.0, "frame_ms_max": 64.0,   // required
    "used_mem_mb": 812,                                           // required
    "main_thread_ms_avg": 12.1, "gc_alloc_kb": 40.0,              // optional
    "battery_level": 0.82, "battery_status": "Discharging", "thermal_state": "Fair" // optional
  }],
  "bug_reports": [{
    "client_ts": 1760000100000, "tester_note": "stuck on loading",
    "screenshot_b64": "<base64 JPEG, max ~450 KB>",   // optional; stored in the private bug-screenshots bucket
    "breadcrumbs": [], "context": {}
  }]
}
```

## Responses

| Status | Meaning | Client action |
|---|---|---|
| 200 `{success, accepted, dropped}` | Stored. `dropped` = malformed items that were skipped. | Remove from queue |
| 400 | Invalid JSON, bad encoding, or invalid session | Drop the batch (retrying cannot help) |
| 401 | Unknown API key | Stop sending; surface a config error |
| 409 / 422 | Database rejected the batch (session owned by another project / bad data) | Drop the batch |
| 413 | Too large or more than 500 items | Split the batch once; drop if still rejected |
| 429 + `Retry-After` | Per-device rate limit (~30 req/min, per isolate, approximate) | Re-queue; wait at least `Retry-After` seconds |
| 503 / network error | Supabase unavailable | Re-queue with exponential backoff |

## Known limitation: duplicate delivery

If a request is stored but the response is lost (client sees a timeout), the retry inserts the
batch a second time: events are duplicated and issue counts are inflated. The session upsert is
idempotent, event inserts are not. Acceptable for internal playtests; a `batch_id` dedupe column
can be added later if it matters.
