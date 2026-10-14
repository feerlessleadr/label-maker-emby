using System;
using System.Collections.Generic;
using System.Linq;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;

namespace LabelMaker.Core.Matching
{
    /// <summary>One audio file of an album folder, with the tags read from it.</summary>
    public sealed class AlbumFile
    {
        public AlbumFile(string path, TrackTags tags)
        {
            Path = path;
            Tags = tags;
        }

        public string Path { get; }
        public TrackTags Tags { get; }
    }

    /// <summary>A file paired with the track of a release it was matched to (<see cref="Track"/> is null when no track was left for it).</summary>
    public sealed class FileAssignment
    {
        public AlbumFile File { get; set; } = null!;
        public int Disc { get; set; }
        public RecordingInfo? Track { get; set; }
        public double Score { get; set; }
    }

    /// <summary>How well one edition of an album fits the files.</summary>
    public sealed class ReleaseVerification
    {
        public ReleaseInfo Release { get; set; } = null!;
        public IReadOnlyList<FileAssignment> Assignments { get; set; } = Array.Empty<FileAssignment>();

        /// <summary>The average of the files' own scores, 0 to 1.</summary>
        public double TrackScore { get; set; }

        /// <summary>The whole edition's score: tracks, album title and artist, 0 to 1.</summary>
        public double Score { get; set; }

        /// <summary>The lowest score of any single file. One wrong file must not hide inside a good average.</summary>
        public double MinFileScore { get; set; }

        /// <summary>Files with no track, or a track scoring under the settings' minimum.</summary>
        public int WeakFiles { get; set; }

        /// <summary>Which recording sits where for these files. Two editions with the same signature are interchangeable for them.</summary>
        public string Signature { get; set; } = string.Empty;

        /// <summary>Which track titles sit where for these files, ignoring which recording MusicBrainz lists. Editions with the same title signature read identically.</summary>
        public string TitleSignature { get; set; } = string.Empty;
    }

    /// <summary>
    /// Pairs the files of an album folder with the tracks of one release and scores the fit. Files are a subset of the release in
    /// the normal case (partial albums), so missing tracks cost nothing; every file must get a distinct track.
    /// </summary>
    public static class ReleaseVerifier
    {
        private const double TitleWeight = 0.60;
        private const double NumberWeight = 0.20;
        private const double DiscWeight = 0.10;
        private const double DurationWeight = 0.10;

        private const double TracksShare = 0.65;
        private const double AlbumShare = 0.20;
        private const double ArtistShare = 0.15;

        public static ReleaseVerification Verify(ReleaseInfo release, IReadOnlyList<AlbumFile> files, double minFileScore = 0.5)
        {
            var slots = new List<(int Disc, RecordingInfo Track)>();
            foreach (var medium in release.Media)
            {
                foreach (var track in medium.Tracks) slots.Add((medium.Position, track));
            }

            var scores = new double[files.Count, slots.Count];
            for (int i = 0; i < files.Count; i++)
            {
                for (int j = 0; j < slots.Count; j++) scores[i, j] = TrackScore(files[i].Tags, slots[j].Disc, slots[j].Track);
            }

            var pairing = AssignmentSolver.MaximiseScore(scores);
            var assignments = new List<FileAssignment>();
            for (int i = 0; i < files.Count; i++)
            {
                int j = pairing[i];
                assignments.Add(j < 0
                    ? new FileAssignment { File = files[i], Score = 0 }
                    : new FileAssignment { File = files[i], Disc = slots[j].Disc, Track = slots[j].Track, Score = scores[i, j] });
            }

            double average = assignments.Count == 0 ? 0 : assignments.Average(a => a.Score);
            double lowest = assignments.Count == 0 ? 0 : assignments.Min(a => a.Score);
            int weak = assignments.Count(a => a.Track == null || a.Score < minFileScore);

            return new ReleaseVerification
            {
                Release = release,
                Assignments = assignments,
                TrackScore = average,
                MinFileScore = lowest,
                WeakFiles = weak,
                Score = ReleaseScore(release, files, average),
                TitleSignature = string.Join("|", assignments
                    .OrderBy(a => a.File.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(a => a.Track == null ? "-" : $"{a.Disc}-{a.Track.Position}:{TitleNormaliser.Normalise(a.Track.Title)}")),
                Signature = string.Join("|", assignments
                    .OrderBy(a => a.File.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(a => a.Track == null ? "-" : $"{a.Track.RecordingId}@{a.Disc}-{a.Track.Position}")),
            };
        }

        /// <summary>How well a file fits one track: the title, the track and disc numbers and the length, each only when both sides have it.</summary>
        public static double TrackScore(TrackTags file, int disc, RecordingInfo track)
        {
            double total = TitleWeight * TitleNormaliser.Similarity(file.Title, track.Title);
            double weight = TitleWeight;

            if (file.TrackNumber.HasValue && track.Position > 0)
            {
                total += NumberWeight * (file.TrackNumber.Value == track.Position ? 1 : 0);
                weight += NumberWeight;
            }
            if (file.DiscNumber.HasValue && disc > 0)
            {
                total += DiscWeight * (file.DiscNumber.Value == disc ? 1 : 0);
                weight += DiscWeight;
            }
            if (file.DurationMs.HasValue && track.LengthMs.HasValue)
            {
                double seconds = Math.Abs(file.DurationMs.Value - track.LengthMs.Value) / 1000.0;
                double closeness = seconds <= 3 ? 1 : seconds >= 15 ? 0 : (15 - seconds) / 12.0;
                total += DurationWeight * closeness;
                weight += DurationWeight;
            }
            return total / weight;
        }

        private static double ReleaseScore(ReleaseInfo release, IReadOnlyList<AlbumFile> files, double trackScore)
        {
            double total = TracksShare * trackScore;
            double weight = TracksShare;

            var album = MostCommon(files.Select(f => f.Tags.Album));
            if (album.Length > 0)
            {
                total += AlbumShare * TitleNormaliser.AlbumSimilarity(album, release.Title);
                weight += AlbumShare;
            }

            var artist = MostCommon(files.Select(f => f.Tags.AlbumArtist.Length > 0 ? f.Tags.AlbumArtist : f.Tags.Artist));
            if (artist.Length > 0 && release.ArtistCredit.Length > 0)
            {
                total += ArtistShare * TitleNormaliser.Similarity(artist, release.ArtistCredit);
                weight += ArtistShare;
            }
            return total / weight;
        }

        /// <summary>The value most files agree on (ignoring empty ones), or an empty string.</summary>
        public static string MostCommon(IEnumerable<string> values)
        {
            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .GroupBy(v => TitleNormaliser.Normalise(v))
                .OrderByDescending(g => g.Count())
                .Select(g => g.First().Trim())
                .FirstOrDefault() ?? string.Empty;
        }
    }
}
