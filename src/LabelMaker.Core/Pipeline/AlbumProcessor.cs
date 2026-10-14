using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Tags;

namespace LabelMaker.Core.Pipeline
{
    /// <summary>What happened to one album: the state it should be in, why, and what the matcher said.</summary>
    public sealed class ProcessOutcome
    {
        public string State { get; set; } = QueueState.Waiting;
        public string Reason { get; set; } = string.Empty;
        public MatchSummary? Match { get; set; }

        /// <summary>The library folder the album went to (or would go to in a dry run).</summary>
        public string Destination { get; set; } = string.Empty;

        /// <summary>True when MusicBrainz was busy or unreachable: the album waits and is tried again later, it is not "no match".</summary>
        public bool Retry { get; set; }

        /// <summary>The artist and album the files' tags name.</summary>
        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;
    }

    /// <summary>
    /// Takes one finished album from the inbox to its end: read the tags, match, and then either leave it for review, report what a dry run would
    /// do, or tag and move it. It never throws for an album's own problems (a damaged file, a missing library folder): those become a state
    /// with a plain reason, so one bad album cannot stop the queue.
    /// </summary>
    public sealed class AlbumProcessor
    {
        private readonly IMusicBrainz _musicBrainz;
        private readonly IFileOps _ops;
        private readonly JournalStore _journals;
        private readonly MatchSettings _settings;
        private readonly PlannerOptions _planner;
        private readonly TagWriteOptions _tagOptions;

        public AlbumProcessor(IMusicBrainz musicBrainz, IFileOps ops, JournalStore journals, MatchSettings settings, PlannerOptions planner, TagWriteOptions tagOptions)
        {
            _musicBrainz = musicBrainz;
            _ops = ops;
            _journals = journals;
            _settings = settings;
            _planner = planner;
            _tagOptions = tagOptions;
        }

        /// <param name="stage">Called with "Applying" just before files are changed, so the queue shows it (and a crash can be resumed).</param>
        public async Task<ProcessOutcome> ProcessAsync(Lane lane, AlbumUnit unit, bool dryRun, Action<string>? stage, CancellationToken cancellation)
        {
            var (artist, album) = TagSummary(unit);
            var outcome = await ProcessCoreAsync(lane, unit, dryRun, stage, cancellation).ConfigureAwait(false);
            outcome.Artist = artist;
            outcome.Album = album;
            return outcome;
        }

