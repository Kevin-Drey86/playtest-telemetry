#if PT_HAS_CRASHLYTICS
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry.Integrations
{
    /// <summary>
    /// Optional bridge to Firebase Crashlytics. It only sets custom keys (pt_session_id, pt_tester and
    /// pt_ctx_* mirrors of the context) so a Crashlytics crash can be matched to our session timeline.
    /// It never forwards our logs to Crashlytics.
    /// </summary>
    public sealed class CrashlyticsBridge : ITelemetryModule
    {
        const string ContextPrefix = "pt_ctx_";

        TelemetryContext context;
        bool active;

        public void Initialize(TelemetryContext ctx)
        {
            context = ctx;
            if (!ctx.Settings.enableCrashlyticsBridge) return;
            active = true;

            SetKey("pt_session_id", ctx.SessionId);
            SetKey("pt_tester", ctx.TesterAlias ?? "");
            foreach (KeyValuePair<string, string> kv in ctx.SnapshotContext()) SetKey(ContextPrefix + kv.Key, kv.Value);

            ctx.AliasChanged += OnAliasChanged;
            ctx.ContextChanged += OnContextChanged;
        }

        void OnAliasChanged(string alias)
        {
            SetKey("pt_tester", alias ?? "");
        }

        void OnContextChanged(string key, string value)
        {
            SetKey(ContextPrefix + key, value ?? ""); // Crashlytics cannot remove a key: blank it
        }

        static void SetKey(string key, string value)
        {
            try
            {
                Firebase.Crashlytics.Crashlytics.SetCustomKey(key, value);
            }
            catch (Exception)
            {
                // Firebase not initialized yet or unavailable: never hurt the game.
            }
        }

        public void Tick(float unscaledDt) { }

        public void Shutdown()
        {
            if (!active || context == null) return;
            context.AliasChanged -= OnAliasChanged;
            context.ContextChanged -= OnContextChanged;
            active = false;
        }
    }

    internal static class CrashlyticsBridgeRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            PlaytestTelemetry.RegisterModule(new CrashlyticsBridge());
        }
    }
}
#endif
