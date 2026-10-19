using System;

namespace LetiArts.PlaytestTelemetry
{
    internal static class Backoff
    {
        public const float BaseSeconds = 2f;
        public const float CapSeconds = 300f;

        /// <summary>
        /// Exponential backoff with +/-20% jitter: 2 s, 4 s, 8 s, ... capped at 5 minutes.
        /// <paramref name="attempt"/> is 0 for the first retry; <paramref name="jitter01"/> in [0,1] (0.5 = no jitter).
        /// </summary>
        public static float Delay(int attempt, float jitter01)
        {
            if (attempt < 0) attempt = 0;
            if (attempt > 20) attempt = 20;
            double exponential = BaseSeconds * Math.Pow(2, attempt);
            double jittered = exponential * (0.8 + 0.4 * Math.Max(0f, Math.Min(1f, jitter01)));
            return (float)Math.Min(CapSeconds, jittered);
        }
    }
}
