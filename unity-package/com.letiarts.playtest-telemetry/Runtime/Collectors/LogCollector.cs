using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// Captures Unity logs. The (possibly off-thread) callback only enqueues raw entries; fingerprinting,
    /// aggregation and serialization happen on the main thread in Tick.
    /// Identical fingerprints within one flush window become one event with repeat_count.
    /// </summary>
    internal sealed class LogCollector : ITelemetryModule, IFlushableModule
    {
        internal struct RawLog
        {
            public string Condition;
            public string StackTrace;
            public LogType Type;
            public long Ts;
        }

        sealed class Aggregate
        {
            public string LogType;
            public int Priority;
            public string Fingerprint;
            public string Message;
            public string Template;
            public string Stack;
            public long Ts;
            public string Scene;
            public string ContextJson;
            public string BreadcrumbsJson;
            public int Count;
        }

        sealed class CachedFingerprint
        {
            public string Fingerprint;
            public string Template;
        }

        const int MaxRawQueue = 2000;
        const int MaxPerTick = 100;
        const int MaxAggregates = 200;
        const int MaxFingerprintCache = 512;
        const int MaxStackSentEntries = 2000;
        const int MaxMessageLength = 512;
        const int MaxStackLength = 8192;

        readonly ConcurrentQueue<RawLog> raw = new ConcurrentQueue<RawLog>();
        readonly Dictionary<string, Aggregate> aggregates = new Dictionary<string, Aggregate>();
        readonly Dictionary<ulong, CachedFingerprint> fingerprintCache = new Dictionary<ulong, CachedFingerprint>();
        readonly HashSet<string> stackSent = new HashSet<string>();
        TelemetryContext context;
        int rawCount;
        int rawDropped;
        bool subscribed;

        public void Initialize(TelemetryContext ctx)
        {
            context = ctx;
            Application.logMessageReceivedThreaded += OnLog;
            subscribed = true;
        }

        public void Shutdown()
        {
            if (subscribed) Application.logMessageReceivedThreaded -= OnLog;
            subscribed = false;
        }

        // May be called on any thread: only enqueue.
        internal void OnLog(string condition, string stackTrace, LogType type)
        {
            if (condition != null && condition.StartsWith(PtLog.Prefix, System.StringComparison.Ordinal)) return; // self-log guard
            if (Volatile.Read(ref rawCount) >= MaxRawQueue)
            {
                Interlocked.Increment(ref rawDropped);
                return;
            }
            Interlocked.Increment(ref rawCount);
            raw.Enqueue(new RawLog { Condition = condition, StackTrace = stackTrace, Type = type, Ts = Clock.NowMs() });
        }

        public void Tick(float unscaledDt)
        {
            for (int i = 0; i < MaxPerTick; i++)
            {
                RawLog log;
                if (!raw.TryDequeue(out log)) break;
                Interlocked.Decrement(ref rawCount);
                Ingest(log);
            }
        }

        /// <summary>Processes one raw entry (main thread). Exposed to tests.</summary>
        internal void Ingest(RawLog log)
        {
            if (!context.CanCollect) return;

            string level;
            int priority;
            int rank;
            switch (log.Type)
            {
                case LogType.Warning: level = "Warning"; priority = ItemPriority.Warning; rank = (int)TelemetryLogLevel.Warning; break;
                case LogType.Error:
                case LogType.Assert: level = "Error"; priority = ItemPriority.Error; rank = (int)TelemetryLogLevel.Error; break;
                case LogType.Exception: level = "Exception"; priority = ItemPriority.Exception; rank = (int)TelemetryLogLevel.Exception; break;
                default: level = "Info"; priority = ItemPriority.Info; rank = (int)TelemetryLogLevel.Info; break;
            }

            context.Client.Breadcrumbs.Add(level, log.Condition);

            if (rank < (int)context.Settings.minLogLevel) return;

            string condition = log.Condition ?? "";
            string stack = log.StackTrace ?? "";
            ulong rawKey = Fnv1a.HashChars(stack, Fnv1a.HashChars(condition) ^ (ulong)log.Type);

            string key;
            string fingerprint = null;
            string template = null;
            if (priority == ItemPriority.Info)
            {
                key = "i|" + rawKey;
            }
            else
            {
                CachedFingerprint cached;
                if (!fingerprintCache.TryGetValue(rawKey, out cached))
                {
                    string normalized = Fingerprinter.NormalizeMessage(condition);
                    cached = new CachedFingerprint
                    {
                        Template = normalized,
                        Fingerprint = Fingerprinter.Compute(normalized, Fingerprinter.TopFrames(stack, 3)),
                    };
                    if (fingerprintCache.Count >= MaxFingerprintCache) fingerprintCache.Clear();
                    fingerprintCache[rawKey] = cached;
                }
                fingerprint = cached.Fingerprint;
                template = cached.Template;
                key = fingerprint;
            }

            Aggregate agg;
            if (aggregates.TryGetValue(key, out agg))
            {
                agg.Count++;
            }
            else
            {
                if (aggregates.Count >= MaxAggregates) OnFlush(); // bounded: hand over early instead of dropping

                agg = new Aggregate
                {
                    LogType = level,
                    Priority = priority,
                    Fingerprint = fingerprint,
                    Message = condition.Length > MaxMessageLength ? condition.Substring(0, MaxMessageLength) : condition,
                    Template = template,
                    Ts = log.Ts,
                    Scene = context.CurrentScene,
                    Count = 1,
                };

                if (fingerprint != null)
                {
                    // The server keeps the stack trace on the issue: send it once per fingerprint per session.
                    if (stackSent.Count >= MaxStackSentEntries) stackSent.Clear();
                    if (stackSent.Add(fingerprint) && stack.Length > 0)
                    {
                        agg.Stack = stack.Length > MaxStackLength ? stack.Substring(0, MaxStackLength) : stack;
                    }
                }
                if (priority >= ItemPriority.Error)
                {
                    agg.ContextJson = context.Client.ContextValues.ToJson();
                    agg.BreadcrumbsJson = context.Client.Breadcrumbs.ToJson();
                }
                aggregates[key] = agg;
            }

            if (log.Type == LogType.Exception && context.Settings.flushOnException) context.Client.RequestImmediateFlush();
        }

        /// <summary>Hands the aggregated events to the queue and clears the (bounded) fingerprint dictionary.</summary>
        public void OnFlush()
        {
            foreach (KeyValuePair<string, Aggregate> kv in aggregates)
            {
                Aggregate a = kv.Value;
                string json = ItemBuilder.LogEvent(
                    a.Ts, a.LogType, a.Fingerprint, a.Message, a.Template, a.Stack,
                    a.Count, a.Scene, a.ContextJson, a.BreadcrumbsJson);
                context.Client.Enqueue(new QueueItem(ItemKind.Event, a.Priority, json));
            }
            aggregates.Clear();

            int dropped = Interlocked.Exchange(ref rawDropped, 0);
            if (dropped > 0) context.Client.ReportDropped(dropped);
        }
    }
}
