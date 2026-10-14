using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LabelMaker.Core.Inbox
{
    /// <summary>
    /// What MusicGrabber wrote in an album folder's hidden <c>.albuminfo</c> file. When the album came from a MusicBrainz link,
    /// <see cref="ReleaseMbid"/> names the exact release: the strongest hint available, verified against the files before use.
    /// </summary>
    public sealed class AlbumInfo
    {
        public string Artist { get; set; } = string.Empty;

        public string Album { get; set; } = string.Empty;

        /// <summary>A lower-case MusicBrainz release id, or empty when the file has none or it is not a valid id.</summary>
        public string ReleaseMbid { get; set; } = string.Empty;

        /// <summary>The year text as MusicGrabber wrote it (a pressing's year, which can differ from the original).</summary>
        public string Year { get; set; } = string.Empty;
    }

    public static class AlbumInfoReader
    {
        /// <summary>The largest file read; a real one is a few hundred bytes.</summary>
        public const int MaxBytes = 64 * 1024;

        private static readonly Regex GuidPattern = new Regex(
            "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Reads the file. Returns null when it is missing, too large or not JSON; never throws on bad content.</summary>
        public static AlbumInfo? Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxBytes) return null;
                return Parse(File.ReadAllText(path));
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static AlbumInfo? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                var root = doc.RootElement;
                var mbid = Text(root, "release_mbid").Trim().ToLowerInvariant();
                return new AlbumInfo
                {
                    Artist = Text(root, "artist"),
                    Album = Text(root, "album"),
                    ReleaseMbid = GuidPattern.IsMatch(mbid) ? mbid : string.Empty,
                    Year = Text(root, "year"),
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Text(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        }
    }
}