        /// <summary>The artist and album most of the first few files name (read before anything moves).</summary>
        private static (string Artist, string Album) TagSummary(AlbumUnit unit)
        {
            var tags = new List<TrackTags>();
            foreach (var path in unit.Files.Take(8))
            {
                try { tags.Add(TrackTagFile.Read(path)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException) { }
            }
            var artist = ReleaseVerifier.MostCommon(tags.Select(t => t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.Artist));
            return (artist, ReleaseVerifier.MostCommon(tags.Select(t => t.Album)));
        }

        private async Task<ProcessOutcome> ProcessCoreAsync(Lane lane, AlbumUnit unit, bool dryRun, Action<string>? stage, CancellationToken cancellation)
        {
            // 1. Read the tags.
            var files = new List<AlbumFile>();
            var unreadable = new List<string>();
            foreach (var path in unit.Files)
            {
                try
                {
                    files.Add(new AlbumFile(path, TrackTagFile.Read(path)));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException)
                {
                    unreadable.Add(Path.GetFileName(path));
                }
            }
            if (files.Count == 0) return Failed("None of the files in this folder could be read as music.");
            if (unreadable.Count > 0)
            {
                return Failed($"{unreadable.Count} file(s) could not be read ({string.Join(", ", unreadable.Take(3))}). Fix or remove them and the album is checked again.");
            }

            // 2. A folder that holds several different albums is not guessed at.
            if (unit.Kind == UnitKind.Album && DistinctAlbums(files) > 1)
            {
                return new ProcessOutcome
                {
                    State = QueueState.NeedsReview,
                    Reason = $"This folder holds {DistinctAlbums(files)} different albums by their tags. Use \"Split by album\" on the review screen to put each album in its own folder, or sort them yourself.",
                };
            }

            // 3. Match.
            MatchResult match;
            try
            {
                var info = unit.Kind == UnitKind.Album ? AlbumInfoReader.Read(Path.Combine(unit.Folder, ".albuminfo")) : null;
                match = await new AlbumMatcher(_musicBrainz, _settings).MatchAsync(files, info, cancellation).ConfigureAwait(false);
            }
            catch (MusicBrainzUnavailableException e)
            {
                return new ProcessOutcome { State = QueueState.Waiting, Retry = true, Reason = e.Message };
            }
            var summary = Summarise(match);

            if (match.Outcome == MatchOutcome.NoMatch) return new ProcessOutcome { State = QueueState.NoMatch, Reason = match.Reason, Match = summary };
            if (match.Outcome == MatchOutcome.NeedsReview) return new ProcessOutcome { State = QueueState.NeedsReview, Reason = match.Reason, Match = summary };

            // 4. A certain match: is it allowed to go ahead by itself?
            if (!lane.AutoApply)
            {
                return new ProcessOutcome { State = QueueState.NeedsReview, Reason = "This lane is set to always ask you first.", Match = summary };
            }
            if (!_ops.DirectoryExists(lane.LibraryPath))
            {
                return new ProcessOutcome { State = QueueState.Failed, Reason = "The library folder for this lane was not found or cannot be reached.", Match = summary };
            }

            var options = unit.Kind == UnitKind.Single ? CopyWithoutCover(_planner) : _planner;
            var plan = Planner.Plan(unit.Id, lane.InboxPath, unit.Folder, lane.LibraryPath, match.Best!, _ops, options);
            var destination = plan.Files.Count > 0 ? Path.GetRelativePath(lane.LibraryPath, Path.GetDirectoryName(plan.Files[0].Destination)!) : string.Empty;

            if (plan.IsDuplicate) return new ProcessOutcome { State = QueueState.Duplicate, Reason = string.Join(" ", plan.Problems), Match = summary, Destination = destination };
            if (!plan.CanApply) return new ProcessOutcome { State = QueueState.NeedsReview, Reason = string.Join(" ", plan.Problems), Match = summary };

            if (dryRun)
            {
                return new ProcessOutcome
                {
                    State = QueueState.Ready,
                    Reason = $"Dry run: would tag and move {plan.Files.Count} file(s) to {destination}.",
                    Match = summary,
                    Destination = destination,
                };
            }

            // 5. Apply.
            stage?.Invoke(QueueState.Applying);
            var applied = new Applier(_ops, _journals, _tagOptions).Apply(plan, dryRun: false);
            return FromApply(applied, summary, destination);
        }

        /// <summary>Finishes an album whose earlier run was cut off in the middle of applying, from its journal.</summary>
        public ProcessOutcome Resume(string albumId)
        {
            var applied = new Applier(_ops, _journals, _tagOptions).Resume(albumId);
            return FromApply(applied, null, string.Empty);
        }

        internal static ProcessOutcome FromApply(ApplyResult applied, MatchSummary? summary, string destination)
        {
            switch (applied.State)
            {
                case ApplyState.Applied:
                    return new ProcessOutcome { State = QueueState.Done, Reason = applied.Message, Match = summary, Destination = destination };
                case ApplyState.Duplicate:
                    return new ProcessOutcome { State = QueueState.Duplicate, Reason = applied.Message, Match = summary, Destination = destination };
                default:
                    return new ProcessOutcome { State = QueueState.Failed, Reason = applied.Message, Match = summary, Destination = destination };
            }
        }

        private static ProcessOutcome Failed(string reason) => new ProcessOutcome { State = QueueState.Failed, Reason = reason };

        internal static PlannerOptions CopyWithoutCover(PlannerOptions source)
        {
            var copy = new PlannerOptions { NamingScript = source.NamingScript, MaxPathLength = source.MaxPathLength, ReuseExistingFolders = source.ReuseExistingFolders, MoveCover = false };
            copy.TrackSidecarExtensions.Clear();
            foreach (var e in source.TrackSidecarExtensions) copy.TrackSidecarExtensions.Add(e);
            return copy;
        }

        /// <summary>How many different albums the files' tags name (names that are the same album with a subtitle count once).</summary>
        internal static int DistinctAlbums(IReadOnlyList<AlbumFile> files) => ClusterByAlbum(files).Count;

        /// <summary>The files grouped by the album their tags name (names that are the same album with a subtitle share a group). Files with no album tag belong to no group.</summary>
        internal static List<AlbumCluster> ClusterByAlbum(IReadOnlyList<AlbumFile> files)
        {
            var clusters = new List<AlbumCluster>();
            foreach (var file in files.Where(f => !string.IsNullOrWhiteSpace(f.Tags.Album)))
            {
                var cleaned = AlbumNameCleaner.ForSearch(file.Tags.Album);
                var cluster = clusters.FirstOrDefault(c => TitleNormaliser.AlbumSimilarity(c.Key, cleaned) >= 0.85);
                if (cluster == null) clusters.Add(cluster = new AlbumCluster { Key = cleaned, Name = file.Tags.Album.Trim() });
                cluster.Files.Add(file);
            }
            return clusters;
        }

        internal sealed class AlbumCluster
        {
            public string Key { get; set; } = string.Empty;

            /// <summary>The album name as written on the first file of the group.</summary>
            public string Name { get; set; } = string.Empty;

            public List<AlbumFile> Files { get; } = new List<AlbumFile>();
        }

        private static MatchSummary Summarise(MatchResult match) => new MatchSummary
        {
            Outcome = match.Outcome.ToString(),
            Source = match.Source,
            Reason = match.Reason,
            Score = match.Best?.Score ?? 0,
            BestReleaseId = match.Best?.Release.Id ?? string.Empty,
            BestTitle = match.Best?.Release.Title ?? string.Empty,
            BestArtist = match.Best?.Release.ArtistCredit ?? string.Empty,
            Notes = match.Notes.ToList(),
            Candidates = match.Candidates.Select(c => new CandidateSummary
            {
                ReleaseId = c.Release.Id, Title = c.Release.Title, Artist = c.Release.ArtistCredit, Country = c.Release.Country,
                Date = c.Release.Date, Score = Math.Round(c.Score, 3), WeakFiles = c.WeakFiles,
            }).ToList(),
        };
    }
}
