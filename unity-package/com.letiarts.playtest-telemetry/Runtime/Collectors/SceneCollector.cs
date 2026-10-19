using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Keeps TelemetryContext.CurrentScene up to date and records scene loads as events.
    /// Unity does not tell us when a scene load STARTS, so:
    ///  - loads passed to <see cref="PlaytestTelemetry.TrackSceneLoad"/> get start, finish and duration;
    ///  - any other load still produces a "scene_loaded" event (without duration).
    /// </summary>
    internal sealed class SceneCollector : ITelemetryModule
    {
        sealed class TrackedLoad
        {
            public AsyncOperation Operation;
            public string Scene;
            public float StartedAt;
        }

        readonly List<TrackedLoad> tracked = new List<TrackedLoad>();
        TelemetryContext context;

        public void Initialize(TelemetryContext ctx)
        {
            context = ctx;
            context.CurrentScene = SceneManager.GetActiveScene().name ?? "";
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        public void Shutdown()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        }

        void OnActiveSceneChanged(Scene previous, Scene next)
        {
            context.CurrentScene = next.name ?? "";
            context.AddBreadcrumb("active scene: " + context.CurrentScene, "scene");
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!context.CanCollect) return;
            for (int i = 0; i < tracked.Count; i++)
            {
                if (tracked[i].Scene == scene.name) return; // Tick reports tracked loads with their duration
            }
            Emit("scene_loaded", scene.name, null);
        }

        /// <summary>Starts timing an async scene load.</summary>
        public void Track(AsyncOperation operation, string sceneName)
        {
            if (operation == null || !context.CanCollect) return;
            tracked.Add(new TrackedLoad { Operation = operation, Scene = sceneName ?? "", StartedAt = Time.realtimeSinceStartup });
            Emit("scene_load_start", sceneName, null);
            context.AddBreadcrumb("loading scene: " + sceneName, "scene");
        }

        public void Tick(float unscaledDt)
        {
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                TrackedLoad load = tracked[i];
                if (!load.Operation.isDone) continue;
                tracked.RemoveAt(i);
                Emit("scene_load_finish", load.Scene, (Time.realtimeSinceStartup - load.StartedAt) * 1000f);
            }
        }

        void Emit(string name, string scene, float? durationMs)
        {
            var props = new Dictionary<string, object> { { "scene", scene ?? "" } };
            if (durationMs.HasValue) props["duration_ms"] = (double)durationMs.Value;
            context.Client.TrackEvent(name, props);
        }
    }
}
