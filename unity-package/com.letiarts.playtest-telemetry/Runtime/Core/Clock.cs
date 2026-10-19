using System;

namespace LetiArts.PlaytestTelemetry
{
    internal static class Clock
    {
        /// <summary>Unix epoch milliseconds (UTC). Thread-safe.</summary>
        public static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    /// <summary>Internal logger. Everything it writes starts with the prefix the LogCollector ignores.</summary>
    internal static class PtLog
    {
        public const string Prefix = "[PlaytestTelemetry]";
        public static bool Enabled = true;

        public static void Info(string message)
        {
            if (Enabled) UnityEngine.Debug.Log(Prefix + " " + message);
        }

        // Deliberately a warning: our own problems must never show up as Error logs in the host game.
        public static void Warn(string message)
        {
            if (Enabled) UnityEngine.Debug.LogWarning(Prefix + " " + message);
        }
    }
}
