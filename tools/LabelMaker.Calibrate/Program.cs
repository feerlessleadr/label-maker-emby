using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.MusicBrainz;

namespace LabelMaker.Calibrate
{
    /// <summary>
    /// Calibration: runs the real matcher (live MusicBrainz, answers cached on disk) over albums exported from a library whose
    /// tags are already correct, and checks each result against the MusicBrainz release id in the tags. Reports precision and the
    /// share applied automatically at each score threshold. Usage:
    ///   dotnet run -- --cache &lt;dir&gt; --out &lt;report.md&gt; [--limit N] export1.json export2.json ...
    /// </summary>
    internal static class Program
    {
        private sealed class Album
        {
            public string Source = string.Empty;
            public string Folder = string.Empty;
            public List<TrackTags> Tracks = new List<TrackTags>();
            public List<string> Names = new List<string>();
            public string TruthId = string.Empty;
            public bool MixedIds;
        }

        private sealed class Row
        {
            public string Source = string.Empty;
            public string Folder = string.Empty;
            public string Scenario = string.Empty;
            public int Files;
            public string Outcome = string.Empty;
            public double Score;
            public bool Correct;
            public string Reason = string.Empty;
            public string BestId = string.Empty;
            public string BestTitle = string.Empty;
            public string TruthTitle = string.Empty;
            public string TruthStatus = string.Empty;
            public string Error = string.Empty;
            public string Artist = string.Empty;
            public string AlbumName = string.Empty;
        }

        private static async Task<int> Main(string[] args)
        {
            string cache = Path.Combine(Directory.GetCurrentDirectory(), "mbcache");
            string report = Path.Combine(Directory.GetCurrentDirectory(), "calibration-report.md");
            int limit = int.MaxValue;
            var inputs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--cache") cache = args[++i];
                else if (args[i] == "--out") report = args[++i];
                else if (args[i] == "--limit") limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else inputs.Add(args[i]);
            }
            if (inputs.Count == 0) { Console.Error.WriteLine("Give at least one export JSON file."); return 2; }

            var albums = inputs.SelectMany(LoadAlbums).ToList();
            var answered = albums.Where(a => a.TruthId.Length > 0).Take(limit).ToList();
            Console.WriteLine($"{albums.Count} albums loaded; {answered.Count} have one consistent MusicBrainz release id ({albums.Count(a => a.MixedIds)} mixed ids, {albums.Count(a => a.TruthId.Length == 0 && !a.MixedIds)} with none).");

            var handler = new CachingHandler(new HttpClientHandler(), cache, TimeSpan.FromMilliseconds(1100));
            var mb = new MusicBrainzClient(handler, "LabelMaker-calibrate/0.1 (feerlessleadr@users.noreply.github.com)") { MinInterval = TimeSpan.Zero, MaxRetries = 6 };
            var matcher = new AlbumMatcher(mb, new MatchSettings { AutoThreshold = 0.5 });

            var scenarios = new (string Name, Func<TrackTags, TrackTags> Change)[]
            {
                ("tags as they are", t => t),
                ("album name with [FLAC] noise", t => Clone(t, album: t.Album + " [FLAC]")),
            };

