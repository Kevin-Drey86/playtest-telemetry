using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>Everything about the current session (one per app launch). Device info lives here only, never on events.</summary>
    internal sealed class SessionInfo
    {
        public string Id;
        public string DeviceId;
        public string BuildVersion;
        public string Platform;
        public string DeviceModel;
        public string Os;
        public string Gpu;
        public int SystemMemoryMb;
        public string Screen;
        public long StartedAtMs;

        string testerAlias;
        string cachedPrefix;

        public string TesterAlias
        {
            get { return testerAlias; }
            set
            {
                testerAlias = value;
                cachedPrefix = null;
            }
        }

        public static SessionInfo Create(string salt)
        {
            return new SessionInfo
            {
                Id = Guid.NewGuid().ToString("D"),
                DeviceId = HashDeviceId(SystemInfo.deviceUniqueIdentifier, salt),
                BuildVersion = Application.version ?? "",
                Platform = Application.platform.ToString(),
                DeviceModel = SystemInfo.deviceModel ?? "",
                Os = SystemInfo.operatingSystem ?? "",
                Gpu = SystemInfo.graphicsDeviceName ?? "",
                SystemMemoryMb = SystemInfo.systemMemorySize,
                Screen = UnityEngine.Screen.width + "x" + UnityEngine.Screen.height,
                StartedAtMs = Clock.NowMs(),
            };
        }

        /// <summary>Hex SHA-256 of the raw device ID + project salt. The raw ID is never sent.</summary>
        public static string HashDeviceId(string rawId, string salt)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes((rawId ?? "") + (salt ?? "")));
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// The session JSON object WITHOUT its closing brace. Callers append more fields
        /// (last_seen_at, ended_at, end_reason) and close it. Stored as-is in disk-queue headers and the
        /// running marker so a later launch can finish the object.
        /// </summary>
        public string JsonPrefix()
        {
            if (cachedPrefix != null) return cachedPrefix;
            var w = new JsonWriter(512);
            w.BeginObject();
            w.Prop("id", Id);
            w.Prop("device_id", DeviceId);
            w.Prop("tester_alias", testerAlias);
            w.Prop("build_version", BuildVersion);
            w.Prop("platform", Platform);
            w.Prop("device_model", DeviceModel);
            w.Prop("os", Os);
            w.Prop("gpu", Gpu);
            w.Prop("system_memory_mb", SystemMemoryMb);
            w.Prop("screen", Screen);
            w.Prop("started_at", StartedAtMs);
            cachedPrefix = w.ToString();
            return cachedPrefix;
        }

        public static string EndFields(long endedAtMs, string endReason)
        {
            return ",\"ended_at\":" + endedAtMs + ",\"end_reason\":\"" + endReason + "\"";
        }

        /// <summary>The complete session object to put in a request.</summary>
        public string BuildJson(bool ended = false, string endReason = null)
        {
            long now = Clock.NowMs();
            string json = JsonPrefix() + ",\"last_seen_at\":" + now;
            if (ended) json += EndFields(now, endReason ?? "clean");
            return json + "}";
        }

        /// <summary>Header line content for a disk snapshot (no last_seen_at; that is added on upload).</summary>
        public string BuildHeader(bool ended = false, string endReason = null)
        {
            string header = JsonPrefix();
            if (ended) header += EndFields(Clock.NowMs(), endReason ?? "clean");
            return header;
        }
    }
}
