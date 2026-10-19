using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Public entry point. Void methods are marked [Conditional("PLAYTEST_TELEMETRY")]: when the
    /// PLAYTEST_TELEMETRY scripting define is not set, calls to them are removed by the compiler and cost
    /// nothing in production. Nothing here ever throws into game code or blocks on I/O.
    /// </summary>
    public static class PlaytestTelemetry
    {
        const string Define = "PLAYTEST_TELEMETRY";

        static TelemetryClient client;
        static bool warnedMissingSettings;
        static readonly List<ITelemetryModule> pendingModules = new List<ITelemetryModule>();

        /// <summary>The current session's ID (a GUID), or an empty string when telemetry is not running.</summary>
        public static string SessionId => client != null ? client.Session.Id : "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Supports "Enter Play Mode" without domain reload.
            client = null;
            warnedMissingSettings = false;
            pendingModules.Clear();
            TelemetryRunner.Instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoInitialize()
        {
            try
            {
                TelemetrySettings settings = TelemetrySettings.Load();
                if (settings != null && settings.autoInitialize) StartClient(settings, null, null, false);
            }
            catch (Exception e)
            {
                PtLog.Warn("auto-initialize failed: " + e.Message);
            }
        }

        /// <summary>
        /// Starts telemetry using the settings asset in Resources. Only needed when
        /// "Auto Initialize" is off (for example to start after the player gave consent). Safe to call twice.
        /// </summary>
        [Conditional(Define)]
        public static void Initialize()
        {
            try
            {
                TelemetrySettings settings = TelemetrySettings.Load();
                if (settings == null)
                {
                    if (!warnedMissingSettings)
                    {
                        warnedMissingSettings = true;
                        PtLog.Warn("no settings asset found. Create one in Project Settings > Playtest Telemetry.");
                    }
                    return;
                }
                StartClient(settings, null, null, false);
            }
            catch (Exception e)
            {
                PtLog.Warn("initialize failed: " + e.Message);
            }
        }

        /// <summary>Sets a human-readable tester name that is attached to the session (for example "alice").</summary>
        [Conditional(Define)]
        public static void SetTesterAlias(string alias)
        {
            try { if (client != null) client.SetTesterAlias(alias); } catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>
        /// Sets a context value (for example "level" = "3") that is attached to every Error/Exception event
        /// and bug report from now on. At most 32 keys; values are truncated to 256 characters.
        /// </summary>
        [Conditional(Define)]
        public static void SetContext(string key, string value)
        {
            try { if (client != null) client.SetContext(key, value); } catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>Removes a context value set with <see cref="SetContext"/>.</summary>
        [Conditional(Define)]
        public static void RemoveContext(string key)
        {
            try { if (client != null) client.RemoveContext(key); } catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>
        /// Adds a breadcrumb to the on-device ring buffer. Breadcrumbs are never sent on their own: they are
        /// attached to the next Error/Exception event and to bug reports.
        /// </summary>
        [Conditional(Define)]
        public static void AddBreadcrumb(string message, string category = null)
        {
            try { if (client != null) client.Breadcrumbs.Add(category, message); } catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>
        /// Sends a custom game event. Props must be flat: string, number, bool or null values only
        /// (anything else is ignored by the server). Custom events are the lowest priority when queues overflow.
        /// </summary>
        [Conditional(Define)]
        public static void TrackEvent(string name, IDictionary<string, object> props = null)
        {
            try { if (client != null) client.TrackEvent(name, props); } catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>
        /// Files a bug report with the tester's note, the breadcrumbs and the context. With
        /// <paramref name="includeScreenshot"/> a screenshot is captured at the end of the frame,
        /// downscaled to 720 px and uploaded as JPEG.
        /// </summary>
        [Conditional(Define)]
        public static void ReportBug(string note, bool includeScreenshot = true)
        {
            try
            {
                if (client != null && client.BugReports != null) client.BugReports.Report(note, includeScreenshot);
            }
            catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>Sends everything that is queued as soon as possible (on the next frame).</summary>
        [Conditional(Define)]
        public static void Flush()
        {
            try { if (client != null) client.Flush(); } catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>
        /// Records the tester's consent. Only matters when "Require Consent" is on: nothing is collected or
        /// sent until consent is true, and withdrawing it drops the queued data. Works before <see cref="Initialize"/>.
        /// </summary>
        [Conditional(Define)]
        public static void SetConsent(bool consent)
        {
            try
            {
                if (client != null) client.SetConsent(consent);
                else
                {
                    PlayerPrefs.SetInt("PlaytestTelemetry.Consent", consent ? 1 : 0);
                    PlayerPrefs.Save();
                }
            }
            catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>
        /// Registers a custom module. It is initialized right away when telemetry is running, otherwise as
        /// soon as it starts.
        /// </summary>
        [Conditional(Define)]
        public static void RegisterModule(ITelemetryModule module)
        {
            if (module == null) return;
            try
            {
                if (client != null) client.AddModule(module);
                else pendingModules.Add(module);
            }
            catch (Exception e) { PtLog.Warn(e.Message); }
        }

        /// <summary>
        /// Starts timing an async scene load so a duration can be reported. Unity gives no callback when a
        /// load starts, so loads that are not passed here are reported without a duration.
        /// </summary>
        [Conditional(Define)]
        public static void TrackSceneLoad(AsyncOperation operation, string sceneName)
        {
            try { if (client != null && client.Scenes != null) client.Scenes.Track(operation, sceneName); } catch (Exception e) { PtLog.Warn(e.Message); }
        }

        // ------------------------------------------------------------------ internals

        internal static List<ITelemetryModule> TakePendingModules()
        {
            var copy = new List<ITelemetryModule>(pendingModules);
            pendingModules.Clear();
            return copy;
        }

        /// <summary>Creates the client and its hidden runner. <paramref name="force"/> skips activation/config checks (tests).</summary>
        internal static TelemetryClient StartClient(TelemetrySettings settings, ITransport transport, string directory, bool force)
        {
            if (client != null) return client;
            PtLog.Enabled = settings.internalLogging;
            if (!force)
            {
                if (!settings.IsActive()) return null;
                if (!settings.IsConfigured)
                {
                    PtLog.Warn("endpoint URL or API key is not set; telemetry is off. See Project Settings > Playtest Telemetry.");
                    return null;
                }
            }

            var created = new TelemetryClient(settings, transport ?? new HttpTransport(), directory);
            created.Start();

            TelemetryRunner runner = TelemetryRunner.Instance; // reuse a live runner (singleton)
            if (runner == null)
            {
                var go = new GameObject("[PlaytestTelemetry]") { hideFlags = HideFlags.HideInHierarchy };
                runner = go.AddComponent<TelemetryRunner>();
            }
            if (runner != null)
            {
                runner.Client = created;
                created.Runner = runner;
            }
            client = created;
            return created;
        }

        /// <summary>Tears everything down without persisting (tests).</summary>
        internal static void ShutdownForTests()
        {
            if (client != null) client.Stop();
            TelemetryRunner runner = TelemetryRunner.Instance;
            TelemetryRunner.Instance = null; // Destroy is deferred: make sure the next start does not pick this one up
            if (runner != null) Object.Destroy(runner.gameObject);
            client = null;
            pendingModules.Clear();
        }

        internal static TelemetryClient Client => client;
    }
}
