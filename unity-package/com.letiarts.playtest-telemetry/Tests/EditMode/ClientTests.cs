using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LetiArts.PlaytestTelemetry.Tests
{
    /// <summary>Drives a TelemetryClient directly (no runner, no network) with the in-memory transport.</summary>
    public class ClientTests
    {
        string dir;
        TelemetrySettings settings;
        InMemoryTransport transport;
        readonly List<TelemetryClient> clients = new List<TelemetryClient>();

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "pt-client-" + Guid.NewGuid().ToString("N"));
            settings = ScriptableObject.CreateInstance<TelemetrySettings>();
            settings.endpointUrl = "https://example.invalid";
            settings.apiKey = "test-key";
            settings.deviceIdSalt = "salt";
            settings.flushIntervalSeconds = 1f;
            settings.flushOnException = false;
            settings.minLogLevel = TelemetryLogLevel.Warning;
            settings.enablePerformance = false;   // keep payloads deterministic
            settings.enableSceneCollector = false;
            settings.internalLogging = false;
            transport = new InMemoryTransport();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (TelemetryClient c in clients) c.Stop();
            clients.Clear();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            UnityEngine.Object.DestroyImmediate(settings);
        }

        TelemetryClient NewClient(InMemoryTransport t = null)
        {
            var c = new TelemetryClient(settings, t ?? transport, dir);
            c.Start();
            clients.Add(c);
            return c;
        }

        static LogCollector.RawLog Raw(string message, LogType type, string stack = "Game.Foo:Bar () (at Foo.cs:1)")
        {
            return new LogCollector.RawLog { Condition = message, StackTrace = stack, Type = type, Ts = Clock.NowMs() };
        }

        static int Count(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ---------------------------------------------------------------- aggregation

        [Test]
        public void IdenticalFingerprints_BecomeOneEventWithRepeatCount()
        {
            TelemetryClient c = NewClient();
            for (int i = 0; i < 5; i++) c.Logs.Ingest(Raw("NullReference at slot " + i, LogType.Error));
            c.Logs.Ingest(Raw("something else entirely", LogType.Error));
            c.Flush();
            c.Tick(0.01f);

            Assert.AreEqual(1, transport.RequestCount);
            string body = transport.SentBodies[0];
            Assert.AreEqual(2, Count(body, "\"log_type\":\"Error\""));
            Assert.AreEqual(1, Count(body, "\"repeat_count\":5"));
            Assert.AreEqual(1, Count(body, "\"message_template\":\"NullReference at slot <n>\""));
        }

        [Test]
        public void StackTrace_IsSentOnlyTheFirstTimeAFingerprintIsSeen()
        {
            TelemetryClient c = NewClient();
            c.Logs.Ingest(Raw("boom 1", LogType.Error));
            c.Flush(); c.Tick(0.01f);
            c.Logs.Ingest(Raw("boom 2", LogType.Error));
            c.Flush(); c.Tick(0.01f);

            Assert.AreEqual(2, transport.RequestCount);
            StringAssert.Contains("\"stack_trace\"", transport.SentBodies[0]);
            StringAssert.DoesNotContain("\"stack_trace\"", transport.SentBodies[1]);
        }

        [Test]
        public void AggregationDictionaryIsClearedAfterEachFlush()
        {
            TelemetryClient c = NewClient();
            c.Logs.Ingest(Raw("boom 1", LogType.Error));
            c.Logs.Ingest(Raw("boom 1", LogType.Error));
            c.Flush(); c.Tick(0.01f);
            c.Logs.Ingest(Raw("boom 1", LogType.Error));
            c.Flush(); c.Tick(0.01f);

            StringAssert.Contains("\"repeat_count\":2", transport.SentBodies[0]);
            StringAssert.Contains("\"repeat_count\":1", transport.SentBodies[1]);
        }

        [Test]
        public void LogsBelowMinimumLevelAreNotSent_ButStillBecomeBreadcrumbs()
        {
            TelemetryClient c = NewClient();
            c.Logs.Ingest(Raw("just info", LogType.Log));
            c.Logs.Ingest(Raw("boom", LogType.Error));
            c.Flush(); c.Tick(0.01f);

            string body = transport.SentBodies[0];
            Assert.AreEqual(1, Count(body, "\"log_type\":"));
            StringAssert.Contains("just info", body);          // present only inside the error's breadcrumbs
            StringAssert.Contains("\"breadcrumbs\":[", body);
        }

        [Test]
        public void ErrorsCarryContext_AndWarningsDoNot()
        {
            TelemetryClient c = NewClient();
            c.SetContext("level", "3");
            c.Logs.Ingest(Raw("bad", LogType.Error));
            c.Logs.Ingest(Raw("meh", LogType.Warning));
            c.Flush(); c.Tick(0.01f);

            string body = transport.SentBodies[0];
            Assert.AreEqual(1, Count(body, "\"context\":{\"level\":\"3\"}"));
            Assert.AreEqual(1, Count(body, "\"breadcrumbs\":["));
        }

        [Test]
        public void OwnLogMessagesAreIgnoredByTheSelfLogGuard()
        {
            TelemetryClient c = NewClient();
            c.Logs.OnLog(PtLog.Prefix + " send failed", "", LogType.Error);
            c.Logs.OnLog("real game error", "Game.Foo:Bar ()", LogType.Error);
            c.Logs.Tick(0f);
            c.Flush(); c.Tick(0.01f);

            string body = transport.SentBodies[0];
            StringAssert.Contains("real game error", body);
            StringAssert.DoesNotContain("send failed", body);
        }

        // ---------------------------------------------------------------- transport behaviour

        [Test]
        public void EveryRequestCarriesTheSessionBlock_WithHashedDeviceId()
        {
            TelemetryClient c = NewClient();
            c.TrackEvent("hello", null);
            c.Flush(); c.Tick(0.01f);

            string body = transport.SentBodies[0];
            StringAssert.Contains("\"session\":{\"id\":\"" + c.Session.Id + "\"", body);
            StringAssert.Contains("\"event_name\":\"hello\"", body);
            Assert.AreEqual(64, c.Session.DeviceId.Length);   // hex SHA-256
            StringAssert.DoesNotContain(SystemInfo.deviceUniqueIdentifier, body);
        }

        [Test]
        public void FailedSend_RequeuesAndRetriesAfterBackoff_NeverDropsData()
        {
            TelemetryClient c = NewClient();
            transport.NextOutcome = TransportOutcome.Retry;
            c.Logs.Ingest(Raw("boom", LogType.Error));
            c.Flush(); c.Tick(0.01f);

            Assert.AreEqual(1, transport.RequestCount);
            Assert.AreEqual(1, c.Queue.Count, "batch must be back in the queue");

            transport.NextOutcome = TransportOutcome.Success;
            c.Tick(0.5f);                         // still inside the backoff window (>= 1.6 s)
            Assert.AreEqual(1, transport.RequestCount);

            c.Tick(3f);                           // past the backoff
            Assert.AreEqual(2, transport.RequestCount);
            Assert.AreEqual(0, c.Queue.Count);
            Assert.AreEqual(transport.SentBodies[0].Contains("\"message\":\"boom\""), transport.SentBodies[1].Contains("\"message\":\"boom\""));
        }

        [Test]
        public void RetryAfterHeaderIsRespected()
        {
            TelemetryClient c = NewClient();
            transport.NextOutcome = TransportOutcome.Retry;
            transport.NextRetryAfter = 60f;
            c.TrackEvent("e", null);
            c.Flush(); c.Tick(0.01f);

            transport.NextOutcome = TransportOutcome.Success;
            c.Tick(30f);
            Assert.AreEqual(1, transport.RequestCount);
            c.Tick(31f);
            Assert.AreEqual(2, transport.RequestCount);
        }

        [Test]
        public void ServerRejection_DropsTheBatch_ButNotFutureOnes()
        {
            TelemetryClient c = NewClient();
            transport.NextOutcome = TransportOutcome.Drop;
            c.TrackEvent("bad", null);
            c.Flush(); c.Tick(0.01f);
            Assert.AreEqual(0, c.Queue.Count);

            transport.NextOutcome = TransportOutcome.Success;
            c.TrackEvent("good", null);
            c.Flush(); c.Tick(0.01f);
            Assert.AreEqual(2, transport.RequestCount);
        }

        [Test]
        public void Unauthorized_PausesUploads_AndKeepsTheData()
        {
            TelemetryClient c = NewClient();
            transport.NextOutcome = TransportOutcome.Unauthorized;
            c.TrackEvent("e", null);
            c.Flush(); c.Tick(0.01f);
            Assert.AreEqual(1, c.Queue.Count);

            transport.NextOutcome = TransportOutcome.Success;
            c.Tick(5f);
            Assert.AreEqual(1, transport.RequestCount);
        }

        [Test]
        public void LargeQueue_IsSplitIntoBatchesUnderTheSizeLimit()
        {
            TelemetryClient c = NewClient();
            string big = new string('x', 100 * 1024);
            for (int i = 0; i < 4; i++) c.Enqueue(new QueueItem(ItemKind.Bug, ItemPriority.Bug, "{\"tester_note\":\"" + big + "\"}"));
            settings.gzip = false;
            c.Flush(); c.Tick(0.01f);
            c.Tick(0.01f); c.Tick(0.01f);
            Assert.GreaterOrEqual(transport.RequestCount, 2);
            foreach (TransportRequest r in transport.Requests) Assert.LessOrEqual(r.Body.Length, TelemetryClient.MaxBatchBytes + 110 * 1024);
        }

        [Test]
        public void Gzip_CompressesLargerPayloads()
        {
            TelemetryClient c = NewClient();
            for (int i = 0; i < 20; i++) c.TrackEvent("event_" + i, new Dictionary<string, object> { { "pad", new string('a', 100) } });
            c.Flush(); c.Tick(0.01f);
            Assert.IsTrue(transport.Requests[0].Gzip);
            StringAssert.Contains("event_19", transport.SentBodies[0]);   // decoded by the fake transport
        }

        // ---------------------------------------------------------------- disk persistence and sessions

        [Test]
        public void Pause_PersistsTheQueueAndUploadsIt()
        {
            TelemetryClient c = NewClient();
            c.Logs.Ingest(Raw("boom", LogType.Error));
            c.OnPause(true);

            Assert.AreEqual(0, c.Queue.Count);
            Assert.AreEqual(1, transport.RequestCount, "persisted snapshot is uploaded right away");
            StringAssert.Contains("\"message\":\"boom\"", transport.SentBodies[0]);
            c.Tick(0.01f);
            Assert.AreEqual(0, c.Disk.ListFiles().Count, "snapshot deleted after a successful upload");
        }

        [Test]
        public void Pause_WhileOffline_KeepsTheSnapshotForNextTime()
        {
            transport.NextOutcome = TransportOutcome.Retry;
            TelemetryClient c = NewClient();
            c.Logs.Ingest(Raw("boom", LogType.Error));
            c.OnPause(true);
            c.Tick(0.01f);
            Assert.AreEqual(1, c.Disk.ListFiles().Count);
        }

        [Test]
        public void Snapshots_AreUploadedAtTheNextLaunch()
        {
            transport.NextOutcome = TransportOutcome.Retry;
            TelemetryClient first = NewClient();
            first.Logs.Ingest(Raw("from last time", LogType.Error));
            first.OnPause(true);
            first.Stop();

            var second = new InMemoryTransport();
            TelemetryClient next = NewClient(second);
            next.Tick(0.01f);
            Assert.GreaterOrEqual(second.RequestCount, 1);
            StringAssert.Contains("from last time", second.SentBodies[0]);
            StringAssert.Contains("\"id\":\"" + first.Session.Id + "\"", second.SentBodies[0]);   // the old session's block
        }

        [Test]
        public void MarkerLeftBehind_ReportsThePreviousSessionAsAbnormal()
        {
            TelemetryClient crashed = NewClient();    // never quits cleanly: marker stays on disk
            string crashedId = crashed.Session.Id;
            crashed.Stop();

            var second = new InMemoryTransport();
            TelemetryClient next = NewClient(second);
            next.Tick(0.01f);

            Assert.GreaterOrEqual(second.RequestCount, 1);
            string body = second.SentBodies[0];
            StringAssert.Contains("\"id\":\"" + crashedId + "\"", body);
            StringAssert.Contains("\"end_reason\":\"abnormal\"", body);
        }

        [Test]
        public void CleanQuit_RemovesTheMarker_AndEndsTheSessionCleanly()
        {
            transport.NextOutcome = TransportOutcome.Retry;   // keep the final snapshot on disk to inspect it
            TelemetryClient c = NewClient();
            c.OnQuit();

            Assert.IsFalse(File.Exists(Path.Combine(dir, "running.session")));
            PendingUpload u = c.Disk.Load(c.Disk.ListFiles()[0]);
            StringAssert.Contains("\"end_reason\":\"clean\"", u.Header);
            StringAssert.Contains("\"ended_at\":", u.Header);

            // And a second launch does not report it as abnormal.
            var second = new InMemoryTransport();
            NewClient(second).Tick(0.01f);
            foreach (string b in second.SentBodies) StringAssert.DoesNotContain("abnormal", b);
        }

        // ---------------------------------------------------------------- consent, modules

        [Test]
        public void WithoutConsent_NothingIsCollectedOrSent()
        {
            settings.requireConsent = true;
            PlayerPrefs.DeleteKey("PlaytestTelemetry.Consent");
            TelemetryClient c = NewClient();
            c.TrackEvent("e", null);
            c.Logs.Ingest(Raw("boom", LogType.Error));
            c.Flush(); c.Tick(0.01f);
            Assert.AreEqual(0, transport.RequestCount);
            Assert.AreEqual(0, c.Queue.Count);

            c.SetConsent(true);
            c.TrackEvent("after", null);
            c.Flush(); c.Tick(0.01f);
            Assert.AreEqual(1, transport.RequestCount);
            PlayerPrefs.DeleteKey("PlaytestTelemetry.Consent");
        }

        sealed class ThrowingModule : ITelemetryModule
        {
            public int Ticks;
            public bool ShutDown;
            public void Initialize(TelemetryContext context) { }
            public void Tick(float dt) { Ticks++; throw new InvalidOperationException("boom"); }
            public void Shutdown() { ShutDown = true; }
        }

        [Test]
        public void FailingModule_NeverThrowsIntoTheGame_AndIsDisabledAfterRepeatedFailures()
        {
            TelemetryClient c = NewClient();
            var bad = new ThrowingModule();
            c.AddModule(bad);
            for (int i = 0; i < 20; i++) Assert.DoesNotThrow(() => c.Tick(0.01f));
            Assert.AreEqual(ModuleHost.MaxConsecutiveFailures, bad.Ticks);
            Assert.IsTrue(bad.ShutDown);
        }

        [Test]
        public void ContextAndAliasEventsAreRaised()
        {
            TelemetryClient c = NewClient();
            var seen = new List<string>();
            c.Context.ContextChanged += (k, v) => seen.Add(k + "=" + (v ?? "<removed>"));
            c.Context.AliasChanged += a => seen.Add("alias=" + a);
            c.SetContext("level", "2");
            c.SetTesterAlias("alice");
            c.RemoveContext("level");
            CollectionAssert.AreEqual(new[] { "level=2", "alias=alice", "level=<removed>" }, seen);
        }
    }
}
