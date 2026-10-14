using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Notifications;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Review;
using LabelMaker.Core.Tags;

namespace LabelMaker.Core.Engine
{
    /// <summary>What the engine needs from the plugin: where to keep its files, where the settings are, and a few callbacks.</summary>
    public sealed class EngineHost
    {
        public string DataFolder { get; set; } = string.Empty;
        public Func<EngineSettings> Settings { get; set; } = () => new EngineSettings();
        public IMusicBrainz MusicBrainz { get; set; } = null!;
        public IFileOps Ops { get; set; } = new RealFileOps();
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
        public string PluginVersion { get; set; } = "0.0.0.0";

        /// <summary>Called with a library folder that just received (or lost) an album, so Emby can be told to look at it.</summary>
        public Action<string>? RefreshFolder { get; set; }

        /// <summary>Called with every event that was logged, to send it to the phones. An exception here never stops the engine.</summary>
        public Action<NotificationEvent>? OnEvent { get; set; }

        public Action<string>? Log { get; set; }
    }

    public sealed class PassSummary
    {
        public bool Ran { get; set; }
        public string Message { get; set; } = string.Empty;
        public PassResult? Result { get; set; }
        public int EventsSent { get; set; }
    }

    /// <summary>
    /// Everything Label Maker does, without Emby: the timed pass over the lanes (health check, worker, library refresh, events), the status, the
    /// album list and the review actions. The plugin shell only supplies settings, the clock, the push sender and the library refresh, and puts
    /// routes and a page on top. One gate keeps a pass and a review change from running at the same moment.
    /// </summary>
    public sealed class LabelMakerEngine
    {
        public const string MatchingVersion = "matching-2026-10-14";
        private static readonly TimeSpan HealthEvery = TimeSpan.FromMinutes(5);
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        private readonly EngineHost _host;
        private readonly MonitoredMusicBrainz _musicBrainz;
        private readonly QueueStore _store;
        private readonly JournalStore _journals;
        private readonly EventLog _events;
        private readonly AggregatorState _aggregatorState;
        private readonly EventAggregator _aggregator;
        private readonly string _statePath;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private Components? _components;
        private string _componentsKey = string.Empty;
        private List<Lane> _runnable = new List<Lane>();
        private List<LaneProblemInfo> _problems = new List<LaneProblemInfo>();
        private DateTime _healthAt = DateTime.MinValue;
        private string _healthKey = string.Empty;
        private DateTime? _lastPassUtc;
        private long _bump;

        private sealed class Components
        {
            public AlbumProcessor Processor = null!;
            public InboxWorker Worker = null!;
            public ReviewService Review = null!;
        }

        public LabelMakerEngine(EngineHost host)
        {
            _host = host;
            Directory.CreateDirectory(host.DataFolder);
            _musicBrainz = new MonitoredMusicBrainz(host.MusicBrainz, host.UtcNow);
            _store = new QueueStore(Path.Combine(host.DataFolder, "queue.json"));
            _journals = new JournalStore(Path.Combine(host.DataFolder, "journals"));
            _events = new EventLog(Path.Combine(host.DataFolder, "events.json"), host.UtcNow);
            _statePath = Path.Combine(host.DataFolder, "alerts-state.json");
            _aggregatorState = LoadState();
            _aggregator = new EventAggregator(_aggregatorState);
        }

        public QueueStore Queue => _store;
        public EventLog Events => _events;
        public DateTime? LastPassUtc => _lastPassUtc;

        /// <summary>When MusicBrainz started failing and has not answered since, or null.</summary>
        public DateTime? ServiceTroubleSince => _musicBrainz.TroubleSince;

        // ------------------------------------------------------------------ the timed pass

