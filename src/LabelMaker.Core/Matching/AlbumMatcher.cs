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

    public sealed class MatchSettings
    {
        /// <summary>The best edition must score at least this much to be applied without asking.</summary>
        public double AutoThreshold { get; set; } = 0.92;

        /// <summary>Under this, there is no match at all.</summary>
        public double ReviewThreshold { get; set; } = 0.60;

        /// <summary>Every file needs a track scoring at least this much.</summary>
        public double MinFileScore { get; set; } = 0.5;

        /// <summary>A different edition (with other tracks) must score at least this much lower than the best, or the album goes to review.</summary>
        public double Margin { get; set; } = 0.05;

        /// <summary>How many release groups with the album's title are checked (several albums can share a title).</summary>
        public int MaxReleaseGroups { get; set; } = 3;

        /// <summary>Country codes in order of preference for choosing between identical editions ("XW" is worldwide).</summary>
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

            var groups = await _musicBrainz.SearchReleaseGroupsAsync(artist, album, 5, cancellation).ConfigureAwait(false);
            foreach (var group in ChooseGroups(groups, album))
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
            int identical = rivals.Count(c => c.Signature == best.Signature);
            if (rivals.Any(c => c.Signature != best.Signature))
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
