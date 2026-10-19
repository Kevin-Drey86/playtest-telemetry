using System;
using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LetiArts.PlaytestTelemetry.Tests
{
    /// <summary>Runs the real bootstrap (hidden runner, Update loop, Unity log callback) with a FakeTransport.</summary>
    public class RuntimeTests
    {
        string dir;
        TelemetrySettings settings;
        InMemoryTransport fake;
        TelemetryClient client;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "pt-play-" + Guid.NewGuid().ToString("N"));
            settings = ScriptableObject.CreateInstance<TelemetrySettings>();
            settings.endpointUrl = "https://example.invalid";
            settings.apiKey = "test-key";
            settings.deviceIdSalt = "salt";
            settings.flushIntervalSeconds = 3600f;     // flush only when the test asks
            settings.flushOnException = false;
            settings.minLogLevel = TelemetryLogLevel.Warning;
            settings.enablePerformance = false;
            settings.internalLogging = false;
            fake = new InMemoryTransport();
            client = PlaytestTelemetry.StartClient(settings, fake, dir, true);
            Assert.IsNotNull(client);
        }

        [TearDown]
        public void TearDown()
        {
            PlaytestTelemetry.ShutdownForTests();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            UnityEngine.Object.Destroy(settings);
        }

        // The test runner fails a test on any exception logged by Update unless it is declared.
        static void ExpectThrows(int times)
        {
            for (int i = 0; i < times; i++) LogAssert.Expect(LogType.Exception, new Regex("update failed"));
        }

        static int Count(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        [UnityTest]
        public IEnumerator ExceptionThrownInUpdate_ProducesOneAggregatedEvent_WithBreadcrumbs()
        {
            client.Breadcrumbs.Add("test", "player tapped play");
            ExpectThrows(5);
            var go = new GameObject("thrower");
            go.AddComponent<ThrowInUpdate>();

            for (int i = 0; i < 10; i++) yield return null;   // 5 throws + time for the log queue to drain
            client.Flush();
            for (int i = 0; i < 3; i++) yield return null;

            Assert.AreEqual(1, fake.RequestCount);
            string body = fake.SentBodies[0];
            Assert.AreEqual(1, Count(body, "\"log_type\":\"Exception\""), "body: " + body.Replace("{", "(").Replace("}", ")"));
            StringAssert.Contains("\"repeat_count\":5", body);
            StringAssert.Contains("\"fingerprint\":\"", body);
            StringAssert.Contains("player tapped play", body);        // breadcrumbs attached
            StringAssert.Contains("InvalidOperationException", body);

            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator FlushOnException_SendsWithoutWaitingForTheInterval()
        {
            settings.flushOnException = true;
            ExpectThrows(1);
            var go = new GameObject("thrower");
            go.AddComponent<ThrowInUpdate>().Remaining = 1;

            for (int i = 0; i < 10; i++) yield return null;
            Assert.GreaterOrEqual(fake.RequestCount, 1);
            StringAssert.Contains("\"log_type\":\"Exception\"", fake.SentBodies[0]);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator LoggedError_IsCapturedThroughTheUnityLogCallback()
        {
            LogAssert.Expect(LogType.Error, "captured error 1");
            Debug.LogError("captured error 1");
            for (int i = 0; i < 5; i++) yield return null;
            client.Flush();
            for (int i = 0; i < 3; i++) yield return null;

            Assert.AreEqual(1, fake.RequestCount);
            StringAssert.Contains("captured error", fake.SentBodies[0]);
        }

        [UnityTest]
        public IEnumerator Pause_FlushesImmediately()
        {
            client.TrackEvent("before_pause", null);
            client.OnPause(true);
            yield return null;
            yield return null;

            Assert.GreaterOrEqual(fake.RequestCount, 1);
            StringAssert.Contains("before_pause", fake.SentBodies[0]);
        }

        [UnityTest]
        public IEnumerator FailedSend_IsRequeued_AndDeliveredOnceTheServerRecovers()
        {
            fake.NextOutcome = TransportOutcome.Retry;
            client.TrackEvent("important", null);
            client.Flush();
            yield return null;
            yield return null;

            Assert.AreEqual(1, fake.RequestCount);
            Assert.AreEqual(1, client.Queue.Count, "failed batch is back in the queue");

            fake.NextOutcome = TransportOutcome.Success;
            yield return new WaitForSecondsRealtime(2.6f);     // first backoff is 1.6-2.4 s
            yield return null;

            Assert.AreEqual(2, fake.RequestCount);
            Assert.AreEqual(0, client.Queue.Count);
            StringAssert.Contains("important", fake.SentBodies[1]);
        }

        [Test]
        public void Bootstrap_IsASingleton()
        {
            TelemetryClient again = PlaytestTelemetry.StartClient(settings, fake, dir, true);
            Assert.AreSame(client, again);
            #pragma warning disable CS0618 // FindObjectsOfType is deprecated in Unity 6 but FindObjectsByType does not exist in early 2021.3
            Assert.AreEqual(1, UnityEngine.Object.FindObjectsOfType<TelemetryRunner>().Length);
#pragma warning restore CS0618
        }
    }
}
