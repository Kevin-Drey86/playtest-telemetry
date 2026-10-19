using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace LetiArts.PlaytestTelemetry.Tests
{
    /// <summary>
    /// Sends real HTTP requests to a deployed (or `wrangler dev`) Worker. Skipped unless both
    /// PT_TEST_ENDPOINT and PT_TEST_KEY are set in the environment of the Unity process, e.g.
    ///   PT_TEST_ENDPOINT=https://playtest-telemetry.example.workers.dev PT_TEST_KEY=pt_... Unity -runTests ...
    /// The test data is tagged with the tester alias "integration-test".
    /// </summary>
    [Category("Integration")]
    public class IntegrationTests
    {
        string dir;
        TelemetrySettings settings;
        TelemetryClient client;

        [SetUp]
        public void SetUp()
        {
            string endpoint = Environment.GetEnvironmentVariable("PT_TEST_ENDPOINT");
            string key = Environment.GetEnvironmentVariable("PT_TEST_KEY");
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
            {
                Assert.Ignore("Set PT_TEST_ENDPOINT and PT_TEST_KEY to run the integration test.");
            }

            dir = Path.Combine(Path.GetTempPath(), "pt-int-" + Guid.NewGuid().ToString("N"));
            settings = ScriptableObject.CreateInstance<TelemetrySettings>();
            settings.endpointUrl = endpoint;
            settings.apiKey = key;
            settings.deviceIdSalt = "integration";
            settings.flushIntervalSeconds = 3600f;
            settings.flushOnException = false;
            settings.perfSampleIntervalSeconds = 1f;   // produce a perf sample quickly
            settings.minLogLevel = TelemetryLogLevel.Warning;
            settings.internalLogging = true;
            client = PlaytestTelemetry.StartClient(settings, null, dir, true); // null => real HttpTransport
        }

        [TearDown]
        public void TearDown()
        {
            PlaytestTelemetry.ShutdownForTests();
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
            if (settings != null) UnityEngine.Object.Destroy(settings);
        }

        [UnityTest]
        public IEnumerator RealRequest_IsAcceptedWithoutDroppedItems()
        {
            client.SetTesterAlias("integration-test");
            client.SetContext("test", "integration");
            client.Breadcrumbs.Add("test", "integration test started");
            client.TrackEvent("pt_integration_event", new Dictionary<string, object> { { "n", 1 }, { "ok", true } });

            LogAssert.Expect(LogType.Error, "Integration error 42");
            Debug.LogError("Integration error 42");
            LogAssert.Expect(LogType.Warning, "Integration warning 7");
            Debug.LogWarning("Integration warning 7");

            // wait for at least one perf sample (1 s interval)
            float until = Time.realtimeSinceStartup + 2.5f;
            while (Time.realtimeSinceStartup < until) yield return null;

            client.BugReports.Report("integration bug report", false);
            client.Flush();

            until = Time.realtimeSinceStartup + 15f;
            while (client.SuccessfulRequests == 0 && Time.realtimeSinceStartup < until) yield return null;

            Assert.GreaterOrEqual(client.SuccessfulRequests, 1, "no successful request; last result: " + client.LastResult.Outcome + " HTTP " + client.LastResult.StatusCode + " " + client.LastResult.Detail);
            Assert.AreEqual(0, client.LastServerDropped, "the Worker dropped items it considered malformed: the client and Worker disagree on the wire format");
        }
    }
}
