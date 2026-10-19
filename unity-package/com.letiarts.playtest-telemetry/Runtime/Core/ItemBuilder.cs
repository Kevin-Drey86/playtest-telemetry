using System.Collections.Generic;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Serializes queue items to the JSON shapes in docs/ingest-protocol.md.
    /// Uses one shared writer: main thread only.
    /// </summary>
    internal static class ItemBuilder
    {
        static readonly JsonWriter W = new JsonWriter(2048);

        public static string LogEvent(
            long ts, string logType, string fingerprint, string message, string template, string stack,
            int repeatCount, string scene, string contextJson, string breadcrumbsJson)
        {
            W.Clear();
            W.BeginObject();
            W.Prop("client_ts", ts);
            W.Prop("log_type", logType);
            if (fingerprint != null) W.Prop("fingerprint", fingerprint);
            W.Prop("message", message);
            if (template != null) W.Prop("message_template", template);
            if (stack != null) W.Prop("stack_trace", stack);
            W.Prop("repeat_count", repeatCount);
            W.Prop("scene", scene ?? "");
            if (contextJson != null) W.PropRaw("context", contextJson);
            if (breadcrumbsJson != null) W.PropRaw("breadcrumbs", breadcrumbsJson);
            W.EndObject();
            return W.ToString();
        }

        public static string CustomEvent(long ts, string name, IDictionary<string, object> props, string scene)
        {
            W.Clear();
            W.BeginObject();
            W.Prop("client_ts", ts);
            W.Prop("log_type", "Event");
            W.Prop("event_name", name);
            if (props != null && props.Count > 0)
            {
                W.Name("props");
                W.Value((object)props);
            }
            W.Prop("scene", scene ?? "");
            W.EndObject();
            return W.ToString();
        }

        public static string PerfSample(
            long ts, string scene, double fpsAvg, double fpsP1Low, double frameMsMax,
            double? mainThreadMs, double? gcAllocKb, int usedMemMb,
            double? batteryLevel, string batteryStatus, string thermalState)
        {
            W.Clear();
            W.BeginObject();
            W.Prop("client_ts", ts);
            W.Prop("scene", scene ?? "");
            W.Prop("fps_avg", fpsAvg);
            W.Prop("fps_p1_low", fpsP1Low);
            W.Prop("frame_ms_max", frameMsMax);
            if (mainThreadMs.HasValue) W.Prop("main_thread_ms_avg", mainThreadMs.Value);
            if (gcAllocKb.HasValue) W.Prop("gc_alloc_kb", gcAllocKb.Value);
            W.Prop("used_mem_mb", usedMemMb);
            if (batteryLevel.HasValue) W.Prop("battery_level", batteryLevel.Value);
            if (batteryStatus != null) W.Prop("battery_status", batteryStatus);
            if (thermalState != null) W.Prop("thermal_state", thermalState);
            W.EndObject();
            return W.ToString();
        }

        public static string BugReport(long ts, string note, string screenshotBase64, string breadcrumbsJson, string contextJson)
        {
            W.Clear();
            W.BeginObject();
            W.Prop("client_ts", ts);
            W.Prop("tester_note", note ?? "");
            if (screenshotBase64 != null) W.Prop("screenshot_b64", screenshotBase64);
            if (breadcrumbsJson != null) W.PropRaw("breadcrumbs", breadcrumbsJson);
            if (contextJson != null) W.PropRaw("context", contextJson);
            W.EndObject();
            return W.ToString();
        }
    }
}