            var truthCache = new Dictionary<string, ReleaseInfo?>();
            var rows = new List<Row>();
            int done = 0;
            foreach (var album in answered)
            {
                done++;
                if (done % 10 == 0) Console.WriteLine($"  {done} of {answered.Count} albums  (network {handler.NetworkCalls}, cached {handler.CacheHits}, rows {rows.Count})");
                var subset = Subset(album);
                foreach (var scenario in scenarios)
                {
                    var files = subset.Select(i => new AlbumFile(album.Names[i], scenario.Change(album.Tracks[i]))).ToList();
                    var row = new Row
                    {
                        Source = album.Source, Folder = album.Folder, Scenario = scenario.Name, Files = files.Count,
                        Artist = ReleaseVerifier.MostCommon(album.Tracks.Select(t => t.AlbumArtist.Length > 0 ? t.AlbumArtist : t.Artist)),
                        AlbumName = ReleaseVerifier.MostCommon(album.Tracks.Select(t => t.Album)),
                    };
                    try
                    {
                        var result = await matcher.MatchAsync(files, null);
                        row.Outcome = result.Outcome.ToString();
                        row.Score = result.Best?.Score ?? 0;
                        row.Reason = result.Reason;
                        row.BestId = result.Best?.Release.Id ?? string.Empty;
                        row.BestTitle = result.Best?.Release.Title ?? string.Empty;

                        if (!truthCache.TryGetValue(album.TruthId, out var truth)) { truth = await mb.LookupReleaseAsync(album.TruthId); truthCache[album.TruthId] = truth; }
                        row.TruthTitle = truth?.Title ?? "(release not found)";
                        row.TruthStatus = truth?.Status ?? string.Empty;
                        if (result.Best != null)
                        {
                            if (string.Equals(result.Best.Release.Id, album.TruthId, StringComparison.OrdinalIgnoreCase)) row.Correct = true;
                            else if (truth != null)
                            {
                                var truthFit = ReleaseVerifier.Verify(truth, files);
                                row.Correct = truthFit.WeakFiles == 0 && truthFit.Signature == result.Best.Signature;
                            }
                        }
                    }
                    catch (MusicBrainzUnavailableException e)
                    {
                        row.Outcome = "Error";
                        row.Error = e.Message;
                    }
                    rows.Add(row);
                }
            }

            File.WriteAllText(report, BuildReport(rows, albums, handler), new UTF8Encoding(false));
            Console.WriteLine($"Report written to {report} (network {handler.NetworkCalls}, cached {handler.CacheHits}).");
            return 0;
        }

        // ---- loading -----------------------------------------------------------------------------------

