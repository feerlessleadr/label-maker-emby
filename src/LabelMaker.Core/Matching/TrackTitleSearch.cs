using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.MusicBrainz;

namespace LabelMaker.Core.Matching
{
    /// <summary>An album that songs of the files appear on, found by searching MusicBrainz for the song titles.</summary>
    public sealed class TitleGroup
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ArtistCredit { get; set; } = string.Empty;
        public string PrimaryType { get; set; } = string.Empty;
        public int SecondaryTypeCount { get; set; }

        /// <summary>How many of the searched files have a song of this album in their results.</summary>
        public int Votes { get; set; }
    }

    /// <summary>
    /// The last resort when the album name on the files leads nowhere (the tag is wrong, missing or "Unknown Album"): search MusicBrainz for a few
    /// of the song titles with their artist, and see which albums those songs share. An album found this way is checked track by track like any
    /// other, but it is never applied without a person looking.
    /// </summary>
    public static class TrackTitleSearch
    {
        /// <summary>At most this many files are searched, so one album costs a handful of requests at most.</summary>
        public const int MaxFilesSearched = 3;

        public static async Task<IReadOnlyList<TitleGroup>> FindAsync(
            IMusicBrainz musicBrainz, IReadOnlyList<AlbumFile> files, string albumArtist, int maxGroups, IList<string> notes, CancellationToken cancellation)
        {
            // A few files with different, non-empty titles, in track order.
            var chosen = files.Where(f => !string.IsNullOrWhiteSpace(f.Tags.Title))
                .OrderBy(f => f.Tags.DiscNumber ?? 1).ThenBy(f => f.Tags.TrackNumber ?? int.MaxValue)
                .GroupBy(f => TitleNormaliser.Normalise(f.Tags.Title)).Select(g => g.First())
                .Take(MaxFilesSearched).ToList();
            if (chosen.Count == 0)
            {
                notes.Add("the files have no song titles to search for");
                return Array.Empty<TitleGroup>();
            }

            string ArtistOf(AlbumFile f) => f.Tags.Artist.Length > 0 ? f.Tags.Artist : albumArtist;
            // A title alone names thousands of songs. Without any artist at all, only several files that agree can narrow it down.
            if (chosen.All(f => string.IsNullOrWhiteSpace(ArtistOf(f))) && chosen.Count < 2)
            {
                notes.Add("a single song title with no artist is too vague to search for");
                return Array.Empty<TitleGroup>();
            }

            var groups = new Dictionary<string, TitleGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in chosen)
            {
                var artist = ArtistOf(file);
                var hits = await musicBrainz.SearchRecordingsAsync(artist, file.Tags.Title, 25, cancellation).ConfigureAwait(false);
                var seenForFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int usable = 0;
                foreach (var hit in hits)
                {
                    if (hit.Score < 80) continue;
                    if (TitleNormaliser.Similarity(file.Tags.Title, hit.Title) < 0.85) continue;
                    if (artist.Length > 0 && !AlbumMatcher.ArtistsAgree(artist, hit.ArtistCredit)) continue;
                    usable++;
                    foreach (var release in hit.Releases)
                    {
                        if (!seenForFile.Add(release.ReleaseGroupId)) continue;
                        if (!groups.TryGetValue(release.ReleaseGroupId, out var group))
                        {
                            group = new TitleGroup
                            {
                                Id = release.ReleaseGroupId, Title = release.ReleaseGroupTitle, ArtistCredit = hit.ArtistCredit,
                                PrimaryType = release.PrimaryType, SecondaryTypeCount = release.SecondaryTypes.Count,
                            };
                            groups[release.ReleaseGroupId] = group;
                        }
                        group.Votes++;
                    }
                }
                notes.Add($"song search for \"{file.Tags.Title}\"{(artist.Length > 0 ? " by " + artist : string.Empty)}: {hits.Count} song(s) came back, {usable} fit, on {seenForFile.Count} album(s)");
            }

            int needed = chosen.All(f => string.IsNullOrWhiteSpace(ArtistOf(f))) ? 2 : 1;
            return groups.Values.Where(g => g.Votes >= needed)
                .OrderByDescending(g => g.Votes)
                .ThenByDescending(g => string.Equals(g.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase))
                .ThenBy(g => g.SecondaryTypeCount)
                .Take(Math.Max(1, maxGroups))
                .ToList();
        }
    }
}