        /// <summary>
        /// One pass: check the lanes' health, work the inbox (unless paused or a lane is broken), tell Emby about albums that arrived in a
        /// library, and send the events that follow. <paramref name="manual"/> is true for "Check inbox now" (health is checked fresh and the end of
        /// the pass is reported).
        /// </summary>
        public async Task<PassSummary> RunPassAsync(bool manual, CancellationToken cancellation = default)
        {
            await _gate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var settings = _host.Settings();
                var now = _host.UtcNow();
                RefreshHealth(settings, now, force: manual);
                _runnable = settings.Lanes.Where(l => !_problems.Any(p => p.Blocking && p.LaneId == l.Id)).ToList();

                var summary = new PassSummary();
                var doneBefore = new HashSet<string>(_store.All().Where(i => i.State == QueueState.Done).Select(i => i.Id), StringComparer.Ordinal);
                if (settings.Paused)
                {
                    summary.Message = "Label Maker is paused, so the inbox was not checked.";
                }
                else if (_runnable.Count == 0)
                {
                    summary.Message = settings.Lanes.Count == 0 ? "There are no lanes yet. Add one in the plugin settings." : "Every lane has a problem, so nothing was checked.";
                }
                else
                {
                    summary.Result = await GetComponents(settings).Worker.RunPassAsync(cancellation).ConfigureAwait(false);
                    summary.Ran = true;
                    summary.Message = $"Checked {summary.Result.Found} album folder(s): {summary.Result.Queued} new, {summary.Result.Processed} looked at.";
                    _lastPassUtc = now;
                }

                var items = _store.All();
                foreach (var item in items.Where(i => i.State == QueueState.Done && !doneBefore.Contains(i.Id))) NoteFiled(item, settings);

                summary.EventsSent = Publish(settings, items, now, manual && summary.Ran ? summary.Result : null);
                Interlocked.Increment(ref _bump);
                return summary;
            }
            finally
            {
                _gate.Release();
            }
        }

        private int Publish(EngineSettings settings, IReadOnlyList<QueueItem> items, DateTime now, PassResult? manualPass)
        {
            var pending = _aggregator.Evaluate(new AggregatorInput
            {
                Now = now,
                Items = items,
                LaneNames = settings.Lanes.Where(l => !string.IsNullOrEmpty(l.Id)).GroupBy(l => l.Id).ToDictionary(g => g.Key, g => string.IsNullOrWhiteSpace(g.First().Name) ? g.Key : g.First().Name),
                Paused = settings.Paused,
                DryRun = settings.DryRun,
                LaneProblems = _problems,
                ServiceTroubleSince = _musicBrainz.TroubleSince,
                ManualPass = manualPass,
                Settings = settings.Notifications,
            });
            foreach (var p in pending)
            {
                var logged = _events.Append(p);
                try { _host.OnEvent?.Invoke(logged); }
                catch (Exception e) { _host.Log?.Invoke("A phone alert could not be sent: " + e.GetType().Name + " " + e.Message); }
            }
            SaveState();
            return pending.Count;
        }

        // ------------------------------------------------------------------ lane health

        /// <summary>The current problems of every lane (rule breaks and preflight findings), refreshed at most every five minutes unless forced.</summary>
        public IReadOnlyList<LaneProblemInfo> LaneProblems => _problems;

        private void RefreshHealth(EngineSettings settings, DateTime now, bool force)
        {
            var key = JsonSerializer.Serialize(settings.Lanes.Select(l => new { l.Id, l.Name, l.InboxPath, l.LibraryPath })) + settings.AccountName;
            if (!force && key == _healthKey && now - _healthAt < HealthEvery) return;

            var problems = new List<LaneProblemInfo>();
            string NameOf(Lane l) => string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name;
            var issues = LaneValidator.Validate(settings.Lanes);
            foreach (var issue in issues)
            {
                var lane = settings.Lanes.FirstOrDefault(l => l.Id == issue.LaneId);
                problems.Add(new LaneProblemInfo
                {
                    LaneId = issue.LaneId, LaneName = lane == null ? issue.LaneId : NameOf(lane),
                    Kind = issue.Code == "InboxIsLibrary" || issue.Code == "LibraryInsideInbox" ? LaneProblemKinds.InboxInsideLibrary : LaneProblemKinds.BadSettings,
                    Message = issue.Message, Blocking = true,
                });
            }
            var broken = new HashSet<string>(issues.Select(i => i.LaneId), StringComparer.Ordinal);
            foreach (var lane in settings.Lanes.Where(l => !broken.Contains(l.Id)))
            {
                var preflight = Preflight.Run(lane, _host.Ops, new PreflightOptions { AccountName = settings.AccountName });
                foreach (var check in preflight.Checks.Where(c => c.Level == PreflightLevel.Error || c.Level == PreflightLevel.Warning))
                {
                    var kind = check.Name switch
                    {
                        "Inbox found" => LaneProblemKinds.InboxUnreachable,
                        "Library found" => LaneProblemKinds.LibraryUnreachable,
                        "Inbox changes" or "Library changes" => LaneProblemKinds.NotWritable,
                        "Free space" => LaneProblemKinds.LowDiskSpace,
                        "Path length" => LaneProblemKinds.PathTooLong,
                        _ => null,
                    };
                    if (kind == null) continue;
                    if (problems.Any(p => p.LaneId == lane.Id && p.Kind == kind)) continue;
                    problems.Add(new LaneProblemInfo { LaneId = lane.Id, LaneName = NameOf(lane), Kind = kind, Message = check.Message, Blocking = check.Level == PreflightLevel.Error });
                }
            }
            _problems = problems;
            _healthAt = now;
            _healthKey = key;
        }

