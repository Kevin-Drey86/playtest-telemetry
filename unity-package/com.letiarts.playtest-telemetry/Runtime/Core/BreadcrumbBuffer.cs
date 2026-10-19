using System.Collections.Generic;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Fixed-size ring buffer of recent activity. Kept on the device; only ever attached to
    /// Error/Exception events and bug reports. Thread-safe (manual breadcrumbs may come from any thread).
    /// </summary>
    internal sealed class BreadcrumbBuffer
    {
        const int MaxMessageLength = 256;
        const int MaxCategoryLength = 32;

        struct Crumb
        {
            public long Ts;
            public string Category;
            public string Message;
        }

        readonly object gate = new object();
        Crumb[] ring = new Crumb[0];
        int head;   // index of the next write
        int count;

        public bool Enabled;

        public void Resize(int capacity)
        {
            lock (gate)
            {
                ring = new Crumb[capacity < 0 ? 0 : capacity];
                head = 0;
                count = 0;
            }
        }

        public int Count
        {
            get { lock (gate) return count; }
        }

        public void Add(string category, string message)
        {
            if (!Enabled || message == null) return;
            if (message.Length > MaxMessageLength) message = message.Substring(0, MaxMessageLength);
            if (category != null && category.Length > MaxCategoryLength) category = category.Substring(0, MaxCategoryLength);
            long ts = Clock.NowMs();
            lock (gate)
            {
                if (ring.Length == 0) return;
                ring[head] = new Crumb { Ts = ts, Category = category ?? "", Message = message };
                head = (head + 1) % ring.Length;
                if (count < ring.Length) count++;
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                head = 0;
                count = 0;
            }
        }

        /// <summary>JSON array (oldest first) of {ts, category, message}, or null when empty.</summary>
        public string ToJson()
        {
            lock (gate)
            {
                if (count == 0) return null;
                var w = new JsonWriter(count * 64);
                w.BeginArray();
                int start = (head - count + ring.Length) % ring.Length;
                for (int i = 0; i < count; i++)
                {
                    Crumb c = ring[(start + i) % ring.Length];
                    w.BeginObject();
                    w.Prop("ts", c.Ts);
                    w.Prop("category", c.Category);
                    w.Prop("message", c.Message);
                    w.EndObject();
                }
                w.EndArray();
                return w.ToString();
            }
        }
    }

    /// <summary>Key/value context (level, tester state, ...) attached to errors and bug reports.</summary>
    internal sealed class ContextStore
    {
        public const int MaxKeys = 32;
        const int MaxKeyLength = 64;
        const int MaxValueLength = 256;

        readonly object gate = new object();
        readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public bool Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.Length > MaxKeyLength) key = key.Substring(0, MaxKeyLength);
            value = value ?? "";
            if (value.Length > MaxValueLength) value = value.Substring(0, MaxValueLength);
            lock (gate)
            {
                if (!values.ContainsKey(key) && values.Count >= MaxKeys) return false;
                values[key] = value;
                return true;
            }
        }

        public bool Remove(string key)
        {
            if (key == null) return false;
            lock (gate) return values.Remove(key);
        }

        public Dictionary<string, string> Snapshot()
        {
            lock (gate) return new Dictionary<string, string>(values);
        }

        /// <summary>JSON object, or null when empty.</summary>
        public string ToJson()
        {
            lock (gate)
            {
                if (values.Count == 0) return null;
                var w = new JsonWriter(values.Count * 48);
                w.BeginObject();
                foreach (KeyValuePair<string, string> kv in values) w.Prop(kv.Key, kv.Value);
                w.EndObject();
                return w.ToString();
            }
        }
    }
}
