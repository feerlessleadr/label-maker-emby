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
        /// editions of up to this many albums with the right name, so the wrong one is never chosen just because it was found first (a band can have a single, two albums and a live record all called "High Voltage"). Default 5.
        /// </summary>
        public int MaxReleaseGroups { get; set; } = 5;

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

        /// <summary>What was searched and what came back, in plain lines. Shown when someone asks why an album was not matched.</summary>
        public IReadOnlyList<string> Notes { get; set; } = Array.Empty<string>();
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
            var notes = new List<string>();
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
                            Notes = new[] { "MusicGrabber's release id was looked up and every file fits it" },
                        };
                    }
                    notes.Add($"MusicGrabber's release id points at \"{hinted.Title}\", which fits the files only at {verified.Score:0.00} with {verified.WeakFiles} weak file(s), so it is not trusted on its own");
                    candidates.Add(verified); // shown in review even though it did not fit well enough on its own
                }
                else
                {
                    notes.Add("MusicGrabber's release id was not found on MusicBrainz");
                }
            }

            // 1b. Release ids already in the files' tags (a Picard-tagged file carries its release id): looked up and checked exactly like the hint
            // above. If the files of one folder carry different ids, nothing is applied automatically: a person decides which album they belong to.
            var tagIds = files.Select(f => (f.Tags.ReleaseMbid ?? string.Empty).Trim().ToLowerInvariant())
                .Where(id => Guid.TryParse(id, out _)).Distinct().ToList();
            bool mixedIds = tagIds.Count > 1;
            foreach (var id in tagIds)
            {
                if (info != null && string.Equals(info.ReleaseMbid, id, StringComparison.OrdinalIgnoreCase)) continue; // already looked up above
                var tagged = await _musicBrainz.LookupReleaseAsync(id, cancellation).ConfigureAwait(false);
                if (tagged == null) { notes.Add($"the release id {id} in the tags was not found on MusicBrainz"); continue; }
                var verifiedTag = ReleaseVerifier.Verify(tagged, files, _settings.MinFileScore);
                if (!mixedIds && verifiedTag.WeakFiles == 0 && verifiedTag.Score >= _settings.AutoThreshold)
                {
                    return new MatchResult
                    {
                        Outcome = MatchOutcome.Auto,
                        Source = "tags",
                        Reason = "The release id already in the tags names this release and every file fits it.",
                        Best = verifiedTag,
                        Candidates = new[] { verifiedTag },
                        Notes = new[] { "the release id in the tags was looked up and every file fits it" },
                    };
                }
                notes.Add($"the release id in the tags points at \"{tagged.Title}\", which fits the files only at {verifiedTag.Score:0.00} with {verifiedTag.WeakFiles} weak file(s)");
                if (!candidates.Any(c => string.Equals(c.Release.Id, tagged.Id, StringComparison.OrdinalIgnoreCase))) candidates.Add(verifiedTag);
            }
            if (mixedIds) notes.Add($"the files carry {tagIds.Count} different release ids");

            // 2. Search by album and artist, then check every official edition of the best release groups.
            var artist = ReleaseVerifier.MostCommon(files.Select(f => f.Tags.AlbumArtist.Length > 0 ? f.Tags.AlbumArtist : f.Tags.Artist));
            var album = ReleaseVerifier.MostCommon(files.Select(f => f.Tags.Album));
            if (artist.Length == 0 && info != null) artist = info.Artist;
            if (album.Length == 0 && info != null) album = info.Album;
            if (album.Length == 0)
            {
                notes.Add("the files have no album name");
                var none = Finish(candidates, source, "These files have no album name to search for.");
                none.Notes = notes;
                return none;
            }

            // The exact "album AND artist" search is precise but often finds nothing, so it is tried first and the looser searches follow, each
            // with the album name as it is (noise such as "[FLAC]" removed), then without any trailing "(...)", then with 2 and II swapped.
            // Every album a search leaves is checked track by track. The cascade stops as soon as some edition fits the files well enough to
            // suggest (the review threshold); until then the next search is tried, up to a fixed number of searches.
            var browsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relaxedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // albums found only because the name or artist differs from the tags
            int attempts = 0;
            const int MaxSearches = 8;
            bool enough = false;
            foreach (var variant in AlbumNameVariants(album))
            {
                foreach (var shape in SearchShapes(artist))
                {
                    if (attempts >= MaxSearches) break;
                    attempts++;
                    int limit = 25; // a common title can name many albums; one request returns them all
                    var groups = await _musicBrainz.SearchReleaseGroupsAsync(artist, variant, limit, shape, cancellation).ConfigureAwait(false);

                    // Albums whose name and artist both fit; if there are none, albums whose name fits (an artist can be credited under another
                    // name, "mgk" for "Machine Gun Kelly"): those can only reach review, because the artist part of the score stays low.
                    var chosen = ChooseGroups(groups, variant, artist, requireArtist: true).ToList();
                    bool relaxed = false;
                    if (chosen.Count == 0)
                    {
                        // An album can be filed under a slightly different name (renamed, or a regional title): same artist, similar name.
                        var renamed = groups.Where(g => ArtistsStronglyAgree(artist, g.ArtistCredit) && TitleNormaliser.AlbumSimilarity(g.Title, variant) >= 0.5)
                            .OrderByDescending(g => TitleNormaliser.AlbumSimilarity(g.Title, variant)).Take(2);
                        chosen = renamed.Concat(ChooseGroups(groups, variant, artist, requireArtist: false))
                            .GroupBy(g => g.Id).Select(g => g.First()).Take(_settings.MaxReleaseGroups).ToList();
                        relaxed = chosen.Count > 0;
                    }
                    notes.Add($"search {attempts} ({Describe(shape)}; album \"{variant}\", artist \"{artist}\"): {groups.Count} album(s) came back, {chosen.Count} kept{(relaxed ? " (name only, artist differs)" : string.Empty)}"
                        + (groups.Count == 0 ? string.Empty : ": " + string.Join("; ", groups.Take(5).Select(g => $"\"{g.Title}\" by {g.ArtistCredit} (score {g.Score}, {g.PrimaryType})"))));

                    foreach (var group in chosen)
                    {
                        if (relaxed) relaxedGroups.Add(group.Id);
                        if (!browsed.Add(group.Id)) continue;
                        var releases = await _musicBrainz.BrowseOfficialReleasesAsync(group.Id, cancellation).ConfigureAwait(false);
                        notes.Add($"\"{group.Title}\" by {group.ArtistCredit} has {releases.Count} official edition(s)");
                        foreach (var release in releases)
                        {
                            if (candidates.Any(c => string.Equals(c.Release.Id, release.Id, StringComparison.OrdinalIgnoreCase))) continue;
                            candidates.Add(ReleaseVerifier.Verify(release, files, _settings.MinFileScore));
                        }
                    }
                    if (candidates.Count > 0 && candidates.Max(c => c.Score) >= _settings.ReviewThreshold) { enough = true; break; }
                }
                if (enough || attempts >= MaxSearches) break;
            }
            if (candidates.Count > 0)
            {
                var top = candidates.OrderByDescending(c => c.Score).First();
                notes.Add($"best edition checked: \"{top.Release.Title}\" by {top.Release.ArtistCredit} ({top.Release.Country} {top.Release.Date}), score {top.Score:0.00}, tracks {top.TrackScore:0.00}, {top.WeakFiles} weak file(s)");
            }

            var result = Finish(candidates, source, "No release was found for this album.");
            result.Notes = notes;

            // An album found only through the relaxed passes (MusicBrainz lists it under another name or artist than the tags say) is never
            // applied without a person looking, however well the tracks fit.
            if (result.Outcome == MatchOutcome.Auto && result.Best != null && relaxedGroups.Contains(result.Best.Release.ReleaseGroupId))
            {
                result.Outcome = MatchOutcome.NeedsReview;
                result.Reason = "MusicBrainz lists this album under a different name or artist than your tags say, so please check it.";
            }
            if (mixedIds && result.Outcome == MatchOutcome.Auto)
            {
                result.Outcome = MatchOutcome.NeedsReview;
                result.Reason = "The files in this folder carry different MusicBrainz release ids, so please check which album they belong to.";
            }
            return result;
        }

        /// <summary>Groups whose title is close to the album, plain albums before compilations and live records.</summary>
        /// <summary>The album names to try, in order: as written (noise removed), without trailing "(...)", and each with 2 and II swapped.</summary>
        internal static IReadOnlyList<string> AlbumNameVariants(string album)
        {
            var list = new List<string>();
            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
            }
            var clean = AlbumNameCleaner.ForSearch(album);
            var loose = AlbumNameCleaner.ForSearchLoose(album);
            Add(clean);
            Add(loose);
            Add(TitleNormaliser.SwapNumerals(clean));
            Add(TitleNormaliser.SwapNumerals(loose));
            return list;
        }

        private static IEnumerable<SearchShape> SearchShapes(string artist)
        {
            if (!string.IsNullOrWhiteSpace(artist)) yield return SearchShape.QuotedAlbumAndArtist;
            yield return SearchShape.QuotedAlbumOnly;
            yield return SearchShape.PlainWords;
            if (!string.IsNullOrWhiteSpace(artist) && TitleNormaliser.Normalise(artist) != "various artists") yield return SearchShape.ArtistAlbums;
        }

        private static string Describe(SearchShape shape) =>
            shape == SearchShape.QuotedAlbumAndArtist ? "exact album and artist"
            : shape == SearchShape.QuotedAlbumOnly ? "exact album, any artist"
            : shape == SearchShape.ArtistAlbums ? "all albums by the artist"
            : "plain words";

        /// <summary>
        /// Whether the artist on the files and the artist MusicBrainz credits are plausibly the same: similar names, or a shared name part
        /// ("Alan Menken &amp; Howard Ashman" and "Alan Menken"), or "Various Artists" on either side. Track-by-track checking still decides.
        /// </summary>
        internal static bool ArtistsAgree(string fileArtist, string credit)
        {
            var a = TitleNormaliser.Normalise(fileArtist);
            var b = TitleNormaliser.Normalise(credit);
            if (a.Length == 0 || b.Length == 0) return true;
            if (a == "various artists" || b == "various artists") return true;
            if (TitleNormaliser.Similarity(fileArtist, credit) >= 0.5) return true;
            var skip = new HashSet<string> { "the", "and", "feat", "featuring", "ft", "with", "cast" };
            var ta = new HashSet<string>(a.Split(' ').Where(w => w.Length > 0 && !skip.Contains(w)));
            var tb = new HashSet<string>(b.Split(' ').Where(w => w.Length > 0 && !skip.Contains(w)));
            if (ta.Count == 0 || tb.Count == 0) return true;
            int shared = ta.Count(w => tb.Contains(w));
            return shared > 0 && shared >= 0.5 * Math.Min(ta.Count, tb.Count);
        }

        /// <summary>The same artist under (nearly) the same name, not just a shared word. "Various Artists" never counts.</summary>
        internal static bool ArtistsStronglyAgree(string fileArtist, string credit)
        {
            if (string.IsNullOrWhiteSpace(fileArtist) || string.IsNullOrWhiteSpace(credit)) return false;
            if (TitleNormaliser.Normalise(fileArtist) == "various artists" || TitleNormaliser.Normalise(credit) == "various artists") return false;
            return TitleNormaliser.Similarity(fileArtist, credit) >= 0.9;
        }

        private IEnumerable<ReleaseGroupHit> ChooseGroups(IReadOnlyList<ReleaseGroupHit> groups, string album, string artist, bool requireArtist)
        {
            var fitting = groups
                .Where(g => (g.Score >= 80 || !requireArtist) && TitleNormaliser.AlbumSimilarity(g.Title, album) >= 0.85 && (!requireArtist || ArtistsAgree(artist, g.ArtistCredit)))
                .OrderBy(g => g.SecondaryTypes.Count)
                .ThenByDescending(g => g.Score)
                .ToList();

            // Albums with exactly the name on the files come first, and all of them are checked (up to 10): a very common title can name several
            // different albums (a film soundtrack, a cast recording), and the right one must not be cut off by the usual limit.
            var wanted = TitleNormaliser.Normalise(album);
            var exact = fitting.Where(g => TitleNormaliser.Normalise(g.Title) == wanted).ToList();
            var others = fitting.Where(g => TitleNormaliser.Normalise(g.Title) != wanted).ToList();
            int limit = Math.Max(_settings.MaxReleaseGroups, Math.Min(exact.Count, 10));
            return exact.Concat(others).Take(limit);
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
            // Editions of one album are interchangeable. A different album with the very same title (another release group: a film soundtrack and a cast recording both called "The Sound of Music") that reads the same for these files is not: a person decides. A differently named album (a "Deluxe" edition) is left to the edition rules.
            bool OtherAlbum(ReleaseVerification c) => c.Release.ReleaseGroupId.Length > 0 && best.Release.ReleaseGroupId.Length > 0 && c.Release.ReleaseGroupId != best.Release.ReleaseGroupId
                && TitleNormaliser.Normalise(c.Release.Title) == TitleNormaliser.Normalise(best.Release.Title);
            int identical = rivals.Count(c => Same(c, best) && !OtherAlbum(c));
            if (rivals.Any(c => !Same(c, best)))
            {
                return Result(MatchOutcome.NeedsReview, "Several editions fit these files about equally.", identical);
            }
            if (rivals.Any(OtherAlbum))
            {
                return Result(MatchOutcome.NeedsReview, "Two different albums on MusicBrainz fit these files equally, so please pick the right one.", identical);
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
