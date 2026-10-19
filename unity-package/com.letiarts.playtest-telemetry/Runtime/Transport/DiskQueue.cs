using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LetiArts.PlaytestTelemetry
{
    /// <summary>A snapshot file loaded for upload. Items before <see cref="Sent"/> have already been accepted by the server.</summary>
    internal sealed class PendingUpload
    {
        public string Path;
        /// <summary>Session JSON without closing brace and without last_seen_at.</summary>
        public string Header;
        public List<QueueItem> Items = new List<QueueItem>();
        public int Sent;
        public long LastWriteMs;
    }

    /// <summary>
    /// Persists queue snapshots as JSON Lines under persistentDataPath/playtest-telemetry/.
    /// File layout: line 1 = "S" + session JSON prefix; every other line = kind char + priority digit + item JSON.
    /// Files are written to a temp name and renamed, so a kill mid-write never leaves a half file.
    /// All errors are swallowed (returned as false/0): disk problems must never hurt the game.
    /// </summary>
    internal sealed class DiskQueue
    {
        const string Extension = ".jsonl";
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        readonly string directory;
        readonly long maxBytes;
        long sequence;

        public DiskQueue(string directory, long maxBytes)
        {
            this.directory = directory;
            this.maxBytes = maxBytes;
        }

        public string Directory => directory;

        static string FormatItem(QueueItem item)
        {
            return item.Kind.ToString() + (char)('0' + item.Priority) + item.Json;
        }

        static string Serialize(string header, IList<QueueItem> items, int from)
        {
            var sb = new StringBuilder(1024);
            sb.Append('S').Append(header).Append('\n');
            for (int i = from; i < items.Count; i++) sb.Append(FormatItem(items[i])).Append('\n');
            return sb.ToString();
        }

        void WriteAtomic(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Utf8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Writes a new snapshot file. Returns its path, or null on failure.</summary>
        public string WriteSnapshot(string header, IList<QueueItem> items)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                string name = "q-" + DateTime.UtcNow.Ticks.ToString("D19") + "-" + (sequence++).ToString("D4") + Extension;
                string path = System.IO.Path.Combine(directory, name);
                WriteAtomic(path, Serialize(header, items, 0));
                return path;
            }
            catch (Exception e)
            {
                PtLog.Warn("disk write failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Snapshot files, oldest first.</summary>
        public List<string> ListFiles()
        {
            var files = new List<string>();
            try
            {
                if (!System.IO.Directory.Exists(directory)) return files;
                files.AddRange(System.IO.Directory.GetFiles(directory, "q-*" + Extension));
                files.Sort(StringComparer.Ordinal);
            }
            catch (Exception e)
            {
                PtLog.Warn("disk list failed: " + e.Message);
            }
            return files;
        }

        /// <summary>Reads a snapshot. Returns null if the file is unreadable or has no valid header.</summary>
        public PendingUpload Load(string path)
        {
            try
            {
                string[] lines = File.ReadAllLines(path, Utf8);
                if (lines.Length == 0 || lines[0].Length < 2 || lines[0][0] != 'S') return null;
                var upload = new PendingUpload
                {
                    Path = path,
                    Header = lines[0].Substring(1),
                    LastWriteMs = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(),
                };
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length < 3) continue;
                    char kind = line[0];
                    int priority = line[1] - '0';
                    if ((kind != ItemKind.Event && kind != ItemKind.Perf && kind != ItemKind.Bug) ||
                        priority < 0 || priority >= ItemPriority.Count) continue;
                    upload.Items.Add(new QueueItem(kind, priority, line.Substring(2)));
                }
                return upload;
            }
            catch (Exception e)
            {
                PtLog.Warn("disk read failed: " + e.Message);
                return null;
            }
        }

        /// <summary>Rewrites a file keeping only the items not yet sent.</summary>
        public bool Rewrite(PendingUpload upload)
        {
            try
            {
                WriteAtomic(upload.Path, Serialize(upload.Header, upload.Items, upload.Sent));
                return true;
            }
            catch (Exception e)
            {
                PtLog.Warn("disk rewrite failed: " + e.Message);
                return false;
            }
        }

        public void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                PtLog.Warn("disk delete failed: " + e.Message);
            }
        }

        public long TotalBytes()
        {
            long total = 0;
            foreach (string f in ListFiles())
            {
                try { total += new FileInfo(f).Length; } catch { /* ignore */ }
            }
            return total;
        }

        sealed class Entry
        {
            public int File;
            public int Index;
            public int Priority;
            public int Bytes;
        }

        /// <summary>
        /// Enforces the size cap across all snapshot files by dropping, in order: the oldest Info/Warning
        /// items, then the oldest perf samples, then bug reports, then errors, then exceptions.
        /// Returns the number of items dropped.
        /// </summary>
        public int EnforceCap()
        {
            try
            {
                if (TotalBytes() <= maxBytes) return 0;

                List<string> paths = ListFiles();
                var uploads = new List<PendingUpload>();
                var entries = new List<Entry>();
                long total = 0;
                for (int f = 0; f < paths.Count; f++)
                {
                    PendingUpload u = Load(paths[f]);
                    if (u == null)
                    {
                        Delete(paths[f]); // unreadable: cannot help
                        uploads.Add(null);
                        continue;
                    }
                    uploads.Add(u);
                    total += Utf8.GetByteCount(u.Header) + 2;
                    for (int i = 0; i < u.Items.Count; i++)
                    {
                        int bytes = Utf8.GetByteCount(u.Items[i].Json) + 3;
                        total += bytes;
                        entries.Add(new Entry { File = f, Index = i, Priority = u.Items[i].Priority, Bytes = bytes });
                    }
                }

                // Lowest priority first, then oldest (file order, then line order).
                entries.Sort((a, b) =>
                {
                    int c = a.Priority.CompareTo(b.Priority);
                    if (c != 0) return c;
                    c = a.File.CompareTo(b.File);
                    return c != 0 ? c : a.Index.CompareTo(b.Index);
                });

                var removed = new HashSet<long>();
                int dropped = 0;
                for (int k = 0; k < entries.Count && total > maxBytes; k++)
                {
                    Entry e = entries[k];
                    removed.Add(((long)e.File << 32) | (uint)e.Index);
                    total -= e.Bytes;
                    dropped++;
                }

                for (int f = 0; f < uploads.Count; f++)
                {
                    PendingUpload u = uploads[f];
                    if (u == null) continue;
                    var kept = new List<QueueItem>();
                    for (int i = 0; i < u.Items.Count; i++)
                    {
                        if (!removed.Contains(((long)f << 32) | (uint)i)) kept.Add(u.Items[i]);
                    }
                    if (kept.Count != u.Items.Count)
                    {
                        u.Items = kept;
                        Rewrite(u);
                    }
                }

                // Headers alone can still exceed the cap with a huge backlog of sessions: drop oldest files.
                if (total > maxBytes)
                {
                    foreach (string p in ListFiles())
                    {
                        if (TotalBytes() <= maxBytes) break;
                        Delete(p);
                    }
                }
                return dropped;
            }
            catch (Exception e)
            {
                PtLog.Warn("disk cap enforcement failed: " + e.Message);
                return 0;
            }
        }
    }
}
