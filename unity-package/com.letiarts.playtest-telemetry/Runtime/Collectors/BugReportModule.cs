using System;
using System.Collections;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Builds bug reports: tester note + optional screenshot (end of frame, downscaled to 720 px on the
    /// long edge, JPEG quality 70, base64) + breadcrumbs + context. Sent promptly with the next flush.
    /// </summary>
    internal sealed class BugReportModule : ITelemetryModule
    {
        const int MaxScreenshotEdge = 720;
        const int JpegQuality = 70;
        const int MaxNoteLength = 2000;

        TelemetryContext context;

        public void Initialize(TelemetryContext ctx)
        {
            context = ctx;
        }

        public void Tick(float unscaledDt) { }

        public void Shutdown() { }

        public void Report(string note, bool includeScreenshot)
        {
            if (!context.CanCollect) return;
            if (note == null) note = "";
            if (note.Length > MaxNoteLength) note = note.Substring(0, MaxNoteLength);

            // Snapshot breadcrumbs/context NOW: they may change before the end-of-frame screenshot completes.
            string breadcrumbs = context.Client.Breadcrumbs.ToJson();
            string contextJson = context.Client.ContextValues.ToJson();
            long ts = Clock.NowMs();

            if (includeScreenshot && Application.isPlaying && TelemetryRunner.Instance != null)
            {
                TelemetryRunner.Instance.StartCoroutine(CaptureAndEnqueue(note, ts, breadcrumbs, contextJson));
            }
            else
            {
                Enqueue(note, ts, null, breadcrumbs, contextJson);
            }
        }

        IEnumerator CaptureAndEnqueue(string note, long ts, string breadcrumbs, string contextJson)
        {
            yield return new WaitForEndOfFrame();
            string base64 = null;
            try
            {
                base64 = CaptureScreenshotBase64();
            }
            catch (Exception e)
            {
                PtLog.Warn("screenshot failed: " + e.Message);
            }
            Enqueue(note, ts, base64, breadcrumbs, contextJson);
        }

        void Enqueue(string note, long ts, string base64, string breadcrumbs, string contextJson)
        {
            try
            {
                string json = ItemBuilder.BugReport(ts, note, base64, breadcrumbs, contextJson);
                context.Client.Enqueue(new QueueItem(ItemKind.Bug, ItemPriority.Bug, json));
                context.Client.RequestImmediateFlush();
            }
            catch (Exception e)
            {
                PtLog.Warn("bug report failed: " + e.Message);
            }
        }

        static string CaptureScreenshotBase64()
        {
            Texture2D full = ScreenCapture.CaptureScreenshotAsTexture();
            if (full == null) return null;

            Texture2D small = null;
            RenderTexture temp = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                int w = full.width, h = full.height;
                float scale = Mathf.Min(1f, MaxScreenshotEdge / (float)Mathf.Max(w, h));
                Texture2D source = full;
                if (scale < 1f)
                {
                    int nw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
                    int nh = Mathf.Max(1, Mathf.RoundToInt(h * scale));
                    temp = RenderTexture.GetTemporary(nw, nh, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(full, temp);
                    RenderTexture.active = temp;
                    small = new Texture2D(nw, nh, TextureFormat.RGB24, false);
                    small.ReadPixels(new Rect(0, 0, nw, nh), 0, 0);
                    small.Apply(false);
                    source = small;
                }
                byte[] jpg = source.EncodeToJPG(JpegQuality);
                return jpg != null && jpg.Length > 0 ? Convert.ToBase64String(jpg) : null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temp != null) RenderTexture.ReleaseTemporary(temp);
                if (small != null) UnityEngine.Object.Destroy(small);
                UnityEngine.Object.Destroy(full);
            }
        }
    }
}
