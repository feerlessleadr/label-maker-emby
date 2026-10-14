using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.MusicBrainz;

namespace LabelMaker.Core.Matching
{
    public enum MatchOutcome
    {
        /// <summary>Sure enough to tag and file without asking (when the lane allows it and dry run is off).</summary>
        Auto,

        /// <summary>A likely match exists, but a person should look: see <see cref="MatchResult.Reason"/>.</summary>
        NeedsReview,

        /// <summary>Nothing usable was found. The album stays in the inbox.</summary>
        NoMatch,
    }

    public enum EditionRule
    {
        /// <summary>
        /// Strict. Two editions of an album count as the same only when MusicBrainz lists the very same recording of every one of your
        /// songs. A remastered edition lists different recordings, so when both a remaster and the original fit your files, the album goes to
        /// your review list so you choose. Safest, but more albums wait for you.
        /// </summary>
        SameRecordings,

        /// <summary>
        /// Same titles (the default). Editions of an album count as the same when they list the same song titles in the same places for the
        /// files you have. Label Maker then picks one for you (your preferred countries first, then the earliest release date). Your files, folders
        /// and visible tags come out the same whichever it picks; the only difference is the hidden MusicBrainz ids, which may belong to a
        /// different edition of the same album (for example the remaster). Editions that list different titles or a different order are never
        /// treated as the same.
        /// </summary>
        SameTitles,
    }

    public sealed class MatchSettings
    {
        /// <summary>
        /// How sure Label Maker must be before it tags and moves an album without asking you. The score runs from 0 (nothing fits) to 1 (every
        /// title, number and name fits exactly). Default 0.92: very close to certain. Raise it to send more albums to review; lowering it
        /// applies more albums on its own and, in testing on a real library, starts letting wrong ones through.
        /// </summary>
        public double AutoThreshold { get; set; } = 0.92;

        /// <summary>
        /// Below this score the best candidate is too poor to even suggest, and the album is reported as "no match" and left in the inbox.
        /// Between this and the automatic threshold the album goes to your review list with its best candidates. Default 0.60.
        /// </summary>
        public double ReviewThreshold { get; set; } = 0.60;

        /// <summary>
        /// Every file in the album must fit some track of the release at least this well. One file that does not fit (a different song, a
        /// bonus track the release does not list) sends the whole album to review, so a single wrong file cannot hide in a good average.
        /// Default 0.5.
        /// </summary>
        public double MinFileScore { get; set; } = 0.5;

        /// <summary>
        /// When another edition fits your files almost as well as the best one, and it is a genuinely different fit (not just the same
        /// songs in the same places), the album goes to review instead of guessing. "Almost as well" means within this score. Default 0.05.
        /// </summary>
        public double Margin { get; set; } = 0.05;

        /// <summary>
        /// Several different albums can share a name (a studio album and a live album, or two artists' "Greatest Hits"). Label Maker checks the
        /// editions of up to this many albums with the right name, so the wrong one is never chosen just because it was found first. Default 3.
        /// </summary>
        public int MaxReleaseGroups { get; set; } = 3;

        /// <summary>How Label Maker decides that two editions of an album are interchangeable for your files. See <see cref="EditionRule"/>.</summary>
        public EditionRule EditionRule { get; set; } = EditionRule.SameTitles;

        /// <summary>
        /// When several editions are interchangeable, the one released in the first of these countries wins ("XW" means worldwide). If none match,
        /// the earliest release date wins. Default: United States, worldwide, then Great Britain.
        /// </summary>
        public IList<string> PreferredCountries { get; } = new List<string> { "US", "XW", "GB" };
    }

    public sealed class MatchResult
    {
        public MatchOutcome Outcome { get; set; }

        /// <summary>One plain sentence saying why, for the review list.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>"albuminfo" when MusicGrabber's release id was used, "search" for a MusicBrainz search, or empty.</summary>
        public string Source { get; set; } = string.Empty;

        public ReleaseVerification? Best { get; set; }

        /// <summary>Editions that fit the files exactly as the best one does (same recordings in the same places).</summary>
        public int InterchangeableEditions { get; set; }

        /// <summary>The checked editions, best first, for the review screen.</summary>
        public IReadOnlyList<ReleaseVerification> Candidates { get; set; } = Array.Empty<ReleaseVerification>();
    }

    /// <summary>
    /// Finds the MusicBrainz release for the files of one album folder. First the release id MusicGrabber wrote in
    /// <c>.albuminfo</c> (one lookup, verified against the files); otherwise a release-group search, then every official edition of
    /// the best groups is checked against the files. Nothing is applied from a guess: weak matches go to review.
    /// </summary>
    public sealed class AlbumMatcher
    {
        private readonly IMusicBrainz _musicBrainz;
        private readonly MatchSettings _settings;

        public AlbumMatcher(IMusicBrainz musicBrainz, MatchSettings? settings = null)
        {
            _musicBrainz = musicBrainz ?? throw new ArgumentNullException(nameof(musicBrainz));
            _settings = settings ?? new MatchSettings();
        }

