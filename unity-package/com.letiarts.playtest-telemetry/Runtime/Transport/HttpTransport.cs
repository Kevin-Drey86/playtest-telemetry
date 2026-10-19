using System;
using System.Globalization;
using UnityEngine.Networking;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>UnityWebRequest transport. Polled from the main thread; never blocks.</summary>
    internal sealed class HttpTransport : ITransport
    {
        const int TimeoutSeconds = 20;

        UnityWebRequest request;
        UnityWebRequestAsyncOperation operation;
        Action<TransportResult> callback;

        public bool IsBusy => request != null;

        public void Send(TransportRequest r, Action<TransportResult> onComplete)
        {
            callback = onComplete;
            request = new UnityWebRequest(r.Url, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(r.Body) { contentType = "application/json" };
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("X-Telemetry-Key", r.ApiKey);
            if (r.Gzip) request.SetRequestHeader("Content-Encoding", "gzip");
            request.timeout = TimeoutSeconds;
            operation = request.SendWebRequest();
        }

        public void Poll()
        {
            if (request == null || !operation.isDone) return;

            UnityWebRequest finished = request;
            Action<TransportResult> cb = callback;
            request = null;
            operation = null;
            callback = null;

            TransportResult result;
            try
            {
                result = Interpret(finished);
            }
            finally
            {
                finished.Dispose();
            }
            if (cb != null) cb(result);
        }

        static TransportResult Interpret(UnityWebRequest req)
        {
            long code = req.responseCode;
            var result = new TransportResult { StatusCode = code, Detail = req.error };

            if (req.result == UnityWebRequest.Result.ConnectionError || code == 0)
            {
                result.Outcome = TransportOutcome.Retry;
            }
            else if (code >= 200 && code < 300)
            {
                result.Outcome = TransportOutcome.Success;
                result.Detail = req.downloadHandler != null ? req.downloadHandler.text : null; // {"success":true,"accepted":n,"dropped":n}
            }
            else if (code == 429 || code == 408 || code >= 500)
            {
                result.Outcome = TransportOutcome.Retry;
                string retryAfter = req.GetResponseHeader("Retry-After");
                float seconds;
                if (!string.IsNullOrEmpty(retryAfter) &&
                    float.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                {
                    result.RetryAfterSeconds = seconds;
                }
            }
            else if (code == 401)
            {
                result.Outcome = TransportOutcome.Unauthorized;
            }
            else
            {
                result.Outcome = TransportOutcome.Drop;
            }
            return result;
        }
    }
}