        private static IEnumerable<Album> LoadAlbums(string path)
        {
            var source = Path.GetFileNameWithoutExtension(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var a in doc.RootElement.GetProperty("albums").EnumerateArray())
            {
                var album = new Album { Source = source, Folder = Str(a, "folder") };
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in a.GetProperty("tracks").EnumerateArray())
                {
                    album.Names.Add(Str(t, "file"));
                    album.Tracks.Add(new TrackTags
                    {
                        Title = Str(t, "title"), Artist = Str(t, "artist"), AlbumArtist = Str(t, "albumArtist"), Album = Str(t, "album"),
                        TrackNumber = Num(t, "track"), TotalTracks = Num(t, "trackCount"), DiscNumber = Num(t, "disc"), TotalDiscs = Num(t, "discCount"),
                        Year = Num(t, "year"), DurationMs = Num(t, "durationMs"),
                    });
                    var id = Str(t, "releaseMbid").Trim().ToLowerInvariant();
                    if (Guid.TryParse(id, out _)) ids.Add(id);
                }
                if (ids.Count == 1) album.TruthId = ids.First();
                album.MixedIds = ids.Count > 1;
                yield return album;
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        private static int? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : (int?)null;

        private static TrackTags Clone(TrackTags t, string? album = null) => new TrackTags
        {
            Title = t.Title, Artist = t.Artist, AlbumArtist = t.AlbumArtist, Album = album ?? t.Album, TrackNumber = t.TrackNumber,
            TotalTracks = t.TotalTracks, DiscNumber = t.DiscNumber, TotalDiscs = t.TotalDiscs, Year = t.Year, DurationMs = t.DurationMs,
        };

        /// <summary>The files used: all of them for a small album, otherwise 1 to 6 chosen by a repeatable random draw (downloads arrive in pieces).</summary>
        private static List<int> Subset(Album album)
        {
            var all = Enumerable.Range(0, album.Tracks.Count).ToList();
            if (all.Count <= 6) return all;
            var rng = new Random(StableHash(album.Source + "|" + album.Folder));
            int size = rng.Next(1, 7);
            return all.OrderBy(_ => rng.Next()).Take(size).OrderBy(i => i).ToList();
        }

        private static int StableHash(string text)
        {
            unchecked { int h = 17; foreach (var c in text) h = h * 31 + c; return h; }
        }

        // ---- report ------------------------------------------------------------------------------------

        private static string BuildReport(List<Row> rows, List<Album> albums, CachingHandler handler)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Calibration report");
            sb.AppendLine();
            sb.AppendLine($"Albums exported: {albums.Count}. With one consistent MusicBrainz release id (the answer key): {albums.Count(a => a.TruthId.Length > 0)}. " +
                          $"Mixed ids: {albums.Count(a => a.MixedIds)}. No id: {albums.Count(a => a.TruthId.Length == 0 && !a.MixedIds)}. Rows below are album x scenario.");
            sb.AppendLine();
            foreach (var scenario in rows.Select(r => r.Scenario).Distinct())
            {
                var set = rows.Where(r => r.Scenario == scenario).ToList();
                sb.AppendLine($"## Scenario: {scenario} ({set.Count} albums)");
                sb.AppendLine();
                sb.AppendLine("| Outcome | Albums | Best match correct |");
                sb.AppendLine("|---|---|---|");
                foreach (var g in set.GroupBy(r => r.Outcome).OrderBy(g => g.Key))
                {
                    sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(r => r.Correct)} |");
                }
                sb.AppendLine();
                sb.AppendLine("Precision of automatic applying, by minimum score (applied = outcome Auto and score at or above the threshold):");
                sb.AppendLine();
                sb.AppendLine("| Threshold | Applied | Share of albums | Correct | Wrong | Precision |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var tau in new[] { 0.60, 0.70, 0.80, 0.85, 0.90, 0.92, 0.94, 0.96, 0.98 })
                {
                    var applied = set.Where(r => r.Outcome == "Auto" && r.Score >= tau).ToList();
                    int right = applied.Count(r => r.Correct);
                    string precision = applied.Count == 0 ? "n/a" : (100.0 * right / applied.Count).ToString("0.0", CultureInfo.InvariantCulture) + "%";
                    sb.AppendLine($"| {tau:0.00} | {applied.Count} | {(100.0 * applied.Count / Math.Max(1, set.Count)).ToString("0", CultureInfo.InvariantCulture)}% | {right} | {applied.Count - right} | {precision} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Wrong automatic matches (the ones that matter most)");
            sb.AppendLine();
            var wrong = rows.Where(r => r.Outcome == "Auto" && !r.Correct).OrderByDescending(r => r.Score).ToList();
            if (wrong.Count == 0) sb.AppendLine("None.");
            foreach (var r in wrong)
            {
                sb.AppendLine($"- [{r.Scenario}] score {r.Score:0.000}: \"{r.Artist} - {r.AlbumName}\" ({r.Files} file(s)) matched \"{r.BestTitle}\" {r.BestId}; the library says \"{r.TruthTitle}\" ({r.TruthStatus}). {r.Reason}");
            }
            sb.AppendLine();

            sb.AppendLine("## Not matched at all, tags as they are (first 60)");
            sb.AppendLine();
            foreach (var r in rows.Where(r => r.Scenario == "tags as they are" && r.Outcome == "NoMatch").Take(60))
            {
                sb.AppendLine($"- \"{r.Artist} - {r.AlbumName}\" ({r.Files} file(s), library release {r.TruthStatus}): {r.Reason}");
            }
            sb.AppendLine();

            sb.AppendLine("## Sent to review where the best match was right, tags as they are (first 40)");
            sb.AppendLine();
            foreach (var r in rows.Where(r => r.Scenario == "tags as they are" && r.Outcome == "NeedsReview" && r.Correct).Take(40))
            {
                sb.AppendLine($"- \"{r.Artist} - {r.AlbumName}\" ({r.Files} file(s), score {r.Score:0.000}): {r.Reason}");
            }
            sb.AppendLine();

            var errors = rows.Where(r => r.Outcome == "Error").ToList();
            sb.AppendLine($"## Errors (MusicBrainz busy or unreachable): {errors.Count}");
            foreach (var r in errors.Take(20)) sb.AppendLine($"- \"{r.Artist} - {r.AlbumName}\": {r.Error}");
            sb.AppendLine();
            sb.AppendLine($"Requests this run: {handler.NetworkCalls} sent, {handler.CacheHits} answered from the cache.");
            return sb.ToString();
        }
    }
}