        public async Task<MatchResult> MatchAsync(IReadOnlyList<AlbumFile> files, AlbumInfo? info, CancellationToken cancellation = default)
        {
            if (files == null || files.Count == 0)
            {
                return new MatchResult { Outcome = MatchOutcome.NoMatch, Reason = "There are no audio files in this folder." };
            }

            var candidates = new List<ReleaseVerification>();
            string source = "search";

            // 1. The release MusicGrabber chose: one lookup, then check the files really fit it.
            if (info != null && info.ReleaseMbid.Length > 0)
            {
                var hinted = await _musicBrainz.LookupReleaseAsync(info.ReleaseMbid, cancellation).ConfigureAwait(false);
                if (hinted != null)
                {
                    var verified = ReleaseVerifier.Verify(hinted, files, _settings.MinFileScore);
                    if (verified.WeakFiles == 0 && verified.Score >= _settings.AutoThreshold)
                    {
                        return new MatchResult
                        {
                            Outcome = MatchOutcome.Auto,
                            Source = "albuminfo",
                            Reason = "MusicGrabber named this release and every file fits it.",
                            Best = verified,
                            Candidates = new[] { verified },
                        };
                    }
                    candidates.Add(verified); // shown in review even though it did not fit well enough on its own
                }
            }

            // 2. Search by album and artist, then check every official edition of the best release groups.
            var artist = ReleaseVerifier.MostCommon(files.Select(f => f.Tags.AlbumArtist.Length > 0 ? f.Tags.AlbumArtist : f.Tags.Artist));
            var album = ReleaseVerifier.MostCommon(files.Select(f => f.Tags.Album));
            if (artist.Length == 0 && info != null) artist = info.Artist;
            if (album.Length == 0 && info != null) album = info.Album;
            if (album.Length == 0)
            {
                return Finish(candidates, source, "These files have no album name to search for.");
            }

            var searchAlbum = AlbumNameCleaner.ForSearch(album); // the noise ("[FLAC]", "(Deluxe Edition)") would make the search miss
            var groups = await _musicBrainz.SearchReleaseGroupsAsync(artist, searchAlbum, 5, cancellation).ConfigureAwait(false);
            foreach (var group in ChooseGroups(groups, searchAlbum))
            {
                var releases = await _musicBrainz.BrowseOfficialReleasesAsync(group.Id, cancellation).ConfigureAwait(false);
                foreach (var release in releases)
                {
                    if (candidates.Any(c => string.Equals(c.Release.Id, release.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    candidates.Add(ReleaseVerifier.Verify(release, files, _settings.MinFileScore));
                }
            }
            return Finish(candidates, source, "No release was found for this album.");
        }

        /// <summary>Groups whose title is close to the album, plain albums before compilations and live records.</summary>
        private IEnumerable<ReleaseGroupHit> ChooseGroups(IReadOnlyList<ReleaseGroupHit> groups, string album)
        {
            return groups
                .Where(g => g.Score >= 80 && TitleNormaliser.Similarity(g.Title, album) >= 0.85)
                .OrderBy(g => g.SecondaryTypes.Count)
                .ThenByDescending(g => g.Score)
                .Take(_settings.MaxReleaseGroups);
        }

        private MatchResult Finish(List<ReleaseVerification> candidates, string source, string noneFound)
        {
            if (candidates.Count == 0)
            {
                return new MatchResult { Outcome = MatchOutcome.NoMatch, Reason = noneFound };
            }

            var ordered = candidates
                .OrderByDescending(c => Math.Round(c.Score, 4))
                .ThenBy(c => CountryRank(c.Release.Country))
                .ThenBy(c => string.IsNullOrEmpty(c.Release.Date) ? "9999" : c.Release.Date, StringComparer.Ordinal)
                .ToList();
            var best = ordered[0];

            MatchResult Result(MatchOutcome outcome, string reason, int same = 0) => new MatchResult
            {
                Outcome = outcome,
                Reason = reason,
                Source = source,
                Best = best,
                InterchangeableEditions = same,
                Candidates = ordered.Take(10).ToList(),
            };

            if (best.Score < _settings.ReviewThreshold)
            {
                return new MatchResult { Outcome = MatchOutcome.NoMatch, Source = source, Reason = "Nothing on MusicBrainz fits these files well enough.", Candidates = ordered.Take(10).ToList() };
            }
            if (best.WeakFiles > 0)
            {
                int total = best.Assignments.Count;
                return Result(MatchOutcome.NeedsReview, $"{best.WeakFiles} of {total} files could not be matched to a track of the best release.");
            }
            if (best.Score < _settings.AutoThreshold)
            {
                return Result(MatchOutcome.NeedsReview, "The best match is only probable.");
            }

            // Other editions that fit about as well: the same recordings in the same places do not matter, different ones do.
            var rivals = ordered.Skip(1).Where(c => c.Score >= best.Score - _settings.Margin).ToList();
            int identical = rivals.Count(c => Same(c, best));
            if (rivals.Any(c => !Same(c, best)))
            {
                return Result(MatchOutcome.NeedsReview, "Several editions fit these files about equally.", identical);
            }

            return Result(
                MatchOutcome.Auto,
                identical > 0
                    ? $"{identical + 1} editions fit these files identically; the preferred one was picked."
                    : "Every file fits one release clearly better than any other.",
                identical);
        }

        /// <summary>True when two editions read the same for the files under the chosen edition rule.</summary>
        private bool Same(ReleaseVerification a, ReleaseVerification b) =>
            _settings.EditionRule == EditionRule.SameTitles ? a.TitleSignature == b.TitleSignature : a.Signature == b.Signature;

        private int CountryRank(string country)
        {
            int index = -1;
            for (int i = 0; i < _settings.PreferredCountries.Count; i++)
            {
                if (string.Equals(_settings.PreferredCountries[i], country, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            }
            return index < 0 ? int.MaxValue : index;
        }
    }
}
