using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.CoverArt;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Naming;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Tags;

namespace LabelMaker.Core.Review
{
    /// <summary>
    /// What a person can do with an album on the review screen: look at what an edition would do, choose it, search for another, paste a
    /// MusicBrainz link, say which track a file is, keep their own tags, skip, discard, undo, or ask for a fresh check. Every change is
    /// refused with a plain message when it would be unsafe, and nothing here ever deletes music for good ("discard" moves files to the inbox's
    /// _Trash folder). A person pressing Apply means it, so these actions change files even while the worker is in dry-run mode.
    /// The caller (the plugin) must not run these at the same moment as a worker pass; an album that is being matched or applied is refused.
    /// </summary>
    public sealed class ReviewService
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };
        public const string TrashFolderName = "_Trash";
        private const string ManifestName = "restore.json";

        private readonly IMusicBrainz _musicBrainz;
        private readonly IFileOps _ops;
        private readonly JournalStore _journals;
        private readonly QueueStore _store;
        private readonly Func<IReadOnlyList<Lane>> _lanes;
        private readonly MatchSettings _settings;
        private readonly PlannerOptions _planner;
        private readonly TagWriteOptions _tagOptions;
        private readonly Func<DateTime> _utcNow;
        private readonly CoverArtFiller? _cover;

        public ReviewService(IMusicBrainz musicBrainz, IFileOps ops, JournalStore journals, QueueStore store, Func<IReadOnlyList<Lane>> lanes,
            MatchSettings settings, PlannerOptions planner, TagWriteOptions tagOptions, Func<DateTime>? utcNow = null, ICoverArt? coverArt = null)
        {
            _cover = coverArt == null ? null : new CoverArtFiller(coverArt, ops, journals);
            _musicBrainz = musicBrainz;
            _ops = ops;
            _journals = journals;
            _store = store;
            _lanes = lanes;
            _settings = settings;
            _planner = planner;
            _tagOptions = tagOptions;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        // ------------------------------------------------------------------ looking at an edition

        /// <summary>Shows what using this edition would do (with an optional hand-made mapping of files to tracks). Changes nothing.</summary>
        public async Task<ReviewPreview> PreviewAsync(string id, string releaseId, IReadOnlyList<MappingEntry>? mapping, CancellationToken cancellation = default)
        {
            var open = Open(id, requireOpenState: true);
            if (open.Failure != null) return new ReviewPreview { Message = open.Failure.Message };
            var planned = await PlanForReleaseAsync(open.Context!, releaseId, mapping, cancellation).ConfigureAwait(false);
            return planned.Preview;
        }

        /// <summary>Tags and files the album using this edition, after the same checks as the preview. Returns Success only when the album is filed.</summary>
        public async Task<ReviewResult> ApplyAsync(string id, string releaseId, IReadOnlyList<MappingEntry>? mapping, CancellationToken cancellation = default)
        {
            var open = Open(id, requireOpenState: true);
            if (open.Failure != null) return open.Failure;
            var planned = await PlanForReleaseAsync(open.Context!, releaseId, mapping, cancellation).ConfigureAwait(false);
            if (planned.Plan == null || !planned.Preview.CanApply)
            {
                var message = planned.Preview.Problems.Count > 0 ? string.Join(" ", planned.Preview.Problems) : planned.Preview.Message;
                return ReviewResult.Fail(message, open.Context!.Item.State);
            }
            var result = ApplyPlan(open.Context!, planned.Plan, planned.Preview, "review");
            if (_cover != null && result.Success)
            {
                var added = await _cover.AddIfMissingAsync(open.Context!.Unit.Id, cancellation).ConfigureAwait(false);
                if (added.Length > 0)
                {
                    result.Message += " " + added;
                    open.Context!.Item.Reason = result.Message;
                    Save(open.Context!.Item);
                }
            }
            return result;
        }

        // ------------------------------------------------------------------ finding another edition

        /// <summary>Searches MusicBrainz by the artist and album a person types, and shows how well each edition fits this album's files.</summary>
        public async Task<ReviewCandidates> SearchAsync(string id, string artist, string album, CancellationToken cancellation = default)
        {
            var open = Open(id, requireOpenState: false);
            if (open.Failure != null) return new ReviewCandidates { Message = open.Failure.Message };
            if (string.IsNullOrWhiteSpace(album)) return new ReviewCandidates { Message = "Type an album name to search for." };

            var result = new ReviewCandidates();
            try
            {
                var groups = new List<ReleaseGroupHit>();
                var shapes = string.IsNullOrWhiteSpace(artist)
                    ? new[] { SearchShape.QuotedAlbumOnly, SearchShape.PlainWords }
                    : new[] { SearchShape.QuotedAlbumAndArtist, SearchShape.PlainWords };
                foreach (var shape in shapes)
                {
                    foreach (var hit in await _musicBrainz.SearchReleaseGroupsAsync(artist.Trim(), album.Trim(), 5, shape, cancellation).ConfigureAwait(false))
                    {
                        if (groups.All(g => g.Id != hit.Id)) groups.Add(hit);
                    }
                    if (groups.Count >= _settings.MaxReleaseGroups) break;
                }

                var found = new Dictionary<string, ReleaseVerification>(StringComparer.Ordinal);
                foreach (var group in groups.Take(_settings.MaxReleaseGroups))
                {
                    foreach (var release in await _musicBrainz.BrowseOfficialReleasesAsync(group.Id, cancellation).ConfigureAwait(false))
                    {
                        if (!found.ContainsKey(release.Id)) found[release.Id] = ReleaseVerifier.Verify(release, open.Context!.Files, _settings.MinFileScore);
                    }
                }
                AddSorted(result, found.Values, 15);
                result.Success = true;
                result.Message = result.Candidates.Count == 0 ? "MusicBrainz has nothing under that name. Try fewer words, or paste a MusicBrainz link." : string.Empty;
            }
            catch (MusicBrainzUnavailableException e)
            {
                result.Message = "MusicBrainz cannot be reached right now: " + e.Message;
            }
            return result;
        }

        /// <summary>Turns a pasted MusicBrainz release link, release-group link or bare id into candidates for this album.</summary>
        public async Task<ReviewCandidates> ResolveReferenceAsync(string id, string pasted, CancellationToken cancellation = default)
        {
            var open = Open(id, requireOpenState: false);
            if (open.Failure != null) return new ReviewCandidates { Message = open.Failure.Message };
            var reference = ReleaseReference.Parse(pasted);
            if (reference.Kind == ReferenceKind.None)
            {
                return new ReviewCandidates { Message = "That does not look like a MusicBrainz link or id. Paste the address of a release or release group page, for example musicbrainz.org/release/ followed by the id." };
            }

            var result = new ReviewCandidates();
            try
            {
                var found = new List<ReleaseVerification>();
                if (reference.Kind == ReferenceKind.Release || reference.Kind == ReferenceKind.Unknown)
                {
                    var release = await _musicBrainz.LookupReleaseAsync(reference.Id, cancellation).ConfigureAwait(false);
                    if (release != null) found.Add(ReleaseVerifier.Verify(release, open.Context!.Files, _settings.MinFileScore));
                }
                if (found.Count == 0 && (reference.Kind == ReferenceKind.ReleaseGroup || reference.Kind == ReferenceKind.Unknown))
                {
                    foreach (var release in await _musicBrainz.BrowseOfficialReleasesAsync(reference.Id, cancellation).ConfigureAwait(false))
                    {
                        found.Add(ReleaseVerifier.Verify(release, open.Context!.Files, _settings.MinFileScore));
                    }
                }
                AddSorted(result, found, 15);
                result.Success = result.Candidates.Count > 0;
                result.Message = result.Success ? string.Empty : "MusicBrainz has no release or official edition under that id.";
            }
            catch (MusicBrainzUnavailableException e)
            {
                result.Message = "MusicBrainz cannot be reached right now: " + e.Message;
            }
            return result;
        }

        // ------------------------------------------------------------------ keep my tags

        /// <summary>
        /// Files the album using the tags already in the files, unchanged, named by the same naming script. For music MusicBrainz does not have.
        /// Needs a title, an album and an artist on every file, and no two files with the same track number on one disc.
        /// </summary>
        public ReviewResult KeepMyTags(string id)
        {
            var open = Open(id, requireOpenState: true);
            if (open.Failure != null) return open.Failure;
            var c = open.Context!;

            var lacking = c.Files.Where(f => string.IsNullOrWhiteSpace(f.Tags.Title) || string.IsNullOrWhiteSpace(f.Tags.Album) ||
                                             (string.IsNullOrWhiteSpace(f.Tags.AlbumArtist) && string.IsNullOrWhiteSpace(f.Tags.Artist))).ToList();
            if (lacking.Count > 0)
            {
                return ReviewResult.Fail($"{lacking.Count} file(s) have no title, album or artist in their tags ({string.Join(", ", lacking.Take(3).Select(f => Path.GetFileName(f.Path)))}), so there is nothing to file them by. Fix the tags, or search MusicBrainz instead.", c.Item.State);
            }

            var release = new ReleaseInfo
            {
                Title = ReleaseVerifier.MostCommon(c.Files.Select(f => f.Tags.Album)),
                ArtistCredit = ReleaseVerifier.MostCommon(c.Files.Select(f => f.Tags.AlbumArtist.Length > 0 ? f.Tags.AlbumArtist : f.Tags.Artist)),
                Id = ReleaseVerifier.MostCommon(c.Files.Select(f => f.Tags.ReleaseMbid ?? string.Empty).Where(v => Guid.TryParse(v, out _))).ToLowerInvariant(),
                Date = ReleaseVerifier.MostCommon(c.Files.Where(f => f.Tags.Year.HasValue).Select(f => f.Tags.Year!.Value.ToString())),
            };
            var forced = new Dictionary<string, (int Disc, int Position)>(StringComparer.OrdinalIgnoreCase);
            foreach (var disc in c.Files.GroupBy(f => f.Tags.DiscNumber ?? 1).OrderBy(g => g.Key))
            {
                var medium = new MediumInfo { Position = disc.Key };
                int running = 0;
                foreach (var file in disc.OrderBy(f => f.Tags.TrackNumber ?? int.MaxValue).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
                {
                    running++;
                    int position = file.Tags.TrackNumber ?? running;
                    if (medium.Tracks.Any(t => t.Position == position))
                    {
                        return ReviewResult.Fail($"Two files are track {position} on disc {disc.Key}. Fix the track numbers, or use the track mapping.", c.Item.State);
                    }
                    medium.Tracks.Add(new RecordingInfo
                    {
                        Position = position, Number = position.ToString(), Title = file.Tags.Title,
                        ArtistCredit = file.Tags.Artist, LengthMs = file.Tags.DurationMs,
                    });
                    forced[file.Path] = (disc.Key, position);
                }
                release.Media.Add(medium);
            }

            var verification = ReleaseVerifier.VerifyWithMapping(release, c.Files, forced, _settings.MinFileScore);
            var plan = Planner.Plan(c.Unit.Id, c.Lane.InboxPath, c.Unit.Folder, c.Lane.LibraryPath, verification, _ops, OptionsFor(c.Unit));
            foreach (var planned in plan.Files) planned.NewTags = planned.OldTags; // the tags stay exactly as they are
            if (!_ops.DirectoryExists(c.Lane.LibraryPath)) plan.Problems.Add("The library folder for this lane was not found or cannot be reached.");

            var preview = new ReviewPreview { Success = true, Title = release.Title, Artist = release.ArtistCredit, Score = 1 };
            FillFiles(preview, c, verification, plan, forced.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (!preview.CanApply) return ReviewResult.Fail(string.Join(" ", preview.Problems), c.Item.State);
            return ApplyPlan(c, plan, preview, "keep-my-tags");
        }

        // ------------------------------------------------------------------ leaving an album alone, discarding, undoing

        public ReviewResult Skip(string id)
        {
            var open = Open(id, requireOpenState: false, needsFiles: false);
            if (open.Failure != null) return open.Failure;
            var item = open.Context!.Item;
            if (item.State == QueueState.Done) return ReviewResult.Fail("This album is already filed. Undo it first if you want to change it.", item.State);
            item.State = QueueState.Skipped;
            item.Held = true;
            item.Reason = "You chose to skip this album. It stays in the inbox untouched until you check it again.";
            Save(item);
            return new ReviewResult { Success = true, Message = item.Reason, State = item.State };
        }

        /// <summary>Puts an album back in line for a fresh check (after skipping, undoing or fixing files by hand).</summary>
        public ReviewResult Recheck(string id)
        {
            var open = Open(id, requireOpenState: false, needsFiles: false);
            if (open.Failure != null) return open.Failure;
            var item = open.Context!.Item;
            if (item.State == QueueState.Done) return ReviewResult.Fail("This album is already filed. Undo it first if you want to change it.", item.State);
            if (!string.IsNullOrEmpty(item.TrashPath)) return ReviewResult.Fail("This album was discarded. Restore it from the trash first.", item.State);
            item.Held = false;
            item.State = QueueState.Waiting;
            item.Reason = string.Empty;
            item.Match = null;
            item.Attempts = 0;
            item.NextAttemptUtc = DateTime.MinValue;
            Save(item);
            return new ReviewResult { Success = true, Message = "The album will be checked again on the next pass.", State = item.State };
        }

        /// <summary>Moves the album's files into the inbox's _Trash folder. Nothing is deleted; <see cref="RestoreDiscarded"/> puts them back.</summary>
        public ReviewResult Discard(string id)
        {
            var open = Open(id, requireOpenState: false, needsFiles: false);
            if (open.Failure != null) return open.Failure;
            var c = open.Context!;
            c.Unit = FindUnit(c.Lane, id)!;
            if (c.Unit == null) return ReviewResult.Fail("The album is no longer in the inbox.", c.Item.State);
            if (c.Item.State == QueueState.Done) return ReviewResult.Fail("This album is already filed. Undo it first if you want to discard it.", c.Item.State);

            var inbox = TrimRoot(c.Lane.InboxPath);
            var trash = Path.Combine(inbox, TrashFolderName, _utcNow().ToString("yyyyMMdd-HHmmss") + "-" + id.Substring(0, Math.Min(8, id.Length)));
            var files = c.Unit.Kind == UnitKind.Album
                ? AllFilesUnder(c.Unit.Folder).ToList()
                : c.Unit.Files.SelectMany(f => SidecarsAndSelf(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var manifest = new TrashManifest();
            try
            {
                foreach (var file in files)
                {
                    var target = Path.Combine(trash, Path.GetRelativePath(inbox, file));
                    _ops.CreateDirectory(Path.GetDirectoryName(target)!);
                    SafeMover.Move(_ops, file, target);
                    manifest.Files.Add(new TrashEntry { From = file, To = target });
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                var back = PutBack(manifest);
                return ReviewResult.Fail("The album could not be moved to the trash (" + e.Message + ")." + (back ? " Everything was put back." : " Some files may be in the _Trash folder."), c.Item.State);
            }

            _ops.WriteAllBytes(Path.Combine(trash, ManifestName), System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Json)));
            foreach (var folder in manifest.Files.Select(f => Path.GetDirectoryName(f.From)!).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
            {
                RemoveEmptyFoldersUpTo(folder, inbox);
            }

            c.Item.State = QueueState.Skipped;
            c.Item.Held = true;
            c.Item.TrashPath = trash;
            c.Item.Reason = "You discarded this album. Its files are in the _Trash folder of the inbox and can be restored.";
            Save(c.Item);
            return new ReviewResult { Success = true, Message = c.Item.Reason, State = c.Item.State };
        }

        /// <summary>Moves a discarded album's files back to where they were. Refuses if anything is in the way.</summary>
        public ReviewResult RestoreDiscarded(string id)
        {
            var open = Open(id, requireOpenState: false, needsFiles: false);
            if (open.Failure != null) return open.Failure;
            var item = open.Context!.Item;
            if (string.IsNullOrEmpty(item.TrashPath)) return ReviewResult.Fail("This album was not discarded.", item.State);

            var manifestPath = Path.Combine(item.TrashPath, ManifestName);
            if (!_ops.FileExists(manifestPath)) return ReviewResult.Fail("The record of what was discarded is missing, so the files cannot be put back automatically. Look in the _Trash folder of the inbox.", item.State);
            var manifest = JsonSerializer.Deserialize<TrashManifest>(System.Text.Encoding.UTF8.GetString(_ops.ReadAllBytes(manifestPath)), Json) ?? new TrashManifest();
            var blocking = manifest.Files.FirstOrDefault(f => _ops.FileExists(f.From));
            if (blocking != null) return ReviewResult.Fail($"\"{Path.GetFileName(blocking.From)}\" already exists where it used to be, so nothing was restored.", item.State);

            var restored = new TrashManifest();
            try
            {
                foreach (var entry in manifest.Files)
                {
                    _ops.CreateDirectory(Path.GetDirectoryName(entry.From)!);
                    SafeMover.Move(_ops, entry.To, entry.From);
                    restored.Files.Add(entry);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                PutBack(restored, reverse: true);
                return ReviewResult.Fail("The files could not be restored (" + e.Message + "). They are still in the trash.", item.State);
            }

            _ops.DeleteFile(manifestPath);
            foreach (var folder in manifest.Files.Select(f => Path.GetDirectoryName(f.To)!).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
            {
                RemoveEmptyFoldersUpTo(folder, open.Context!.Lane.InboxPath);
            }
            item.TrashPath = string.Empty;
            item.State = QueueState.NeedsReview;
            item.Held = true;
            item.Reason = "Restored from the trash. It waits for your decision.";
            Save(item);
            return new ReviewResult { Success = true, Message = item.Reason, State = item.State };
        }

        /// <summary>Puts a filed album back in the inbox with its old tags and names. The album then waits for a decision; it is not processed again by itself.</summary>
        public ReviewResult Undo(string id)
        {
            var open = Open(id, requireOpenState: false, needsFiles: false);
            if (open.Failure != null) return open.Failure;
            var item = open.Context!.Item;
            if (item.State != QueueState.Done) return ReviewResult.Fail("Only an album that was filed can be undone.", item.State);

            var undone = new Applier(_ops, _journals, _tagOptions).Undo(id);
            if (!undone.Success) return ReviewResult.Fail(undone.Message, item.State);
            item.State = QueueState.NeedsReview;
            item.Held = true;
            item.Destination = string.Empty;
            item.Reason = "You undid this album. It is back in the inbox with its old tags and names, and waits for your decision.";
            Save(item);
            return new ReviewResult { Success = true, Message = item.Reason, State = item.State };
        }

        // ------------------------------------------------------------------ splitting a folder that holds several albums

        /// <summary>
        /// Puts each album of a mixed folder in a folder of its own inside it, named after the album, with each file's lyrics file. Nothing is
        /// renamed or tagged: files only move within the inbox, keeping their subfolders (CD1, CD2), and a cover or other loose file stays
        /// where it was. Each new folder is then picked up as an album on the next pass. Albums are told apart by the album name in the tags, so
        /// every file needs one.
        /// </summary>
        public ReviewResult SplitByAlbum(string id)
        {
            var open = Open(id, requireOpenState: true);
            if (open.Failure != null) return open.Failure;
            var c = open.Context!;
            if (c.Unit.Kind != UnitKind.Album) return ReviewResult.Fail("Only an album folder can be split.", c.Item.State);

            var clusters = AlbumProcessor.ClusterByAlbum(c.Files);
            if (clusters.Count < 2) return ReviewResult.Fail("The tags name only one album in this folder, so there is nothing to split.", c.Item.State);
            var unnamed = c.Files.Where(f => string.IsNullOrWhiteSpace(f.Tags.Album)).ToList();
            if (unnamed.Count > 0)
            {
                return ReviewResult.Fail($"{unnamed.Count} file(s) have no album name in their tags ({string.Join(", ", unnamed.Take(3).Select(f => Path.GetFileName(f.Path)))}), so Label Maker cannot tell which album they belong to. Fix their tags or move them out first.", c.Item.State);
            }

            var folder = c.Unit.Folder;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plan = new List<(AlbumProcessor.AlbumCluster Cluster, string Folder)>();
            foreach (var cluster in clusters)
            {
                var baseName = PathSanitiser.Component(cluster.Name);
                if (baseName.Length == 0) baseName = "Album";
                var candidate = baseName;
                for (int n = 2; used.Contains(candidate) || _ops.DirectoryExists(Path.Combine(folder, candidate)) || _ops.FileExists(Path.Combine(folder, candidate)); n++)
                {
                    candidate = $"{baseName} ({n})";
                }
                used.Add(candidate);
                plan.Add((cluster, candidate));
            }

            var moved = new List<(string From, string To)>();
            try
            {
                foreach (var (cluster, name) in plan)
                {
                    foreach (var file in cluster.Files)
                    {
                        foreach (var source in SidecarsAndSelf(file.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            var target = Path.Combine(folder, name, Path.GetRelativePath(folder, source));
                            SafeMover.Move(_ops, source, target);
                            moved.Add((source, target));
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                bool back = true;
                foreach (var (from, to) in Enumerable.Reverse(moved))
                {
                    try { SafeMover.Move(_ops, to, from); } catch (Exception inner) when (inner is IOException || inner is UnauthorizedAccessException) { back = false; }
                }
                foreach (var dir in plan.Select(p => Path.Combine(folder, p.Folder)))
                {
                    RemoveEmptyFoldersUpTo(dir, folder);
                }
                return ReviewResult.Fail("The folder could not be split (" + e.Message + ")." + (back ? " Everything was put back." : " Some files may be inside the new folders."), c.Item.State);
            }

            c.Item.State = QueueState.Skipped;
            c.Item.Held = true;
            c.Item.Reason = $"Split into {plan.Count} albums ({string.Join(", ", plan.Select(p => p.Folder))}). Each is checked on the next pass.";
            Save(c.Item);
            return new ReviewResult { Success = true, Message = c.Item.Reason, State = c.Item.State };
        }

        // ------------------------------------------------------------------ internals

        private sealed class Context
        {
            public QueueItem Item = null!;
            public Lane Lane = null!;
            public AlbumUnit Unit = null!;
            public List<AlbumFile> Files = new List<AlbumFile>();
        }

        private sealed class Opened
        {
            public Context? Context;
            public ReviewResult? Failure;
        }

        /// <summary>Finds the queue item, its lane and the album as it is in the inbox right now, and reads the tags.</summary>
        private Opened Open(string id, bool requireOpenState, bool needsFiles = true)
        {
            var item = _store.Get(id);
            if (item == null) return new Opened { Failure = ReviewResult.Fail("This album is not in the list (any more).") };
            if (item.State == QueueState.Matching || item.State == QueueState.Applying)
            {
                return new Opened { Failure = ReviewResult.Fail("This album is being worked on right now. Try again in a minute.", item.State) };
            }
            if (requireOpenState && item.State == QueueState.Done)
            {
                return new Opened { Failure = ReviewResult.Fail("This album is already filed. Undo it first if you want to change it.", item.State) };
            }
            if (requireOpenState && !string.IsNullOrEmpty(item.TrashPath))
            {
                return new Opened { Failure = ReviewResult.Fail("This album was discarded. Restore it from the trash first.", item.State) };
            }

            var lane = _lanes().FirstOrDefault(l => l.Id == item.LaneId);
            if (lane == null) return new Opened { Failure = ReviewResult.Fail("The lane of this album no longer exists.", item.State) };
            var context = new Context { Item = item, Lane = lane };
            if (!needsFiles) return new Opened { Context = context };

            var unit = FindUnit(lane, id);
            if (unit == null) return new Opened { Failure = ReviewResult.Fail("The album is no longer in the inbox.", item.State) };
            context.Unit = unit;

            var unreadable = new List<string>();
            foreach (var path in unit.Files)
            {
                try { context.Files.Add(new AlbumFile(path, TrackTagFile.Read(path))); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException)
                {
                    unreadable.Add(Path.GetFileName(path));
                }
            }
            if (unreadable.Count > 0)
            {
                return new Opened { Failure = ReviewResult.Fail($"{unreadable.Count} file(s) could not be read ({string.Join(", ", unreadable.Take(3))}). Fix or remove them first.", item.State) };
            }
            if (context.Files.Count == 0) return new Opened { Failure = ReviewResult.Fail("There are no audio files in this album.", item.State) };
            return new Opened { Context = context };
        }

        private AlbumUnit? FindUnit(Lane lane, string id) => new InboxScanner().Scan(lane, _ops).FirstOrDefault(u => u.Id == id);

        private async Task<(ReviewPreview Preview, AlbumPlan? Plan)> PlanForReleaseAsync(Context c, string releaseId, IReadOnlyList<MappingEntry>? mapping, CancellationToken cancellation)
        {
            ReleaseInfo? release;
            try
            {
                release = await _musicBrainz.LookupReleaseAsync((releaseId ?? string.Empty).Trim().ToLowerInvariant(), cancellation).ConfigureAwait(false);
            }
            catch (MusicBrainzUnavailableException e)
            {
                return (new ReviewPreview { Message = "MusicBrainz cannot be reached right now: " + e.Message }, null);
            }
            if (release == null) return (new ReviewPreview { Message = "That release was not found on MusicBrainz." }, null);

            var forced = new Dictionary<string, (int Disc, int Position)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in mapping ?? Array.Empty<MappingEntry>())
            {
                var file = c.Files.FirstOrDefault(f => string.Equals(Path.GetRelativePath(c.Unit.Folder, f.Path), entry.File.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                if (file == null) return (new ReviewPreview { Message = $"There is no file called \"{entry.File}\" in this album." }, null);
                forced[file.Path] = (entry.Disc, entry.Position);
            }

            ReleaseVerification verification;
            try { verification = ReleaseVerifier.VerifyWithMapping(release, c.Files, forced, _settings.MinFileScore); }
            catch (ArgumentException e) { return (new ReviewPreview { Message = e.Message }, null); }

            var plan = Planner.Plan(c.Unit.Id, c.Lane.InboxPath, c.Unit.Folder, c.Lane.LibraryPath, verification, _ops, OptionsFor(c.Unit));
            if (!_ops.DirectoryExists(c.Lane.LibraryPath)) plan.Problems.Add("The library folder for this lane was not found or cannot be reached.");

            var preview = new ReviewPreview
            {
                Success = true, ReleaseId = release.Id, Title = release.Title, Artist = release.ArtistCredit,
                Date = release.Date, Country = release.Country, Score = Math.Round(verification.Score, 3),
            };
            FillFiles(preview, c, verification, plan, forced.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            return (preview, plan);
        }

        private void FillFiles(ReviewPreview preview, Context c, ReleaseVerification verification, AlbumPlan plan, HashSet<string> placedByPerson)
        {
            foreach (var a in verification.Assignments)
            {
                var planned = plan.Files.FirstOrDefault(p => string.Equals(p.Source, a.File.Path, StringComparison.OrdinalIgnoreCase));
                preview.Files.Add(new PreviewFile
                {
                    Source = Path.GetRelativePath(c.Unit.Folder, a.File.Path),
                    Destination = planned == null ? string.Empty : Path.GetRelativePath(c.Lane.LibraryPath, planned.Destination),
                    Disc = a.Track == null ? 0 : a.Disc,
                    Position = a.Track?.Position ?? 0,
                    Title = a.Track?.Title ?? string.Empty,
                    Score = Math.Round(a.Score, 3),
                    Weak = a.Track == null || (a.Score < _settings.MinFileScore && !placedByPerson.Contains(a.File.Path)),
                });
            }
            preview.Problems.AddRange(plan.Problems);
            preview.IsDuplicate = plan.IsDuplicate;
            preview.CanApply = plan.CanApply;
            if (plan.Files.Count > 0) preview.Destination = Path.GetRelativePath(c.Lane.LibraryPath, Path.GetDirectoryName(plan.Files[0].Destination)!);
        }

        private ReviewResult ApplyPlan(Context c, AlbumPlan plan, ReviewPreview preview, string source)
        {
            var item = c.Item;
            item.State = QueueState.Applying;
            Save(item);
            var applied = new Applier(_ops, _journals, _tagOptions).Apply(plan, dryRun: false);

            var summary = new MatchSummary
            {
                Outcome = "ChosenByPerson", Source = source, Score = preview.Score, BestReleaseId = preview.ReleaseId,
                BestTitle = preview.Title, BestArtist = preview.Artist, Reason = "Chosen on the review screen.",
                Candidates = item.Match?.Candidates ?? new List<CandidateSummary>(),
            };
            var outcome = AlbumProcessor.FromApply(applied, summary, preview.Destination);
            item.State = outcome.State;
            item.Reason = outcome.Reason;
            item.Match = summary;
            item.Destination = outcome.Destination;
            item.Held = false;
            item.Attempts = 0;
            item.NextAttemptUtc = DateTime.MinValue;
            Save(item);
            return new ReviewResult { Success = outcome.State == QueueState.Done, Message = outcome.Reason, State = outcome.State };
        }

        private PlannerOptions OptionsFor(AlbumUnit unit) => unit.Kind == UnitKind.Single ? AlbumProcessor.CopyWithoutCover(_planner) : _planner;

        private static void AddSorted(ReviewCandidates result, IEnumerable<ReleaseVerification> found, int limit)
        {
            foreach (var v in found.OrderByDescending(v => v.Score).ThenBy(v => v.Release.Date, StringComparer.Ordinal).Take(limit))
            {
                result.Candidates.Add(new CandidateSummary
                {
                    ReleaseId = v.Release.Id, Title = v.Release.Title, Artist = v.Release.ArtistCredit, Country = v.Release.Country,
                    Date = v.Release.Date, Score = Math.Round(v.Score, 3), WeakFiles = v.WeakFiles,
                });
            }
        }

        private void Save(QueueItem item)
        {
            item.UpdatedUtc = _utcNow();
            _store.Save(item);
        }

        private static string TrimRoot(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private IEnumerable<string> AllFilesUnder(string folder)
        {
            foreach (var file in _ops.EnumerateFiles(folder)) yield return file;
            foreach (var sub in _ops.EnumerateDirectories(folder))
            {
                foreach (var file in AllFilesUnder(sub)) yield return file;
            }
        }

        /// <summary>A single track plus the files that go with it (same name, another extension: lyrics and the like).</summary>
        private IEnumerable<string> SidecarsAndSelf(string file)
        {
            yield return file;
            var folder = Path.GetDirectoryName(file)!;
            var stem = Path.GetFileNameWithoutExtension(file);
            foreach (var other in _ops.EnumerateFiles(folder))
            {
                if (string.Equals(other, file, StringComparison.OrdinalIgnoreCase)) continue;
                if (!InboxScanner.IsAudio(other) && string.Equals(Path.GetFileNameWithoutExtension(other), stem, StringComparison.OrdinalIgnoreCase)) yield return other;
            }
        }

        private void RemoveEmptyFoldersUpTo(string folder, string stopAt)
        {
            var root = TrimRoot(stopAt);
            var current = TrimRoot(folder);
            while (current.Length > root.Length && current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                if (!_ops.DeleteDirectoryIfEmpty(current)) return;
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
        }

        /// <summary>Moves every file of the manifest back (the other way when reverse is set); true when all went back.</summary>
        private bool PutBack(TrashManifest manifest, bool reverse = false)
        {
            bool all = true;
            foreach (var entry in manifest.Files)
            {
                try
                {
                    if (reverse) SafeMover.Move(_ops, entry.From, entry.To);
                    else SafeMover.Move(_ops, entry.To, entry.From);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { all = false; }
            }
            return all;
        }

        private sealed class TrashEntry
        {
            public string From { get; set; } = string.Empty;
            public string To { get; set; } = string.Empty;
        }

        private sealed class TrashManifest
        {
            public List<TrashEntry> Files { get; set; } = new List<TrashEntry>();
        }
    }
}
