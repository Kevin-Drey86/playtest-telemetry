using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace LetiArts.PlaytestTelemetry.Tests
{
    public class QueueTests
    {
        static QueueItem Item(int priority, string tag = null, char kind = ItemKind.Event)
        {
            return new QueueItem(kind, priority, "{\"t\":\"" + (tag ?? priority.ToString()) + "\"}");
        }

        // ---- memory queue overflow: Exception > Error > bug report > perf sample > Warning

        [Test]
        public void Overflow_DropsOldestOfLowestPriority()
        {
            var q = new TelemetryQueue(3);
            q.Enqueue(Item(ItemPriority.Warning, "w1"));
            q.Enqueue(Item(ItemPriority.Warning, "w2"));
            q.Enqueue(Item(ItemPriority.Error, "e1"));
            Assert.IsTrue(q.Enqueue(Item(ItemPriority.Exception, "x1")));

            var all = new List<QueueItem>();
            q.DrainAll(all);
            Assert.AreEqual(3, all.Count);
            CollectionAssert.AreEqual(new[] { "x1", "e1", "w2" }, all.ConvertAll(i => i.Json.Substring(6, 2)));
            Assert.AreEqual(1, q.TakeDropped());
        }

        [Test]
        public void Overflow_NewLowPriorityItemIsDroppedWhenQueueHoldsMoreImportantData()
        {
            var q = new TelemetryQueue(2);
            q.Enqueue(Item(ItemPriority.Error));
            q.Enqueue(Item(ItemPriority.Exception));
            Assert.IsFalse(q.Enqueue(Item(ItemPriority.Warning)));
            Assert.IsFalse(q.Enqueue(Item(ItemPriority.Perf)));
            Assert.IsFalse(q.Enqueue(Item(ItemPriority.Bug)));
            Assert.AreEqual(2, q.Count);
            Assert.AreEqual(3, q.TakeDropped());
        }

        [Test]
        public void Overflow_FullPriorityOrder()
        {
            // Fill with one item of each priority, then push an Exception: Info goes first, then Warning, Perf, Bug, Error.
            var q = new TelemetryQueue(6);
            for (int p = 0; p < ItemPriority.Count; p++) q.Enqueue(Item(p));

            var dropOrder = new List<int>();
            for (int i = 0; i < 5; i++)
            {
                var before = new List<QueueItem>();
                q.DrainAll(before);
                foreach (var item in before) q.Enqueue(item);

                q.Enqueue(Item(ItemPriority.Exception, "n" + i));
                var after = new List<QueueItem>();
                q.DrainAll(after);
                foreach (var item in after) q.Enqueue(item);

                for (int p = 0; p < ItemPriority.Count; p++)
                {
                    bool had = before.Exists(x => x.Priority == p);
                    bool has = after.Exists(x => x.Priority == p);
                    if (had && !has) dropOrder.Add(p);
                }
            }
            CollectionAssert.AreEqual(
                new[] { ItemPriority.Info, ItemPriority.Warning, ItemPriority.Perf, ItemPriority.Bug, ItemPriority.Error },
                dropOrder);
        }

        [Test]
        public void TakeBatch_MostImportantFirst_AndRespectsLimits()
        {
            var q = new TelemetryQueue(100);
            q.Enqueue(Item(ItemPriority.Warning, "w"));
            q.Enqueue(Item(ItemPriority.Exception, "x"));
            q.Enqueue(Item(ItemPriority.Error, "e"));

            var batch = new List<QueueItem>();
            bool more = q.TakeBatch(int.MaxValue, 2, batch);
            Assert.IsTrue(more);
            Assert.AreEqual(2, batch.Count);
            Assert.AreEqual(ItemPriority.Exception, batch[0].Priority);
            Assert.AreEqual(ItemPriority.Error, batch[1].Priority);
            Assert.AreEqual(1, q.Count);
        }

        [Test]
        public void TakeBatch_AlwaysReturnsAtLeastOneItem_EvenIfLargerThanLimit()
        {
            var q = new TelemetryQueue(10);
            q.Enqueue(new QueueItem(ItemKind.Bug, ItemPriority.Bug, new string('x', 5000)));
            var batch = new List<QueueItem>();
            q.TakeBatch(100, 10, batch);
            Assert.AreEqual(1, batch.Count);
        }

        [Test]
        public void Restore_PutsBatchBackInFront_AndNeverGrowsPastTheCap()
        {
            var q = new TelemetryQueue(3);
            q.Enqueue(Item(ItemPriority.Error, "e1"));
            q.Enqueue(Item(ItemPriority.Error, "e2"));
            var batch = new List<QueueItem>();
            q.TakeBatch(int.MaxValue, 10, batch);
            Assert.AreEqual(0, q.Count);

            q.Enqueue(Item(ItemPriority.Error, "e3"));
            q.Enqueue(Item(ItemPriority.Error, "e4"));
            q.Restore(batch);   // 4 items for a cap of 3
            Assert.AreEqual(3, q.Count);

            var all = new List<QueueItem>();
            q.DrainAll(all);
            Assert.AreEqual("e2", all[0].Json.Substring(6, 2)); // oldest ("e1") was dropped, restored ones stay in front
        }

        // ---- backoff schedule

        [Test]
        public void Backoff_Schedule_DoublesAndCapsAtFiveMinutes()
        {
            float[] expected = { 2, 4, 8, 16, 32, 64, 128, 256, 300, 300 };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], Backoff.Delay(i, 0.5f), 0.001f, "attempt " + i);
            }
        }

        [Test]
        public void Backoff_JitterStaysWithinTwentyPercent_AndNeverExceedsCap()
        {
            Assert.AreEqual(1.6f, Backoff.Delay(0, 0f), 0.001f);
            Assert.AreEqual(2.4f, Backoff.Delay(0, 1f), 0.001f);
            Assert.AreEqual(300f, Backoff.Delay(50, 1f), 0.001f);
        }

        // ---- disk queue

        string tempDir;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "pt-tests-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }

        [Test]
        public void Disk_WriteThenRead_RoundTrips()
        {
            var disk = new DiskQueue(tempDir, 1024 * 1024);
            var items = new List<QueueItem>
            {
                new QueueItem(ItemKind.Event, ItemPriority.Error, "{\"message\":\"line1\\nline2 \\\"q\\\" é 日本\"}"),
                new QueueItem(ItemKind.Perf, ItemPriority.Perf, "{\"fps_avg\":60}"),
                new QueueItem(ItemKind.Bug, ItemPriority.Bug, "{\"tester_note\":\"x\"}"),
            };
            string path = disk.WriteSnapshot("{\"id\":\"abc\"", items);
            Assert.IsNotNull(path);

            PendingUpload loaded = disk.Load(path);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("{\"id\":\"abc\"", loaded.Header);
            Assert.AreEqual(3, loaded.Items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                Assert.AreEqual(items[i].Kind, loaded.Items[i].Kind);
                Assert.AreEqual(items[i].Priority, loaded.Items[i].Priority);
                Assert.AreEqual(items[i].Json, loaded.Items[i].Json);
            }
            CollectionAssert.AreEqual(new[] { path }, disk.ListFiles());
        }

        [Test]
        public void Disk_Rewrite_KeepsOnlyUnsentItems()
        {
            var disk = new DiskQueue(tempDir, 1024 * 1024);
            string path = disk.WriteSnapshot("{\"id\":\"abc\"", new List<QueueItem> { Item(ItemPriority.Error, "a"), Item(ItemPriority.Error, "b") });
            PendingUpload u = disk.Load(path);
            u.Sent = 1;
            Assert.IsTrue(disk.Rewrite(u));
            PendingUpload again = disk.Load(path);
            Assert.AreEqual(1, again.Items.Count);
            StringAssert.Contains("b", again.Items[0].Json);
        }

        [Test]
        public void Disk_SizeCap_DropsWarningsThenPerfThenErrors_OldestFirst()
        {
            var items = new List<QueueItem>();
            string pad = new string('p', 200);
            for (int i = 0; i < 5; i++) items.Add(new QueueItem(ItemKind.Event, ItemPriority.Warning, "{\"w\":" + i + ",\"pad\":\"" + pad + "\"}"));
            for (int i = 0; i < 5; i++) items.Add(new QueueItem(ItemKind.Perf, ItemPriority.Perf, "{\"p\":" + i + ",\"pad\":\"" + pad + "\"}"));
            for (int i = 0; i < 5; i++) items.Add(new QueueItem(ItemKind.Event, ItemPriority.Error, "{\"e\":" + i + ",\"pad\":\"" + pad + "\"}"));

            // Each item is ~225 bytes; allow roughly 9 of the 15.
            var disk = new DiskQueue(tempDir, 9 * 225 + 100);
            string path = disk.WriteSnapshot("{\"id\":\"abc\"", items);
            int dropped = disk.EnforceCap();

            Assert.AreEqual(6, dropped);
            PendingUpload u = disk.Load(path);
            Assert.AreEqual(9, u.Items.Count);
            Assert.AreEqual(0, u.Items.FindAll(x => x.Priority == ItemPriority.Warning).Count, "all warnings dropped first");
            Assert.AreEqual(4, u.Items.FindAll(x => x.Priority == ItemPriority.Perf).Count, "then the OLDEST perf sample");
            Assert.IsFalse(u.Items.Exists(x => x.Json.StartsWith("{\"p\":0,")), "oldest perf sample is the one dropped");
            Assert.AreEqual(5, u.Items.FindAll(x => x.Priority == ItemPriority.Error).Count, "errors survive");
            Assert.LessOrEqual(disk.TotalBytes(), 9 * 225 + 100);
        }

        [Test]
        public void Disk_UnreadableFileIsIgnored()
        {
            var disk = new DiskQueue(tempDir, 1024);
            Directory.CreateDirectory(tempDir);
            string bad = Path.Combine(tempDir, "q-0000000000000000001-0000.jsonl");
            File.WriteAllText(bad, "garbage without header\n");
            Assert.IsNull(disk.Load(bad));
        }
    }
}
