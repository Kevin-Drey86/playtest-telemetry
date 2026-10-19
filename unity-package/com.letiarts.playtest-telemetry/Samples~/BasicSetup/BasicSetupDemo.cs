using System;
using System.Collections.Generic;
using LetiArts.PlaytestTelemetry;
using UnityEngine;

/// <summary>
/// Exercises every public API call. Add it to any GameObject in a scene, enter Play mode (or build a
/// Development build) and press the buttons. Needs the PLAYTEST_TELEMETRY define
/// (Tools > Playtest Telemetry > Add define) for the PlaytestTelemetry.* calls to be compiled in.
/// </summary>
public class BasicSetupDemo : MonoBehaviour
{
    int level = 1;
    int errorCounter;

    void Start()
    {
        // With "Auto Initialize" on, telemetry has already started. Calling Initialize() is harmless.
        PlaytestTelemetry.Initialize();
        PlaytestTelemetry.SetTesterAlias("demo-tester");
        PlaytestTelemetry.SetContext("level", level.ToString());
        PlaytestTelemetry.AddBreadcrumb("demo scene started", "demo");
    }

    void OnGUI()
    {
        GUI.matrix = Matrix4x4.Scale(Vector3.one * Mathf.Max(1f, Screen.dpi / 120f));
        GUILayout.BeginArea(new Rect(20, 20, 420, 800));
        GUILayout.Label("Playtest Telemetry demo");
        GUILayout.Label("Session: " + (string.IsNullOrEmpty(PlaytestTelemetry.SessionId) ? "(not running)" : PlaytestTelemetry.SessionId));

        if (GUILayout.Button("Next level (SetContext)"))
        {
            level++;
            PlaytestTelemetry.SetContext("level", level.ToString());
            PlaytestTelemetry.AddBreadcrumb("reached level " + level, "progress");
        }
        if (GUILayout.Button("Track custom event"))
        {
            PlaytestTelemetry.TrackEvent("demo_button", new Dictionary<string, object> { { "level", level }, { "ok", true } });
        }
        if (GUILayout.Button("Log error (same fingerprint each time)"))
        {
            errorCounter++;
            Debug.LogError("Demo error: failed to load slot " + errorCounter); // numbers are normalized -> one issue
        }
        if (GUILayout.Button("Log warning"))
        {
            Debug.LogWarning("Demo warning at level " + level);
        }
        if (GUILayout.Button("Throw exception"))
        {
            Debug.LogException(new InvalidOperationException("Demo exception"));
        }
        if (GUILayout.Button("Report bug (with screenshot)"))
        {
            PlaytestTelemetry.ReportBug("Demo bug report from level " + level, true);
        }
        if (GUILayout.Button("Flush now"))
        {
            PlaytestTelemetry.Flush();
        }
        GUILayout.EndArea();
    }
}
