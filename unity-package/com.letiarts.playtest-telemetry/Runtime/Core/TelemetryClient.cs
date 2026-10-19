using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>
    /// The engine: owns the modules, the in-memory queue, the disk queue and the transport.
    /// Driven from the main thread by <see cref="TelemetryRunner"/> (or directly by tests).
    ///
    /// Data flow: modules enqueue serialized items -> on each flush the queue is sent in batches
    /// (one request carries logs + perf samples + bug reports) -> failed batches are re-queued.
    /// On pause/quit the whole queue is persisted to disk; snapshot files are uploaded like any other
    /// batch (at the next launch, or right away after a pause), then deleted.
    /// </summary>
    internal sealed class TelemetryClient
    {
        public const int MaxBatchBytes = 256 * 1024;
        public const int MaxBatchItems = 400;                 // server limit is 500 per request
        public const float ImmediateFlushMinGapSeconds = 5f;  // exceptions in a loop must not spam requests
        const float AuthBlockSeconds = 600f;
        const string ConsentKey = "PlaytestTelemetry.Consent";

        sealed class InFlight
        {
            public List<QueueItem> MemoryItems; // set for batches taken from the memory queue
            public PendingUpload Upload;        // set for batches read from a disk snapshot
            public int Count;
            public bool Full;                   // a size/count limit stopped the batch: more data waits
        }

        public readonly TelemetrySettings Settings;
        public readonly SessionInfo Session;
        public readonly TelemetryContext Context;
        public readonly TelemetryQueue Queue;
        public readonly DiskQueue Disk;
        public readonly BreadcrumbBuffer Breadcrumbs = new BreadcrumbBuffer();
        public readonly ContextStore ContextValues = new ContextStore();
        public readonly ITransport Transport;

        public LogCollector Logs { get; private set; }
        public BugReportModule BugReports { get; private set; }
        public SceneCollector Scenes { get; private set; }
        public TelemetryRunner Runner;

        /// <summary>Outcome of the most recent request, and how many items the server reported as malformed (diagnostics).</summary>
        public TransportResult LastResult { get; private set; }
        public int LastServerDropped { get; private set; }
        public int SuccessfulRequests { get; private set; }

        readonly List<ModuleHost> modules = new List<ModuleHost>();
        readonly System.Random random = new System.Random();
        readonly IThermalProvider thermalOverride;

        float clock;               // accumulated unscaled seconds (deterministic in tests)
        float sinceFlush;
        float lastImmediateFlush = -1000f;
        bool forceFlush;
        bool immediateRequested;
        bool sendRequested;
        bool started;
        bool quitHandled;
        bool consentGiven;
        bool authWarned;
        long droppedReport;

        int attempts;
        float nextAttemptAt;
        float authBlockedUntil;
        InFlight inFlight;
        PendingUpload currentUpload;
        List<string> pendingFiles = new List<string>();

        public TelemetryClient(TelemetrySettings settings, ITransport transport, string directory = null, IThermalProvider thermal = null)
        {
            Settings = settings;
            Transport = transport;
            thermalOverride = thermal;
            Session = SessionInfo.Create(settings.deviceIdSalt);
            Context = new TelemetryContext(this);
            Queue = new TelemetryQueue(settings.maxMemoryQueueItems);
            Disk = new DiskQueue(directory ?? Path.Combine(Application.persistentDataPath, "playtest-telemetry"), settings.maxDiskQueueBytes);
            consentGiven = PlayerPrefs.GetInt(ConsentKey, 0) == 1;
        }

        public bool CanCollect => !Settings.requireConsent || consentGiven;
        public int PendingFileCount => pendingFiles.Count + (currentUpload != null ? 1 : 0);

        // ------------------------------------------------------------------ lifecycle

        public void Start()
        {
            if (started) return;
            started = true;

            AddModule(new SessionLifecycle());
            if (Settings.enableBreadcrumbs) AddModule(new BreadcrumbCollector());
            if (Settings.enableSceneCollector) AddModule(Scenes = new SceneCollector());
            if (Settings.enableLogCollector) AddModule(Logs = new LogCollector());
            if (Settings.enablePerformance) AddModule(new PerformanceCollector(thermalOverride ?? ThermalProviderFactory.Create()));
            if (Settings.enableBugReports) AddModule(BugReports = new BugReportModule());
            foreach (ITelemetryModule custom in PlaytestTelemetry.TakePendingModules()) AddModule(custom);

            RefreshPending();
            PtLog.Info("started, session " + Session.Id);
        }

        public void AddModule(ITelemetryModule module)
        {
            var host = new ModuleHost(module);
            modules.Add(host);
            host.Initialize(Context);
        }

        /// <summary>Shuts modules down without persisting anything (tests, re-initialization).</summary>
        public void Stop()
        {
            for (int i = 0; i < modules.Count; i++) modules[i].Shutdown();
            modules.Clear();
            started = false;
        }

        // ------------------------------------------------------------------ public API backing

        public void SetTesterAlias(string alias)
        {
            Session.TesterAlias = string.IsNullOrEmpty(alias) ? null : alias;
            Context.RaiseAliasChanged(Session.TesterAlias);
        }

        public void SetContext(string key, string value)
        {
            if (ContextValues.Set(key, value)) Context.RaiseContextChanged(key, value ?? "");
        }

        public void RemoveContext(string key)
        {
            if (ContextValues.Remove(key)) Context.RaiseContextChanged(key, null);
        }

        public void SetConsent(bool consent)
        {
            consentGiven = consent;
            PlayerPrefs.SetInt(ConsentKey, consent ? 1 : 0);
            if (!consent && Settings.requireConsent) Queue.Clear();
        }

        public void TrackEvent(string name, IDictionary<string, object> props)
        {
            if (!CanCollect || string.IsNullOrEmpty(name)) return;
            string json = ItemBuilder.CustomEvent(Clock.NowMs(), name, props, Context.CurrentScene);
            Enqueue(new QueueItem(ItemKind.Event, ItemPriority.Info, json));
        }

        public void Enqueue(QueueItem item)
        {
            if (!CanCollect) return;
            Queue.Enqueue(item);
        }

        public void ReportDropped(long count)
        {
            droppedReport += count;
        }

        /// <summary>Flush on the next tick, ignoring the minimum gap.</summary>
        public void Flush()
        {
            forceFlush = true;
        }

        /// <summary>Flush soon, but at most once every <see cref="ImmediateFlushMinGapSeconds"/>.</summary>
        public void RequestImmediateFlush()
        {
            immediateRequested = true;
        }

        // ------------------------------------------------------------------ per-frame work

        public void Tick(float unscaledDt)
        {
            if (!started || quitHandled) return;
            clock += unscaledDt;

            for (int i = 0; i < modules.Count; i++) modules[i].Tick(unscaledDt);

            sinceFlush += unscaledDt;
            bool due = forceFlush || sinceFlush >= Settings.flushIntervalSeconds;
            if (immediateRequested && clock - lastImmediateFlush >= ImmediateFlushMinGapSeconds)
            {
                due = true;
                immediateRequested = false;
                lastImmediateFlush = clock;
            }
            if (due)
            {
                FlushModules();
                sinceFlush = 0f;
                forceFlush = false;
                sendRequested = Queue.Count > 0;
            }

            Pump();
        }

        void FlushModules()
        {
            for (int i = 0; i < modules.Count; i++) modules[i].Flush();

            long dropped = Queue.TakeDropped() + droppedReport;
            droppedReport = 0;
            if (dropped > 0)
            {
                var props = new Dictionary<string, object> { { "count", dropped } };
                string json = ItemBuilder.CustomEvent(Clock.NowMs(), "pt_dropped", props, Context.CurrentScene);
                Queue.Enqueue(new QueueItem(ItemKind.Event, ItemPriority.Perf, json));
            }
        }

        // ------------------------------------------------------------------ sending

        void Pump()
        {
            Transport.Poll();
            if (Transport.IsBusy) return;
            if (!CanCollect || !Settings.IsConfigured) return;
            if (clock < nextAttemptAt || clock < authBlockedUntil) return;

            if (sendRequested && Queue.Count > 0) StartMemoryBatch();
            else if (currentUpload != null || pendingFiles.Count > 0) StartFileBatch();

            Transport.Poll(); // transports that complete synchronously (the in-memory fake) deliver now
        }

        void StartMemoryBatch()
        {
            var items = new List<QueueItem>();
            bool full = Queue.TakeBatch(MaxBatchBytes, MaxBatchItems, items);
            if (items.Count == 0) return;
            // Stays true while data from this flush is still waiting (large queues go out as several batches).
            if (Queue.Count == 0) sendRequested = false;
            inFlight = new InFlight { MemoryItems = items, Count = items.Count, Full = full };
            Send(Session.BuildJson(), items, 0, items.Count);
        }

        void StartFileBatch()
        {
            while (currentUpload == null && pendingFiles.Count > 0)
            {
                string path = pendingFiles[0];
                pendingFiles.RemoveAt(0);
                currentUpload = Disk.Load(path);
                if (currentUpload == null) Disk.Delete(path); // unreadable or corrupt
            }
            if (currentUpload == null) return;

            PendingUpload upload = currentUpload;
            int count = 0;
            int bytes = 0;
            for (int i = upload.Sent; i < upload.Items.Count; i++)
            {
                int size = upload.Items[i].Size;
                if (count > 0 && (count >= MaxBatchItems || bytes + size > MaxBatchBytes)) break;
                count++;
                bytes += size;
            }
            inFlight = new InFlight { Upload = upload, Count = count, Full = upload.Sent + count < upload.Items.Count };
            Send(upload.Header + ",\"last_seen_at\":" + upload.LastWriteMs + "}", upload.Items, upload.Sent, count);
        }

        void Send(string sessionJson, IList<QueueItem> items, int from, int count)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(BuildPayload(sessionJson, items, from, count));
                bool gzip = Settings.gzip && body.Length > 512;
                if (gzip) body = Compress(body);
                Transport.Send(
                    new TransportRequest { Url = Settings.IngestUrl(), ApiKey = Settings.apiKey, Body = body, Gzip = gzip },
                    OnResult);
            }
            catch (Exception e)
            {
                PtLog.Warn("send failed to start: " + e.Message);
                OnResult(new TransportResult { Outcome = TransportOutcome.Retry, Detail = e.Message });
            }
        }

        void OnResult(TransportResult result)
        {
            InFlight f = inFlight;
            inFlight = null;
            if (f == null) return; // the app is quitting and the batch was already persisted
            LastResult = result;

            switch (result.Outcome)
            {
                case TransportOutcome.Success:
                case TransportOutcome.Drop:
                    if (result.Outcome == TransportOutcome.Drop)
                    {
                        PtLog.Warn("server rejected a batch (HTTP " + result.StatusCode + "); dropping it");
                        droppedReport += f.Count;
                    }
                    attempts = 0;
                    nextAttemptAt = 0f;
                    if (result.Outcome == TransportOutcome.Success)
                    {
                        SuccessfulRequests++;
                        LastServerDropped = ParseDropped(result.Detail);
                        if (LastServerDropped > 0) PtLog.Warn("the server dropped " + LastServerDropped + " malformed item(s) from a batch");
                    }
                    if (f.Upload != null) FinishFileBatch(f);
                    break;

                case TransportOutcome.Unauthorized:
                    authBlockedUntil = clock + AuthBlockSeconds;
                    if (!authWarned)
                    {
                        authWarned = true;
                        PtLog.Warn("the server rejected the API key (HTTP 401). Check the project settings. Pausing uploads for 10 minutes.");
                    }
                    if (f.MemoryItems != null)
                    {
                        Queue.Restore(f.MemoryItems);
                        sendRequested = true;
                    }
                    break;

                default: // Retry: never drop on a failed send
                    float delay = Math.Max(Backoff.Delay(attempts, (float)random.NextDouble()), result.RetryAfterSeconds);
                    attempts++;
                    nextAttemptAt = clock + delay;
                    PtLog.Info("send failed (" + (result.StatusCode == 0 ? "network" : "HTTP " + result.StatusCode) + "); retrying in " + delay.ToString("0") + " s");
                    if (f.MemoryItems != null)
                    {
                        Queue.Restore(f.MemoryItems);
                        sendRequested = true;
                    }
                    break;
            }
        }

        /// <summary>Reads "dropped":N from the Worker response without a JSON parser.</summary>
        internal static int ParseDropped(string response)
        {
            if (string.IsNullOrEmpty(response)) return 0;
            const string key = "\"dropped\":";
            int i = response.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            int n = 0;
            while (i < response.Length && response[i] >= '0' && response[i] <= '9') n = n * 10 + (response[i++] - '0');
            return n;
        }

        void FinishFileBatch(InFlight f)
        {
            PendingUpload upload = f.Upload;
            upload.Sent += f.Count;
            if (upload.Sent >= upload.Items.Count)
            {
                Disk.Delete(upload.Path);
                if (currentUpload == upload) currentUpload = null;
            }
            else
            {
                Disk.Rewrite(upload); // keep only what the server has not accepted yet
            }
        }

        static string BuildPayload(string sessionJson, IList<QueueItem> items, int from, int count)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{\"session\":").Append(sessionJson);
            AppendKind(sb, "events", ItemKind.Event, items, from, count);
            AppendKind(sb, "perf_samples", ItemKind.Perf, items, from, count);
            AppendKind(sb, "bug_reports", ItemKind.Bug, items, from, count);
            return sb.Append('}').ToString();
        }

        static void AppendKind(StringBuilder sb, string name, char kind, IList<QueueItem> items, int from, int count)
        {
            sb.Append(",\"").Append(name).Append("\":[");
            bool first = true;
            for (int i = from; i < from + count; i++)
            {
                if (items[i].Kind != kind) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(items[i].Json);
            }
            sb.Append(']');
        }

        static byte[] Compress(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gz = new GZipStream(output, CompressionMode.Compress, true))
                {
                    gz.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        // ------------------------------------------------------------------ pause / quit

        public void OnPause(bool paused)
        {
            if (!started || quitHandled) return;
            for (int i = 0; i < modules.Count; i++) modules[i].Pause(paused);
            if (!paused) return;

            // Often the last callback before the OS kills the app: hand over buffered data, persist the
            // queue, then immediately try to upload what was persisted.
            FlushModules();
            PersistQueue(false, false);
            Pump();
        }

        public void OnQuit()
        {
            if (!started || quitHandled) return;
            FlushModules();
            // A request still in flight may not finish before the process exits: persist it too.
            if (inFlight != null && inFlight.MemoryItems != null)
            {
                Queue.Restore(inFlight.MemoryItems);
                inFlight = null;
            }
            PersistQueue(true, true);
            quitHandled = true;

            for (int i = 0; i < modules.Count; i++) modules[i].Quit();
            for (int i = 0; i < modules.Count; i++) modules[i].Shutdown();
        }

        void PersistQueue(bool ended, bool force)
        {
            var items = new List<QueueItem>();
            Queue.DrainAll(items);
            if (items.Count == 0 && !ended) return;

            string path = Disk.WriteSnapshot(Session.BuildHeader(ended, "clean"), items);
            if (path == null)
            {
                // Disk unavailable: keep the data in memory rather than losing it.
                if (!force) Queue.Restore(items);
                return;
            }
            droppedReport += Disk.EnforceCap();
            sendRequested = false;
            RefreshPending();
        }

        void RefreshPending()
        {
            pendingFiles = Disk.ListFiles();
            if (currentUpload != null) pendingFiles.Remove(currentUpload.Path);
        }
    }
}
