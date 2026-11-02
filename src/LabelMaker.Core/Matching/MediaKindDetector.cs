using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LabelMaker.Core.Inbox;

namespace LabelMaker.Core.Matching
{
    public enum MediaKind
    {
        Classical,
        Audiobook,
        Podcast,
    }

    /// <summary>What the detector saw, in a sentence a person can read.</summary>
    public sealed class MediaKindFinding
    {
        public MediaKind Kind { get; set; }

        /// <summary>One plain sentence, for the front of the album's reason.</summary>
        public string Sentence { get; set; } = string.Empty;

        /// <summary>The evidence ("the genre tag says Classical"), for the notes on the album.</summary>
        public string Evidence { get; set; } = string.Empty;
    }

    /// <summary>
    /// Recognizes albums that Label Maker's matching was not built for: classical music (works and movements, not songs), audiobooks and
    /// podcasts (MusicBrainz hardly lists them). The rules are plain and cautious: a genre tag that says so, chapter-like titles, movement
    /// words in the titles, or one very long recording. An album that is recognized is never filed automatically; a person looks at it.
    /// </summary>
    public static class MediaKindDetector
    {
        /// <summary>A file this long (90 minutes) is not a song or a movement: an audiobook, a long podcast or a mix.</summary>
        public static readonly TimeSpan VeryLong = TimeSpan.FromMinutes(90);

        private static readonly string[] PodcastGenres = { "podcast" };
        private static readonly string[] AudiobookGenres = { "audiobook", "audio book", "spoken", "speech", "audio drama", "hörbuch", "hoerbuch" };
        private static readonly string[] ClassicalGenres = { "classical", "opera", "baroque", "orchestral", "chamber music", "symphon", "choral" };

        private static readonly Regex ChapterTitle = new Regex(
            @"^\s*(chapter|part|book|kapitel|chapitre|capitolo|cap[ií]tulo|prologue|epilogue|prolog|epilog)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex MovementTitle = new Regex(
            @"\b(op\.?\s*\d+|bwv\s*\d+|k\.?\s*\d{2,3}|symphony|sinfonia|concerto|sonata|prelude|fugue|allegro|adagio|andante|largo|scherzo|rondo|minuet|menuetto|nocturne|etude|suite)\b|^\s*[IVX]{1,4}\.\s",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>The finding for these files, or null when they look like ordinary music.</summary>
        public static MediaKindFinding? Detect(IReadOnlyList<AlbumFile> files)
        {
            if (files == null || files.Count == 0) return null;
            int half = (files.Count + 1) / 2;

            int GenreCount(string[] words) => files.Count(f => words.Any(w => f.Tags.Genre.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));

            if (GenreCount(PodcastGenres) >= half) return Found(MediaKind.Podcast, "the genre tag says Podcast");
            if (files.Count(f => f.Tags.Album.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) >= half) return Found(MediaKind.Podcast, "the album name says Podcast");
            if (GenreCount(AudiobookGenres) >= half) return Found(MediaKind.Audiobook, "the genre tag says " + FirstGenre(files));
            if (files.Any(f => f.Tags.DurationMs.HasValue && f.Tags.DurationMs.Value >= VeryLong.TotalMilliseconds))
            {
                return Found(MediaKind.Audiobook, "one file is longer than 90 minutes");
            }
            if (files.Count >= 3 && files.Count(f => ChapterTitle.IsMatch(f.Tags.Title)) >= half) return Found(MediaKind.Audiobook, "the titles read like chapters");
            if (GenreCount(ClassicalGenres) >= half) return Found(MediaKind.Classical, "the genre tag says " + FirstGenre(files));
            if (files.Count >= 2 && files.Count(f => MovementTitle.IsMatch(f.Tags.Title)) >= half) return Found(MediaKind.Classical, "the titles name works and movements");
            return null;
        }

        private static string FirstGenre(IReadOnlyList<AlbumFile> files) =>
            files.Select(f => f.Tags.Genre).FirstOrDefault(g => g.Length > 0) is string genre ? "\"" + genre + "\"" : "something like that";

        private static MediaKindFinding Found(MediaKind kind, string evidence) => new MediaKindFinding
        {
            Kind = kind,
            Evidence = evidence,
            Sentence = kind == MediaKind.Classical
                ? "This looks like classical music (" + evidence + "), which is named by work and movement rather than by song, so it needs your eye."
                : kind == MediaKind.Podcast
                    ? "This looks like a podcast (" + evidence + "), which MusicBrainz does not list, so it needs your eye."
                    : "This looks like an audiobook or a long recording (" + evidence + "), which MusicBrainz rarely lists, so it needs your eye.",
        };
    }
}
