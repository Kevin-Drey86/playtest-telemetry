using System.Collections.Generic;

namespace LetiArts.PlaytestTelemetry
{
    internal static class ItemKind
    {
        public const char Event = 'E';
        public const char Perf = 'P';
        public const char Bug = 'B';
    }

    /// <summary>Drop priorities. When a queue is full the lowest priority (then oldest) is dropped first.</summary>
    internal static class ItemPriority
    {
        public const int Info = 0;       // info logs and custom/system events
        public const int Warning = 1;
        public const int Perf = 2;
        public const int Bug = 3;
        public const int Error = 4;
        public const int Exception = 5;
        public const int Count = 6;
    }

    /// <summary>One serialized item waiting to be sent. The JSON is built once, at enqueue time.</summary>
    internal sealed class QueueItem
    {
        public readonly char Kind;
        public readonly int Priority;
        public readonly string Json;

        public QueueItem(char kind, int priority, string json)
        {
            Kind = kind;
            Priority = priority;
            Json = json;
        }

        /// <summary>Approximate wire size in bytes.</summary>
        public int Size => Json.Length;
    }

    /// <summary>
    /// Bounded in-memory queue with priority-based overflow:
    /// Exception > Error > bug report > perf sample > Warning > Info/custom events.
    /// Main thread only.
    /// </summary>
    internal sealed class TelemetryQueue
    {
        readonly LinkedList<QueueItem>[] lanes = new LinkedList<QueueItem>[ItemPriority.Count];
        int count;
        long dropped;

        public int MaxItems;

        public TelemetryQueue(int maxItems)
        {
            MaxItems = maxItems;
            for (int i = 0; i < lanes.Length; i++) lanes[i] = new LinkedList<QueueItem>();
        }

        public int Count => count;

        /// <summary>Returns and resets the number of items dropped since the last call.</summary>
        public long TakeDropped()
        {
            long d = dropped;
            dropped = 0;
            return d;
        }

        /// <summary>Adds an item. Returns false if the item itself was dropped because the queue is full of more important data.</summary>
        public bool Enqueue(QueueItem item)
        {
            if (count >= MaxItems)
            {
                int lowest = LowestNonEmptyLane();
                if (item.Priority < lowest)
                {
                    dropped++;
                    return false;
                }
                lanes[lowest].RemoveFirst();
                count--;
                dropped++;
            }
            lanes[item.Priority].AddLast(item);
            count++;
            return true;
        }

        int LowestNonEmptyLane()
        {
            for (int i = 0; i < lanes.Length; i++)
            {
                if (lanes[i].Count > 0) return i;
            }
            return lanes.Length;
        }

        /// <summary>
        /// Removes up to maxItems/maxBytes of the most important, oldest items into <paramref name="into"/>.
        /// At least one item is always returned if the queue is not empty. Returns true if a limit stopped
        /// the batch (more data is waiting).
        /// </summary>
        public bool TakeBatch(int maxBytes, int maxItems, List<QueueItem> into)
        {
            int bytes = 0;
            for (int p = lanes.Length - 1; p >= 0; p--)
            {
                var lane = lanes[p];
                while (lane.Count > 0)
                {
                    QueueItem first = lane.First.Value;
                    if (into.Count > 0 && (into.Count >= maxItems || bytes + first.Size > maxBytes)) return true;
                    lane.RemoveFirst();
                    count--;
                    into.Add(first);
                    bytes += first.Size;
                }
            }
            return false;
        }

        /// <summary>Puts a failed batch back at the front of its lanes (original order preserved).</summary>
        public void Restore(List<QueueItem> items)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                lanes[items[i].Priority].AddFirst(items[i]);
                count++;
            }
            // New items may have arrived while the batch was in flight: re-apply the cap.
            while (count > MaxItems)
            {
                lanes[LowestNonEmptyLane()].RemoveFirst();
                count--;
                dropped++;
            }
        }

        /// <summary>Removes everything (most important first).</summary>
        public void DrainAll(List<QueueItem> into)
        {
            for (int p = lanes.Length - 1; p >= 0; p--)
            {
                foreach (QueueItem item in lanes[p]) into.Add(item);
                lanes[p].Clear();
            }
            count = 0;
        }

        public void Clear()
        {
            for (int i = 0; i < lanes.Length; i++) lanes[i].Clear();
            count = 0;
        }
    }
}