        /// <summary>The health checks of every lane in full (green and red), for the settings page. Always fresh.</summary>
        public object PreflightReport(EngineSettings settings)
        {
            var issues = LaneValidator.Validate(settings.Lanes);
            return new
            {
                Issues = issues.Select(i => new { i.LaneId, i.Code, i.Message }).ToArray(),
                Lanes = settings.Lanes.Where(l => !issues.Any(i => i.LaneId == l.Id)).Select(l =>
                {
                    var result = Preflight.Run(l, _host.Ops, new PreflightOptions { AccountName = settings.AccountName });
                    return new
                    {
                        LaneId = l.Id, LaneName = l.Name, result.CanRun,
                        Checks = result.Checks.Select(c => new { c.Name, Level = c.Level.ToString(), c.Message }).ToArray(),
                    };
                }).ToArray(),
            };
        }

        // ------------------------------------------------------------------ status, albums

        public Dictionary<string, object?> Info() => new Dictionary<string, object?>
        {
            ["PluginVersion"] = _host.PluginVersion,
            ["ApiLevel"] = EventNames.ApiLevel,
            ["Features"] = new List<string> { "events", "catalog", "status", "albums", "review" },
        };

        public Dictionary<string, object?> Status()
        {
            var settings = _host.Settings();
            var items = _store.All();
            var states = new[] { QueueState.Waiting, QueueState.NeedsReview, QueueState.NoMatch, QueueState.Ready, QueueState.Applying, QueueState.Done, QueueState.Failed, QueueState.Duplicate, QueueState.Skipped };
            Dictionary<string, int> Count(IEnumerable<QueueItem> set)
            {
                var counts = states.ToDictionary(s => s, s => 0);
                foreach (var item in set)
                {
                    var state = item.State == QueueState.Matching ? QueueState.Waiting : item.State;
                    if (counts.ContainsKey(state)) counts[state]++;
                }
                return counts;
            }
            var trouble = _musicBrainz.TroubleSince;
            return new Dictionary<string, object?>
            {
                ["ApiLevel"] = EventNames.ApiLevel,
                ["Version"] = items.Sum(i => (long)i.Version) + _events.LatestSeq + Interlocked.Read(ref _bump),
                ["ServerTimeUtc"] = _host.UtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["Paused"] = settings.Paused,
                ["DryRun"] = settings.DryRun,
                ["LastPassUtc"] = _lastPassUtc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["MatchingVersion"] = MatchingVersion,
                ["Counts"] = Count(items),
                ["Lanes"] = settings.Lanes.Select(l => new Dictionary<string, object?>
                {
                    ["LaneId"] = l.Id,
                    ["LaneName"] = l.Name,
                    ["Counts"] = Count(items.Where(i => i.LaneId == l.Id)),
                    ["Problems"] = _problems.Where(p => p.LaneId == l.Id).Select(p => new Dictionary<string, object?> { ["Kind"] = p.Kind, ["Message"] = EventAggregator.StripPaths(p.Message), ["Blocking"] = p.Blocking }).ToList(),
                }).ToList(),
                ["Service"] = new Dictionary<string, object?>
                {
                    ["MusicBrainz"] = trouble.HasValue ? "Trouble" : "Ok",
                    ["SinceUtc"] = trouble?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                },
                ["LatestSeq"] = _events.LatestSeq,
            };
        }

