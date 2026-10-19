using System;
using System.IO;
using System.Text;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Owns the "session running" marker. The marker is written at launch and deleted on a clean quit;
    /// if it still exists at the next launch the previous session ended abnormally (crash, kill, OOM) and
    /// is reported with end_reason = "abnormal" -- a crash signal even without Crashlytics.
    /// The marker holds the session JSON prefix so the previous session can be reported without more state.
    /// </summary>
    internal sealed class SessionLifecycle : ITelemetryModule, IApplicationLifecycleModule
    {
        const string MarkerName = "running.session";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        TelemetryContext context;
        string markerPath;

        public void Initialize(TelemetryContext ctx)
        {
            context = ctx;
            markerPath = Path.Combine(ctx.Client.Disk.Directory, MarkerName);

            ReportPreviousSessionIfAbnormal();
            WriteMarker();
        }

        void ReportPreviousSessionIfAbnormal()
        {
            try
            {
                if (!File.Exists(markerPath)) return;
                string previousPrefix = File.ReadAllText(markerPath, Utf8).Trim();
                if (previousPrefix.Length > 2 && previousPrefix[0] == '{')
                {
                    string header = previousPrefix + ",\"end_reason\":\"abnormal\"";
                    context.Client.Disk.WriteSnapshot(header, new System.Collections.Generic.List<QueueItem>());
                    PtLog.Info("previous session ended abnormally; reporting it");
                }
                File.Delete(markerPath);
            }
            catch (Exception e)
            {
                PtLog.Warn("could not check previous session: " + e.Message);
            }
        }

        void WriteMarker()
        {
            try
            {
                Directory.CreateDirectory(context.Client.Disk.Directory);
                File.WriteAllText(markerPath, context.Client.Session.JsonPrefix(), Utf8);
            }
            catch (Exception e)
            {
                PtLog.Warn("could not write session marker: " + e.Message);
            }
        }

        public void Tick(float unscaledDt) { }

        public void OnPause(bool paused) { }

        public void OnQuit()
        {
            try
            {
                if (File.Exists(markerPath)) File.Delete(markerPath);
            }
            catch (Exception e)
            {
                PtLog.Warn("could not remove session marker: " + e.Message);
            }
        }

        public void Shutdown() { }
    }
}
