using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace LetiArts.PlaytestTelemetry.Editor
{
    /// <summary>Posts a test session + event to the configured endpoint (used by the settings page).</summary>
    internal static class TestEventSender
    {
        public static void Send(TelemetrySettings settings, Action<string> onResult)
        {
            var w = new JsonWriter(512);
            long now = Clock.NowMs();
            w.BeginObject();
            w.Name("session").BeginObject()
                .Prop("id", Guid.NewGuid().ToString("D"))
                .Prop("device_id", SessionInfo.HashDeviceId("editor-test", settings.deviceIdSalt))
                .Prop("tester_alias", "editor-test")
                .Prop("build_version", Application.version)
                .Prop("platform", "Editor")
                .Prop("device_model", SystemInfo.deviceModel)
                .Prop("os", SystemInfo.operatingSystem)
                .Prop("started_at", now)
                .Prop("last_seen_at", now)
                .Prop("ended_at", now)
                .Prop("end_reason", "clean")
                .EndObject();
            w.Name("events").BeginArray().BeginObject()
                .Prop("client_ts", now)
                .Prop("log_type", "Event")
                .Prop("event_name", "pt_test_event")
                .Name("props").BeginObject().Prop("source", "unity-editor").Prop("unity", Application.unityVersion).EndObject()
                .EndObject().EndArray();
            w.EndObject();

            var request = new UnityWebRequest(settings.IngestUrl(), UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(w.ToString())) { contentType = "application/json" };
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("X-Telemetry-Key", settings.apiKey);
            request.timeout = 15;
            UnityWebRequestAsyncOperation op = request.SendWebRequest();

            void Poll()
            {
                if (!op.isDone) return;
                EditorApplication.update -= Poll;
                string message;
                if (request.result == UnityWebRequest.Result.ConnectionError)
                    message = "FAILED: could not reach the endpoint (" + request.error + ")";
                else if (request.responseCode == 200)
                    message = "OK: " + request.downloadHandler.text;
                else
                    message = "FAILED: HTTP " + request.responseCode + " " + request.downloadHandler.text;
                request.Dispose();
                onResult(message);
            }
            EditorApplication.update += Poll;
        }
    }
}
