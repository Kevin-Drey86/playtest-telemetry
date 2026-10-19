using UnityEngine;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// The single hidden DontDestroyOnLoad object that drives the client. Created by
    /// <see cref="PlaytestTelemetry"/>; there is a singleton guard so it never exists twice.
    /// </summary>
    internal sealed class TelemetryRunner : MonoBehaviour
    {
        internal static TelemetryRunner Instance;
        internal TelemetryClient Client;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Update()
        {
            if (Client == null) return;
            try
            {
                Client.Tick(Time.unscaledDeltaTime);
            }
            catch (System.Exception e)
            {
                PtLog.Warn("tick failed: " + e.Message);
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (Client == null) return;
            try { Client.OnPause(paused); } catch (System.Exception e) { PtLog.Warn("pause handling failed: " + e.Message); }
        }

        void OnApplicationQuit()
        {
            if (Client == null) return;
            try { Client.OnQuit(); } catch (System.Exception e) { PtLog.Warn("quit handling failed: " + e.Message); }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
