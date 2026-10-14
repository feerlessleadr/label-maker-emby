using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;

namespace LabelMaker.Core.Pipeline
{
    public sealed class WorkerOptions
    {
        /// <summary>How long an album's files must stay unchanged before it counts as finished downloading. Default 60 seconds.</summary>
        public TimeSpan StableFor { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>The most albums handled in one pass, so a drop of hundreds does not hold up everything else (they are taken oldest first).</summary>
        public int MaxPerPass { get; set; } = 20;

        /// <summary>True (the default) means nothing is changed: albums that would be applied show as "Ready".</summary>
        public bool DryRun { get; set; } = true;

        /// <summary>Waits before trying an album again after MusicBrainz was busy or unreachable: 1, 5, 15, then every 60 minutes.</summary>
        public IList<TimeSpan> RetryDelays { get; } = new List<TimeSpan> { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60) };
    }

    /// <summary>What one pass did, for the log and the status page.</summary>
    public sealed class PassResult
    {
        public int Found { get; set; }
        public int Queued { get; set; }
        public int Processed { get; set; }
        public int WaitingForRetry { get; set; }
        public int Gone { get; set; }
        public Dictionary<string, int> ByState { get; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// One pass of the inbox worker. Each pass looks at every lane's inbox again (file-system notifications are not trusted, see
    /// <see cref="StabilityTracker"/>), queues new albums, resets albums whose files changed, resumes anything cut off by a crash, and then
    /// handles the finished albums, oldest first, one at a time (MusicBrainz is asked politely, one request at a time). The Emby plugin
    /// calls <see cref="RunPassAsync"/> on a timer; tests call it with a clock they control.
    /// </summary>
    public sealed class InboxWorker
    {
        private readonly Func<IReadOnlyList<Lane>> _lanes;
        private readonly IFileOps _ops;
        private readonly QueueStore _store;
        private readonly AlbumProcessor _processor;
        private readonly WorkerOptions _options;
        private readonly Func<DateTime> _utcNow;
        private readonly InboxScanner _scanner = new InboxScanner();
        private readonly StabilityTracker _stability = new StabilityTracker();

        public InboxWorker(Func<IReadOnlyList<Lane>> lanes, IFileOps ops, QueueStore store, AlbumProcessor processor, WorkerOptions options, Func<DateTime>? utcNow = null)
        {
            _lanes = lanes;
            _ops = ops;
            _store = store;
            _processor = processor;
            _options = options;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public async Task<PassResult> RunPassAsync(CancellationToken cancellation)
        {
            var now = _utcNow();
            var result = new PassResult();
            var lanes = _lanes();
            var laneById = lanes.ToDictionary(l => l.Id, StringComparer.Ordinal);

            // 0. An album cut off in the middle of matching starts over; one cut off in the middle of applying is finished from its journal.
            foreach (var item in _store.All().Where(i => i.State == QueueState.Matching || i.State == QueueState.Applying).ToList())
            {
                if (item.State == QueueState.Matching) { item.State = QueueState.Waiting; Touch(item, now); continue; }
                var resumed = _processor.Resume(item.Id);
                Apply(item, resumed, now);
            }

            // 1. Look at every inbox again; queue what is new, restart what changed.
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var finished = new List<(QueueItem Item, AlbumUnit Unit, Lane Lane)>();
            foreach (var lane in lanes)
            {
                foreach (var unit in _scanner.Scan(lane, _ops))
                {
                    result.Found++;
                    seenIds.Add(unit.Id);
                    var item = _store.Get(unit.Id);
                    if (item == null)
                    {
                        item = new QueueItem { Id = unit.Id, LaneId = lane.Id, FirstSeenUtc = now };
                        Describe(item, unit, now);
                        _store.Save(item);
                        result.Queued++;
                    }
                    else if (item.Fingerprint != unit.Fingerprint && item.State != QueueState.Applying)
                    {
                        // New or changed files: whatever was decided no longer holds.
                        Describe(item, unit, now);
                        item.State = QueueState.Waiting;
                        item.Reason = string.Empty;
                        item.Match = null;
                        item.Attempts = 0;
                        item.NextAttemptUtc = DateTime.MinValue;
                        Touch(item, now);
                    }

                    bool isFinished = _stability.IsFinished(unit, now, _options.StableFor, _ops);
                    if (item.State == QueueState.Waiting && isFinished && item.NextAttemptUtc <= now) finished.Add((item, unit, lane));
                    else if (item.State == QueueState.Waiting && !isFinished && string.IsNullOrEmpty(item.Reason))
                    {
                        item.Reason = unit.HasTemporaryFiles ? "Still downloading." : "Waiting until the files stop changing.";
                        Touch(item, now);
                    }
                }
            }
            _stability.Keep(seenIds);

            // 2. Albums that are no longer in the inbox (moved or deleted by hand).
            foreach (var item in _store.All().Where(i => !seenIds.Contains(i.Id)).ToList())
            {
                if (item.State == QueueState.Done || item.State == QueueState.Skipped || item.State == QueueState.Applying) continue;
                if (laneById.TryGetValue(item.LaneId, out var lane) && !_ops.DirectoryExists(lane.InboxPath)) continue; // the whole inbox is unreachable: wait
                item.State = QueueState.Skipped;
                item.Reason = "The album is no longer in the inbox.";
                Touch(item, now);
                result.Gone++;
            }

            // 3. Handle the finished albums, oldest first.
            foreach (var (item, unit, lane) in finished.OrderBy(f => f.Item.FirstSeenUtc).Take(_options.MaxPerPass))
            {
                cancellation.ThrowIfCancellationRequested();
                item.State = QueueState.Matching;
                Touch(item, now);

                var outcome = await _processor.ProcessAsync(lane, unit, _options.DryRun, s =>
                {
                    item.State = s;
                    Touch(item, _utcNow());
                }, cancellation).ConfigureAwait(false);

                Apply(item, outcome, _utcNow());
                result.Processed++;
                if (outcome.Retry) result.WaitingForRetry++;
            }

            foreach (var item in _store.All()) result.ByState[item.State] = result.ByState.TryGetValue(item.State, out var n) ? n + 1 : 1;
            return result;
        }

        private void Apply(QueueItem item, ProcessOutcome outcome, DateTime now)
        {
            item.State = outcome.State;
            item.Reason = outcome.Reason;
            if (outcome.Match != null) item.Match = outcome.Match;
            if (outcome.Destination.Length > 0) item.Destination = outcome.Destination;
            if (outcome.Retry)
            {
                var delay = _options.RetryDelays[Math.Min(item.Attempts, _options.RetryDelays.Count - 1)];
                item.Attempts++;
                item.NextAttemptUtc = now + delay;
            }
            else
            {
                item.Attempts = 0;
                item.NextAttemptUtc = DateTime.MinValue;
            }
            Touch(item, now);
        }

        private static void Describe(QueueItem item, AlbumUnit unit, DateTime now)
        {
            item.Folder = unit.Folder;
            item.RelativeFolder = unit.RelativeFolder;
            item.Kind = unit.Kind;
            item.FileCount = unit.Files.Count;
            item.Fingerprint = unit.Fingerprint;
        }

        private void Touch(QueueItem item, DateTime now)
        {
            item.UpdatedUtc = now;
            _store.Save(item);
        }
    }
}
