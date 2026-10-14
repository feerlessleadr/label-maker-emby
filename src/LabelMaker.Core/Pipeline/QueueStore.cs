using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LabelMaker.Core.Pipeline
{
    /// <summary>Where an album is in its life. Only <see cref="Done"/> and <see cref="Skipped"/> are final.</summary>
    public static class QueueState
    {
        /// <summary>Seen, but not yet finished downloading, or waiting its turn (or a retry after MusicBrainz was busy).</summary>
        public const string Waiting = "Waiting";

        public const string Matching = "Matching";

        /// <summary>A likely match exists but a person should look. Nothing was touched.</summary>
        public const string NeedsReview = "NeedsReview";

        /// <summary>Nothing on MusicBrainz fits. Nothing was touched; the album stays in the inbox.</summary>
        public const string NoMatch = "NoMatch";

        /// <summary>Dry run: it would have been applied automatically. Nothing was touched.</summary>
        public const string Ready = "Ready";

        public const string Applying = "Applying";
        public const string Done = "Done";
        public const string Failed = "Failed";
        public const string Skipped = "Skipped";
        public const string Duplicate = "Duplicate";
    }

    public sealed class CandidateSummary
    {
        public string ReleaseId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public double Score { get; set; }
        public int WeakFiles { get; set; }
    }

    /// <summary>What the matcher said, kept with the queue item so the review screen does not have to match again.</summary>
    public sealed class MatchSummary
    {
        public string Outcome { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public double Score { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string BestReleaseId { get; set; } = string.Empty;
        public string BestTitle { get; set; } = string.Empty;
        public string BestArtist { get; set; } = string.Empty;
        public List<string> Notes { get; set; } = new List<string>();
        public List<CandidateSummary> Candidates { get; set; } = new List<CandidateSummary>();
    }

    public sealed class QueueItem
    {
        public string Id { get; set; } = string.Empty;
        public string LaneId { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string RelativeFolder { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public string State { get; set; } = QueueState.Waiting;

        /// <summary>One plain sentence saying why the album is in this state.</summary>
        public string Reason { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;
        public DateTime FirstSeenUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        /// <summary>Not tried again before this time (a back-off after MusicBrainz was busy or unreachable).</summary>
        public DateTime NextAttemptUtc { get; set; }

        public int Attempts { get; set; }

        /// <summary>Grows with every change, so a stale action from the review screen can be refused.</summary>
        public int Version { get; set; }

        public MatchSummary? Match { get; set; }

        /// <summary>Dry run and done: the library folder the album went (or would go) to.</summary>
        /// <summary>What the files themselves say (most common tag values), for the review list and alerts. Empty until the album was looked at.</summary>
        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        public string Destination { get; set; } = string.Empty;

        /// <summary>True after a person decided something (skipped it, undid it, discarded it): the album is left alone, even if its files change, until someone chooses "check again".</summary>
        public bool Held { get; set; }

        /// <summary>After "discard": the folder under the inbox's _Trash that holds the files, with a restore.json that lists where each one came from.</summary>
        public string TrashPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// The queue of albums, kept in one JSON file that is replaced atomically on every change, so a restart picks up where it was and a crash
    /// never leaves half a file. A file that cannot be read is set aside as <c>.corrupt</c> and the queue starts empty (the inbox is
    /// scanned again, so nothing is lost).
    /// </summary>
    public sealed class QueueStore
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        private readonly string _path;
        private readonly object _lock = new object();
        private readonly Dictionary<string, QueueItem> _items = new Dictionary<string, QueueItem>(StringComparer.Ordinal);

        public QueueStore(string path)
        {
            _path = path;
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            Load();
        }

        public IReadOnlyList<QueueItem> All()
        {
            lock (_lock) return _items.Values.OrderBy(i => i.FirstSeenUtc).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        }

        public QueueItem? Get(string id)
        {
            lock (_lock) return _items.TryGetValue(id, out var item) ? item : null;
        }

        /// <summary>Adds or replaces an item and saves the file. The version goes up by one.</summary>
        public void Save(QueueItem item)
        {
            lock (_lock)
            {
                item.Version++;
                _items[item.Id] = item;
                Write();
            }
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                var items = JsonSerializer.Deserialize<List<QueueItem>>(File.ReadAllText(_path), Json);
                if (items == null) return;
                foreach (var item in items.Where(i => !string.IsNullOrEmpty(i.Id))) _items[item.Id] = item;
            }
            catch (JsonException)
            {
                var aside = _path + ".corrupt";
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(_path, aside);
            }
        }

        private void Write()
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items.Values.OrderBy(i => i.FirstSeenUtc).ToList(), Json));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temp, _path);
        }
    }
}
