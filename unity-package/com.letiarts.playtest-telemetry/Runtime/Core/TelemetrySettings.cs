using UnityEngine;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>Lowest log level that is sent to the backend.</summary>
    public enum TelemetryLogLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2,
        Exception = 3,
    }

    /// <summary>
    /// Project-wide configuration. Edit it in Project Settings > Playtest Telemetry; the asset lives in
    /// a Resources folder so it can be loaded at runtime.
    /// NOTE: the asset (including the API key) ships inside player builds. The key only allows writing
    /// telemetry for one project; rotate it if a build leaks (see docs/runbook.md).
    /// </summary>
    public sealed class TelemetrySettings : ScriptableObject
    {
        public const string ResourceName = "PlaytestTelemetrySettings";

        [Header("Backend")]
        [Tooltip("Base URL of the Cloudflare Worker, e.g. https://playtest-telemetry.example.workers.dev")]
        public string endpointUrl = "";
        public string apiKey = "";

        [Header("Activation")]
        [Tooltip("Active in the Unity Editor. (The PLAYTEST_TELEMETRY define always activates telemetry.)")]
        public bool enabledInEditor = true;
        [Tooltip("Active in Development builds. (The PLAYTEST_TELEMETRY define always activates telemetry.)")]
        public bool enabledInDevelopmentBuilds = true;
        [Tooltip("Start automatically before the first scene. Disable to call PlaytestTelemetry.Initialize() yourself, e.g. after consent.")]
        public bool autoInitialize = true;
        [Tooltip("Do not collect or send anything until PlaytestTelemetry.SetConsent(true) is called.")]
        public bool requireConsent = false;

        [Header("Logs")]
        public TelemetryLogLevel minLogLevel = TelemetryLogLevel.Warning;
        [Min(0)] public int breadcrumbBufferSize = 50;
        [Min(1)] public float flushIntervalSeconds = 30f;
        [Tooltip("Flush right away (at most every 5 s) when an exception is logged.")]
        public bool flushOnException = true;

        [Header("Performance")]
        public bool enablePerformance = true;
        [Min(1)] public float perfSampleIntervalSeconds = 30f;

        [Header("Queues")]
        [Min(10)] public int maxMemoryQueueItems = 500;
        [Min(10240)] public int maxDiskQueueBytes = 2 * 1024 * 1024;
        public bool gzip = true;

        [Header("Modules")]
        public bool enableLogCollector = true;
        public bool enableBreadcrumbs = true;
        public bool enableSceneCollector = true;
        public bool enableBugReports = true;
        public bool enableCrashlyticsBridge = true;

        [Header("Privacy / misc")]
        [Tooltip("Generated per project. Device IDs are SHA-256(deviceUniqueIdentifier + salt); the raw ID is never sent.")]
        public string deviceIdSalt = "";
        [Tooltip("Write the package's own diagnostics to the Unity console (prefixed [PlaytestTelemetry]).")]
        public bool internalLogging = true;

        public bool IsConfigured => !string.IsNullOrEmpty(endpointUrl) && !string.IsNullOrEmpty(apiKey);

        /// <summary>True when telemetry should run in the current environment.</summary>
        public bool IsActive()
        {
#if PLAYTEST_TELEMETRY
            return true;
#else
            if (Application.isEditor) return enabledInEditor;
            return Debug.isDebugBuild && enabledInDevelopmentBuilds;
#endif
        }

        /// <summary>Full ingest URL (accepts a base URL or one that already ends in /v1/ingest).</summary>
        public string IngestUrl()
        {
            string url = (endpointUrl ?? "").Trim().TrimEnd('/');
            return url.EndsWith("/v1/ingest") ? url : url + "/v1/ingest";
        }

        public static TelemetrySettings Load()
        {
            return Resources.Load<TelemetrySettings>(ResourceName);
        }
    }
}
