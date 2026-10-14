using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LabelMaker.Core.Pipeline;

namespace LabelMaker.Core.Notifications
{
    /// <summary>What the aggregator remembers between runs (saved, so a restart does not repeat alerts).</summary>
    public sealed class AggregatorState
    {
        /// <summary>Albums that needed review and have already been counted in an event. An album that leaves review and comes back is new again.</summary>
        public HashSet<string> NeedsReviewCounted { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        public DateTime LastNeedsReviewUtc { get; set; } = DateTime.MinValue;

        /// <summary>Announced lane problems ("laneId|kind") and when each was last announced.</summary>
        public Dictionary<string, DateTime> LaneProblems { get; set; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public bool TroubleAnnounced { get; set; }

        public HashSet<string> AppliedAnnounced { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> WouldApplyAnnounced { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> FailedAnnounced { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Everything the aggregator looks at in one run.</summary>
    public sealed class AggregatorInput
    {
        public DateTime Now { get; set; }
        public IReadOnlyList<QueueItem> Items { get; set; } = Array.Empty<QueueItem>();
        public IReadOnlyDictionary<string, string> LaneNames { get; set; } = new Dictionary<string, string>();
        public bool Paused { get; set; }
        public bool DryRun { get; set; }
        public IReadOnlyList<LaneProblemInfo> LaneProblems { get; set; } = Array.Empty<LaneProblemInfo>();

        /// <summary>When MusicBrainz started failing (and has not answered since), or null while it works.</summary>
        public DateTime? ServiceTroubleSince { get; set; }

        /// <summary>The pass that just ran, when the owner started it ("Check inbox now"); null for a timed pass.</summary>
        public PassResult? ManualPass { get; set; }

        public NotificationSettings Settings { get; set; } = new NotificationSettings();
    }

    /// <summary>
    /// Turns the state of the queue and the lanes into the few events worth a phone alert (docs/NOTIFICATIONS.md section 4): nothing floods the
    /// phone (a first big pass is one summary, an album already counted is not announced again until its state changes, a lane problem
    /// repeats at most once a day), counts always say how many need review now, and while the plugin is paused only lane problems are sent. No
    /// event ever carries a file path or a share name. A switch that is off keeps the bookkeeping (so switching it on later does not flood)
    /// but sends nothing.
    /// </summary>
    public sealed class EventAggregator
    {
        public static readonly TimeSpan NeedsReviewSpacing = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan LaneProblemRepeat = TimeSpan.FromHours(24);
        public static readonly TimeSpan ServiceTroubleAfter = TimeSpan.FromMinutes(15);
        public const int MaxAlbumsListed = 5;
        public const int MaxFailuresPerRun = 3;

        private static readonly Regex DrivePath = new Regex(@"[A-Za-z]:[\\/][^\s,;)]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SharePath = new Regex(@"\\\\[^\s,;)]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly AggregatorState _state;

        public EventAggregator(AggregatorState state)
        {
            _state = state;
        }

        public AggregatorState State => _state;

        public List<PendingEvent> Evaluate(AggregatorInput input)
        {
            var events = new List<PendingEvent>();
            void Send(string name, Dictionary<string, object?> fields)
            {
                if (input.Settings.IsOn(name)) events.Add(new PendingEvent(name, fields));
            }

            // Lane problems are reported even while paused.
            var current = input.LaneProblems.GroupBy(p => p.LaneId + "|" + p.Kind).Select(g => g.First()).ToList();
            foreach (var problem in current)
            {
                var key = problem.LaneId + "|" + problem.Kind;
                if (!_state.LaneProblems.TryGetValue(key, out var last) || input.Now - last >= LaneProblemRepeat)
                {
                    _state.LaneProblems[key] = input.Now;
                    Send(EventNames.LaneProblem, new Dictionary<string, object?>
                    {
                        ["LaneId"] = problem.LaneId, ["LaneName"] = problem.LaneName, ["Kind"] = problem.Kind, ["Message"] = StripPaths(problem.Message),
                    });
                }
            }
            var currentKeys = new HashSet<string>(current.Select(p => p.LaneId + "|" + p.Kind), StringComparer.Ordinal);
            foreach (var key in _state.LaneProblems.Keys.Where(k => !currentKeys.Contains(k)).ToList())
            {
                _state.LaneProblems.Remove(key);
                var parts = key.Split('|');
                input.LaneNames.TryGetValue(parts[0], out var laneName);
                Send(EventNames.LaneRecovered, new Dictionary<string, object?> { ["LaneId"] = parts[0], ["LaneName"] = laneName ?? parts[0], ["Kind"] = parts.Length > 1 ? parts[1] : string.Empty });
            }

            // Everything below is bookkeeping about albums, kept even while paused so nothing is announced twice later, but sent only when running.
            var items = input.Items;
            string LaneName(QueueItem i) => input.LaneNames.TryGetValue(i.LaneId, out var n) ? n : i.LaneId;

            // Albums waiting for a person.
            var needs = items.Where(i => i.State == QueueState.NeedsReview || i.State == QueueState.NoMatch).ToList();
            _state.NeedsReviewCounted.IntersectWith(needs.Select(i => i.Id));
            var fresh = needs.Where(i => !_state.NeedsReviewCounted.Contains(i.Id)).OrderBy(i => i.FirstSeenUtc).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
            if (fresh.Count > 0 && input.Now - _state.LastNeedsReviewUtc >= NeedsReviewSpacing)
            {
                foreach (var item in fresh) _state.NeedsReviewCounted.Add(item.Id);
                _state.LastNeedsReviewUtc = input.Now;
                if (!input.Paused)
                {
                    Send(EventNames.NeedsReview, new Dictionary<string, object?>
                    {
                        ["Count"] = needs.Count,
                        ["NoMatchCount"] = needs.Count(i => i.State == QueueState.NoMatch),
                        ["New"] = fresh.Count,
                        ["Lanes"] = needs.GroupBy(LaneName).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(g => (object)new Dictionary<string, object?> { ["LaneId"] = g.First().LaneId, ["LaneName"] = g.Key, ["Count"] = g.Count() }).ToList(),
                        ["Albums"] = fresh.Take(MaxAlbumsListed).Select(i => (object)new Dictionary<string, object?>
                        {
                            ["Id"] = i.Id, ["Artist"] = ArtistOf(i), ["Album"] = AlbumOf(i), ["LaneId"] = i.LaneId, ["Reason"] = StripPaths(i.Reason),
                        }).ToList(),
                    });
                }
            }

            // Albums filed by Label Maker itself (a person's choice on the review screen is not announced).
            var filed = items.Where(i => i.State == QueueState.Done && !(i.Match != null && i.Match.Outcome == "ChosenByPerson")).ToList();
            var newlyFiled = filed.Where(i => _state.AppliedAnnounced.Add(i.Id)).OrderBy(i => i.FirstSeenUtc).ToList();
            _state.AppliedAnnounced.IntersectWith(filed.Select(i => i.Id).Concat(newlyFiled.Select(i => i.Id)));
            if (newlyFiled.Count > 0 && !input.Paused) Send(EventNames.AutoApplied, AppliedFields("Applied", newlyFiled));

            var ready = items.Where(i => i.State == QueueState.Ready).ToList();
            var newlyReady = ready.Where(i => _state.WouldApplyAnnounced.Add(i.Id)).OrderBy(i => i.FirstSeenUtc).ToList();
            _state.WouldApplyAnnounced.IntersectWith(ready.Select(i => i.Id));
            if (newlyReady.Count > 0 && !input.Paused) Send(EventNames.AutoApplied, AppliedFields("WouldApply", newlyReady));

            // Albums that could not be handled (a few per run, so a pile of damaged files does not flood the phone).
            var failed = items.Where(i => i.State == QueueState.Failed).ToList();
            _state.FailedAnnounced.IntersectWith(failed.Select(i => i.Id));
            var toAnnounce = failed.Where(i => !_state.FailedAnnounced.Contains(i.Id)).OrderBy(i => i.FirstSeenUtc).ThenBy(i => i.Id, StringComparer.Ordinal).Take(MaxFailuresPerRun).ToList();
            foreach (var item in toAnnounce)
            {
                _state.FailedAnnounced.Add(item.Id);
                if (input.Paused) continue;
                Send(EventNames.AlbumFailed, new Dictionary<string, object?>
                {
                    ["Id"] = item.Id, ["Artist"] = ArtistOf(item), ["Album"] = AlbumOf(item), ["LaneId"] = item.LaneId,
                    ["Reason"] = StripPaths(item.Reason), ["Retryable"] = IsRetryable(item.Reason),
                });
            }

            // MusicBrainz trouble.
            var waiting = items.Count(i => i.State == QueueState.Waiting);
            if (input.ServiceTroubleSince.HasValue)
            {
                if (!_state.TroubleAnnounced && waiting > 0 && input.Now - input.ServiceTroubleSince.Value >= ServiceTroubleAfter)
                {
                    _state.TroubleAnnounced = true;
                    if (!input.Paused)
                    {
                        Send(EventNames.ServiceTrouble, new Dictionary<string, object?>
                        {
                            ["SinceUtc"] = input.ServiceTroubleSince.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["WaitingCount"] = waiting,
                        });
                    }
                }
            }
            else if (_state.TroubleAnnounced)
            {
                _state.TroubleAnnounced = false;
                if (!input.Paused) Send(EventNames.ServiceRecovered, new Dictionary<string, object?>());
            }

            // The end of a pass the owner started.
            if (input.ManualPass != null && !input.Paused)
            {
                Send(EventNames.PassFinished, new Dictionary<string, object?>
                {
                    ["Found"] = input.ManualPass.Found, ["Queued"] = input.ManualPass.Queued, ["Processed"] = input.ManualPass.Processed,
                    ["NeedsReview"] = needs.Count, ["Failed"] = failed.Count,
                });
            }
            return events;
        }

        private Dictionary<string, object?> AppliedFields(string mode, List<QueueItem> albums) => new Dictionary<string, object?>
        {
            ["Mode"] = mode,
            ["Count"] = albums.Count,
            ["Albums"] = albums.Take(MaxAlbumsListed).Select(i => (object)new Dictionary<string, object?>
            {
                ["Id"] = i.Id, ["Artist"] = ArtistOf(i), ["Album"] = AlbumOf(i), ["LaneId"] = i.LaneId, ["Files"] = i.FileCount,
            }).ToList(),
        };

        /// <summary>The artist to show: what the files say, else what the best match says, else nothing (never a folder path).</summary>
        internal static string ArtistOf(QueueItem item) =>
            item.Artist.Length > 0 ? item.Artist : item.Match != null ? item.Match.BestArtist : string.Empty;

        internal static string AlbumOf(QueueItem item)
        {
            if (item.Album.Length > 0) return item.Album;
            if (item.Match != null && item.Match.BestTitle.Length > 0) return item.Match.BestTitle;
            var folder = (item.RelativeFolder ?? string.Empty).TrimEnd('\\', '/');
            var cut = folder.LastIndexOfAny(new[] { '\\', '/' });
            return cut >= 0 ? folder.Substring(cut + 1) : folder;
        }

        /// <summary>A failure the person can fix and have checked again (a damaged file, an unreachable library), as opposed to one that needs a decision.</summary>
        internal static bool IsRetryable(string reason) =>
            reason.IndexOf("could not be read", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("library folder", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("checked again", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Takes any drive path or network path out of a sentence. Events never carry them.</summary>
        public static string StripPaths(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return SharePath.Replace(DrivePath.Replace(text, "(a folder)"), "(a folder)");
        }
    }
}