        /// <summary>The album list: filtered by state (comma separated), lane and text, newest change first.</summary>
        public Dictionary<string, object?> Albums(string? state, string? lane, string? text, int start, int limit)
        {
            var wanted = (state ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var settings = _host.Settings();
            IEnumerable<QueueItem> query = _store.All();
            if (wanted.Count > 0) query = query.Where(i => wanted.Contains(i.State));
            if (!string.IsNullOrEmpty(lane)) query = query.Where(i => i.LaneId == lane);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var t = text!.Trim();
                query = query.Where(i => i.Artist.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 || i.Album.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         i.RelativeFolder.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            var all = query.OrderByDescending(i => i.UpdatedUtc).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
            return new Dictionary<string, object?>
            {
                ["Total"] = all.Count,
                ["Start"] = Math.Max(0, start),
                ["Items"] = all.Skip(Math.Max(0, start)).Take(Math.Max(1, Math.Min(limit, 200))).Select(i => Summarise(i, settings)).ToList(),
            };
        }

        private Dictionary<string, object?> Summarise(QueueItem i, EngineSettings settings) => new Dictionary<string, object?>
        {
            ["Id"] = i.Id,
            ["LaneId"] = i.LaneId,
            ["LaneName"] = settings.Lanes.FirstOrDefault(l => l.Id == i.LaneId)?.Name ?? i.LaneId,
            ["Artist"] = EventAggregator.ArtistOf(i),
            ["Album"] = EventAggregator.AlbumOf(i),
            ["Folder"] = i.RelativeFolder,
            ["Kind"] = i.Kind,
            ["State"] = i.State,
            ["Reason"] = i.Reason,
            ["Files"] = i.FileCount,
            ["Held"] = i.Held,
            ["Discarded"] = !string.IsNullOrEmpty(i.TrashPath),
            ["Score"] = i.Match?.Score,
            ["BestReleaseId"] = i.Match?.BestReleaseId,
            ["BestTitle"] = i.Match?.BestTitle,
            ["BestArtist"] = i.Match?.BestArtist,
            ["Destination"] = i.Destination,
            ["FirstSeenUtc"] = i.FirstSeenUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["UpdatedUtc"] = i.UpdatedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        };

        /// <summary>One album with the match notes, candidates and (while it is still in the inbox) its files and their current tags.</summary>
        public Dictionary<string, object?>? Album(string id)
        {
            var item = _store.Get(id);
            if (item == null) return null;
            var settings = _host.Settings();
            var detail = Summarise(item, settings);
            detail["Source"] = item.Match?.Source;
            detail["Outcome"] = item.Match?.Outcome;
            detail["Notes"] = item.Match?.Notes ?? new List<string>();
            detail["Candidates"] = (item.Match?.Candidates ?? new List<CandidateSummary>()).Select(c => new Dictionary<string, object?>
            {
                ["ReleaseId"] = c.ReleaseId, ["Title"] = c.Title, ["Artist"] = c.Artist, ["Country"] = c.Country, ["Date"] = c.Date, ["Score"] = c.Score, ["WeakFiles"] = c.WeakFiles,
            }).ToList();

            var files = new List<object>();
            var lane = settings.Lanes.FirstOrDefault(l => l.Id == item.LaneId);
            if (lane != null && item.State != QueueState.Done && string.IsNullOrEmpty(item.TrashPath) && _host.Ops.DirectoryExists(lane.InboxPath))
            {
                var unit = new InboxScanner().Scan(lane, _host.Ops).FirstOrDefault(u => u.Id == id);
                if (unit != null)
                {
                    foreach (var path in unit.Files)
                    {
                        var entry = new Dictionary<string, object?> { ["File"] = Path.GetRelativePath(unit.Folder, path) };
                        try
                        {
                            var t = TrackTagFile.Read(path);
                            entry["Title"] = t.Title; entry["Artist"] = t.Artist; entry["Album"] = t.Album; entry["Track"] = t.TrackNumber; entry["Disc"] = t.DiscNumber; entry["Seconds"] = t.DurationMs.HasValue ? t.DurationMs.Value / 1000 : (int?)null;
                        }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException)
                        {
                            entry["Error"] = "This file could not be read.";
                        }
                        files.Add(entry);
                    }
                }
            }
            detail["FileList"] = files;
            return detail;
        }

        // ------------------------------------------------------------------ review actions

        public Task<ReviewPreview> PreviewAsync(string id, string releaseId, IReadOnlyList<MappingEntry>? mapping, CancellationToken ct = default) =>
            GetComponents(_host.Settings()).Review.PreviewAsync(id, releaseId, mapping, ct);

        public Task<ReviewCandidates> SearchAsync(string id, string artist, string album, CancellationToken ct = default) =>
            GetComponents(_host.Settings()).Review.SearchAsync(id, artist, album, ct);

        public Task<ReviewCandidates> ResolveAsync(string id, string pasted, CancellationToken ct = default) =>
            GetComponents(_host.Settings()).Review.ResolveReferenceAsync(id, pasted, ct);

        public Task<ReviewResult> ApplyAsync(string id, string releaseId, IReadOnlyList<MappingEntry>? mapping, CancellationToken ct = default) =>
            Change(id, r => r.ApplyAsync(id, releaseId, mapping, ct));

        public Task<ReviewResult> KeepMyTagsAsync(string id) => Change(id, r => Task.FromResult(r.KeepMyTags(id)));
        public Task<ReviewResult> SkipAsync(string id) => Change(id, r => Task.FromResult(r.Skip(id)));
        public Task<ReviewResult> RecheckAsync(string id) => Change(id, r => Task.FromResult(r.Recheck(id)));
        public Task<ReviewResult> DiscardAsync(string id) => Change(id, r => Task.FromResult(r.Discard(id)));
        public Task<ReviewResult> RestoreAsync(string id) => Change(id, r => Task.FromResult(r.RestoreDiscarded(id)));
        public Task<ReviewResult> UndoAsync(string id) => Change(id, r => Task.FromResult(r.Undo(id)));
        public Task<ReviewResult> SplitAsync(string id) => Change(id, r => Task.FromResult(r.SplitByAlbum(id)));

        /// <summary>Runs a change to an album under the gate (never at the same moment as a pass), then tells Emby about a newly filed album.</summary>
        private async Task<ReviewResult> Change(string id, Func<ReviewService, Task<ReviewResult>> action)
        {
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                return ReviewResult.Fail("Label Maker is busy checking the inbox right now. Try again in a minute.");
            }
            try
            {
                var settings = _host.Settings();
                var result = await action(GetComponents(settings).Review).ConfigureAwait(false);
                if (result.Success && result.State == QueueState.Done)
                {
                    var item = _store.Get(id);
                    if (item != null) NoteFiled(item, settings);
                }
                Interlocked.Increment(ref _bump);
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        private void NoteFiled(QueueItem item, EngineSettings settings)
        {
            var lane = settings.Lanes.FirstOrDefault(l => l.Id == item.LaneId);
            if (lane == null || string.IsNullOrEmpty(item.Destination)) return;
            try { _host.RefreshFolder?.Invoke(Path.Combine(lane.LibraryPath, item.Destination)); }
            catch (Exception e) { _host.Log?.Invoke("Emby could not be told about a new album: " + e.GetType().Name + " " + e.Message); }
        }

        // ------------------------------------------------------------------ parts

        private Components GetComponents(EngineSettings settings)
        {
            var key = settings.BuildSignature();
            if (_components != null && key == _componentsKey) return _components;
            var match = settings.ToMatchSettings();
            var planner = settings.ToPlannerOptions();
            var tags = settings.ToTagOptions();
            var processor = new AlbumProcessor(_musicBrainz, _host.Ops, _journals, match, planner, tags);
            _components = new Components
            {
                Processor = processor,
                Worker = new InboxWorker(() => _runnable, _host.Ops, _store, processor, settings.ToWorkerOptions(), _host.UtcNow),
                Review = new ReviewService(_musicBrainz, _host.Ops, _journals, _store, () => _host.Settings().Lanes, match, planner, tags, _host.UtcNow),
            };
            _componentsKey = key;
            return _components;
        }

        private AggregatorState LoadState()
        {
            try
            {
                if (File.Exists(_statePath)) return JsonSerializer.Deserialize<AggregatorState>(File.ReadAllText(_statePath), Json) ?? new AggregatorState();
            }
            catch (Exception e) when (e is JsonException || e is IOException)
            {
                // A damaged state file only means alerts may repeat once.
            }
            return new AggregatorState();
        }

        private void SaveState()
        {
            try
            {
                var temp = _statePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_aggregatorState, Json));
                if (File.Exists(_statePath)) File.Delete(_statePath);
                File.Move(temp, _statePath);
            }
            catch (IOException e)
            {
                _host.Log?.Invoke("The alert memory could not be saved: " + e.Message);
            }
        }
    }
}
