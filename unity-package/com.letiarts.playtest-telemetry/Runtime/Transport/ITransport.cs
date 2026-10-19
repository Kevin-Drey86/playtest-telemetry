using System;

namespace LetiArts.PlaytestTelemetry
{
    internal enum TransportOutcome
    {
        /// <summary>2xx: stored.</summary>
        Success,
        /// <summary>Network error, timeout, 408, 429 or 5xx: keep the data and retry later.</summary>
        Retry,
        /// <summary>4xx the server will never accept (400, 409, 413, 422): retrying cannot help.</summary>
        Drop,
        /// <summary>401: the API key is wrong. Stop sending for a while.</summary>
        Unauthorized,
    }

    internal sealed class TransportRequest
    {
        public string Url;
        public string ApiKey;
        public byte[] Body;
        public bool Gzip;
    }

    internal struct TransportResult
    {
        public TransportOutcome Outcome;
        public long StatusCode;
        /// <summary>Seconds from the Retry-After header (429), or 0.</summary>
        public float RetryAfterSeconds;
        public string Detail;
    }

    /// <summary>
    /// Sends one request at a time without blocking. The client calls <see cref="Poll"/> every frame;
    /// the callback is invoked from Poll (main thread).
    /// </summary>
    internal interface ITransport
    {
        bool IsBusy { get; }
        void Send(TransportRequest request, Action<TransportResult> onComplete);
        void Poll();
    }
}
