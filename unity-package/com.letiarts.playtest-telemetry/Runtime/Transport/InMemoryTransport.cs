using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Records requests instead of sending them. Used by the tests as the "FakeTransport", and handy for
    /// dry runs. The outcome of the next request is controlled with <see cref="NextOutcome"/>.
    /// </summary>
    internal sealed class InMemoryTransport : ITransport
    {
        public readonly List<string> SentBodies = new List<string>();
        public readonly List<TransportRequest> Requests = new List<TransportRequest>();
        public TransportOutcome NextOutcome = TransportOutcome.Success;
        public float NextRetryAfter;
        /// <summary>When true, a request completes on the next Poll; when false it stays in flight until <see cref="Complete"/>.</summary>
        public bool AutoComplete = true;

        Action<TransportResult> pending;
        TransportResult pendingResult;

        public bool IsBusy => pending != null;
        public int RequestCount => Requests.Count;

        public void Send(TransportRequest request, Action<TransportResult> onComplete)
        {
            Requests.Add(request);
            SentBodies.Add(DecodeBody(request));
            pending = onComplete;
            pendingResult = new TransportResult
            {
                Outcome = NextOutcome,
                StatusCode = NextOutcome == TransportOutcome.Success ? 200 : 503,
                RetryAfterSeconds = NextRetryAfter,
            };
        }

        public void Poll()
        {
            if (pending != null && AutoComplete) Complete();
        }

        public void Complete()
        {
            Action<TransportResult> cb = pending;
            pending = null;
            if (cb != null) cb(pendingResult);
        }

        static string DecodeBody(TransportRequest request)
        {
            if (!request.Gzip) return Encoding.UTF8.GetString(request.Body);
            using (var input = new MemoryStream(request.Body))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
