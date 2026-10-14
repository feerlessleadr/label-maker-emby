using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LabelMaker.Core.MusicBrainz
{
    /// <summary>MusicBrainz kept answering "503 Service Unavailable" after the retries ran out, or could not be reached. Nothing is matched from a partial answer.</summary>
    public sealed class MusicBrainzUnavailableException : Exception
    {
        public MusicBrainzUnavailableException(string message) : base(message) { }
    }

    public sealed class ReleaseInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ReleaseGroupId { get; set; } = string.Empty;
        public string ArtistCredit { get; set; } = string.Empty;
        public List<MediumInfo> Media { get; } = new List<MediumInfo>();
    }

    public sealed class MediumInfo
    {
        public int Position { get; set; }
        public string Format { get; set; } = string.Empty;
        public List<RecordingInfo> Tracks { get; } = new List<RecordingInfo>();
    }

    public sealed class RecordingInfo
    {
        /// <summary>The track's position on its medium (1, 2, 3 ...).</summary>
        public int Position { get; set; }

        /// <summary>The track number as printed (can be "A1" on vinyl, so kept as text).</summary>
        public string Number { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>Track length in milliseconds, or null when MusicBrainz has none.</summary>
        public int? LengthMs { get; set; }

        /// <summary>The artist credited on this track (differs from the album artist on compilations).</summary>
        public string ArtistCredit { get; set; } = string.Empty;

        /// <summary>The recording's MusicBrainz id.</summary>
        public string RecordingId { get; set; } = string.Empty;
    }

    /// <summary>One result of a release-group search.</summary>
    public sealed class ReleaseGroupHit
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ArtistCredit { get; set; } = string.Empty;
        public string PrimaryType { get; set; } = string.Empty;
        public List<string> SecondaryTypes { get; } = new List<string>();

        /// <summary>MusicBrainz's own search score, 0 to 100. Many editions of one album all score 100, so it cannot pick an edition.</summary>
        public int Score { get; set; }
    }

    /// <summary>
    /// How an album is searched. The exact "album AND artist" query is the most precise but often finds nothing (artist names
    /// are stored in many spellings), so the matcher falls back to the looser shapes and checks the artist itself.
    /// </summary>
    public enum SearchShape
    {
        /// <summary>The album name as an exact phrase and the artist name as an exact phrase.</summary>
        QuotedAlbumAndArtist,

        /// <summary>Only the album name, as an exact phrase. The caller filters the answers by artist.</summary>
        QuotedAlbumOnly,

        /// <summary>The album and artist words with all punctuation removed, matched loosely.</summary>
        PlainWords,

        /// <summary>
        /// Every album by the artist (the album name is not searched). The last resort for titles no text search can reach, such as
        /// "G I R L" for "Girl"; the caller picks the album whose name is closest.
        /// </summary>
        ArtistAlbums,
    }

    /// <summary>What the matcher needs from MusicBrainz. The tests use a fake; the plugin uses <see cref="MusicBrainzClient"/>.</summary>
    public interface IMusicBrainz
    {
        Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, CancellationToken cancellation = default);

        Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, CancellationToken cancellation = default);

        /// <summary>The official releases of one release group, with their track lists.</summary>
        Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, CancellationToken cancellation = default);
    }

    /// <summary>
    /// Reads releases from the MusicBrainz web service. One request at a time, at least <see cref="MinInterval"/> apart; a
    /// "503" or "429" is retried with the server's Retry-After (capped) or an exponential back-off. A User-Agent with a contact is
    /// required, as MusicBrainz asks, and the client refuses to run without one.
    /// </summary>
    public sealed class MusicBrainzClient : IMusicBrainz
    {
        public const string DefaultBaseUrl = "https://musicbrainz.org/ws/2";

        private static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(120);

        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly SemaphoreSlim _turn = new SemaphoreSlim(1, 1);
        private DateTime _lastRequest = DateTime.MinValue;

        public TimeSpan MinInterval { get; set; } = DefaultMinInterval;

        /// <summary>How many times a busy answer is retried before the lookup gives up.</summary>
        public int MaxRetries { get; set; } = 4;

        public MusicBrainzClient(HttpMessageHandler handler, string userAgent, string baseUrl = DefaultBaseUrl)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            // Needs "name/version" and a contact: an email address or a web address.
            bool hasVersion = !string.IsNullOrWhiteSpace(userAgent) && userAgent.IndexOf('/') > 0;
            bool hasContact = !string.IsNullOrWhiteSpace(userAgent) &&
                (userAgent.IndexOf('@') >= 0 || userAgent.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hasVersion || !hasContact)
            {
                throw new ArgumentException("MusicBrainz asks for a User-Agent with a name, a version and a contact (an email or a web address).", nameof(userAgent));
            }
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient(handler, disposeHandler: false);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        /// <summary>
        /// Looks up one release by id with its recordings. Returns null when MusicBrainz has no such release (404) or the id is
        /// not a valid MusicBrainz id. Throws <see cref="MusicBrainzUnavailableException"/> when it stays busy.
        /// </summary>
        public async Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, CancellationToken cancellation = default)
        {
            var id = (releaseId ?? string.Empty).Trim().ToLowerInvariant();
            if (!Guid.TryParse(id, out _)) return null;

            var url = $"{_baseUrl}/release/{id}?inc=recordings+artist-credits+media+release-groups&fmt=json";
            var body = await GetWithRetriesAsync(url, cancellation).ConfigureAwait(false);
            return body == null ? null : ParseRelease(body);
        }

        public async Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, CancellationToken cancellation = default)
        {
            var cleanAlbum = CleanForQuery(album);
            var cleanArtist = CleanForQuery(artist);
            if (cleanAlbum.Length == 0 && shape != SearchShape.ArtistAlbums) return Array.Empty<ReleaseGroupHit>();

            string query;
            switch (shape)
            {
                case SearchShape.QuotedAlbumOnly:
                    query = "releasegroup:\"" + LuceneEscape(cleanAlbum) + "\"";
                    break;
                case SearchShape.PlainWords:
                    query = (Words(cleanAlbum) + " " + Words(cleanArtist)).Trim();
                    break;
                case SearchShape.ArtistAlbums:
                    if (cleanArtist.Length == 0) return Array.Empty<ReleaseGroupHit>();
                    query = "artist:\"" + LuceneEscape(cleanArtist) + "\" AND primarytype:album";
                    break;
                default:
                    query = "releasegroup:\"" + LuceneEscape(cleanAlbum) + "\"";
                    if (cleanArtist.Length > 0) query += " AND artist:\"" + LuceneEscape(cleanArtist) + "\"";
                    break;
            }
            var url = $"{_baseUrl}/release-group?query={Uri.EscapeDataString(query)}&fmt=json&limit={Math.Max(1, Math.Min(limit, 25))}";
            var body = await GetWithRetriesAsync(url, cancellation).ConfigureAwait(false);
            return body == null ? Array.Empty<ReleaseGroupHit>() : ParseReleaseGroupSearch(body);
        }

        public async Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, CancellationToken cancellation = default)
        {
            var id = (releaseGroupId ?? string.Empty).Trim().ToLowerInvariant();
            if (!Guid.TryParse(id, out _)) return Array.Empty<ReleaseInfo>();
            var url = $"{_baseUrl}/release?release-group={id}&status=official&inc=recordings+artist-credits+release-groups&limit=100&fmt=json";
            var body = await GetWithRetriesAsync(url, cancellation).ConfigureAwait(false);
            return body == null ? Array.Empty<ReleaseInfo>() : ParseReleaseList(body);
        }

        /// <summary>Reads a release from a JSON body (also used by the tests with a recorded answer).</summary>
        public static ReleaseInfo ParseRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return ParseReleaseElement(doc.RootElement);
        }

        /// <summary>Reads the "releases" list of a browse answer.</summary>
        public static IReadOnlyList<ReleaseInfo> ParseReleaseList(string json)
        {
            var list = new List<ReleaseInfo>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in releases.EnumerateArray()) list.Add(ParseReleaseElement(r));
            }
            return list;
        }

        public static IReadOnlyList<ReleaseGroupHit> ParseReleaseGroupSearch(string json)
        {
            var list = new List<ReleaseGroupHit>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("release-groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in groups.EnumerateArray())
                {
                    var hit = new ReleaseGroupHit
                    {
                        Id = Text(g, "id"),
                        Title = Text(g, "title"),
                        ArtistCredit = Credit(g),
                        PrimaryType = Text(g, "primary-type"),
                        Score = Int(g, "score") ?? 0,
                    };
                    if (g.TryGetProperty("secondary-types", out var secondary) && secondary.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in secondary.EnumerateArray())
                        {
                            if (s.ValueKind == JsonValueKind.String) hit.SecondaryTypes.Add(s.GetString() ?? string.Empty);
                        }
                    }
                    list.Add(hit);
                }
            }
            return list;
        }

        private static ReleaseInfo ParseReleaseElement(JsonElement root)
        {
            var release = new ReleaseInfo
            {
                Id = Text(root, "id"),
                Title = Text(root, "title"),
                Date = Text(root, "date"),
                Country = Text(root, "country"),
                Status = Text(root, "status"),
                ArtistCredit = Credit(root),
            };
            if (root.TryGetProperty("release-group", out var group) && group.ValueKind == JsonValueKind.Object)
            {
                release.ReleaseGroupId = Text(group, "id");
            }
            if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in media.EnumerateArray())
                {
                    var medium = new MediumInfo
                    {
                        Position = Int(m, "position") ?? 0,
                        Format = Text(m, "format"),
                    };
                    if (m.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tracks.EnumerateArray())
                        {
                            var recording = t.TryGetProperty("recording", out var rec) ? rec : default;
                            medium.Tracks.Add(new RecordingInfo
                            {
                                Position = Int(t, "position") ?? 0,
                                Number = Text(t, "number"),
                                Title = Text(t, "title"),
                                LengthMs = Int(t, "length"),
                                RecordingId = recording.ValueKind == JsonValueKind.Object ? Text(recording, "id") : string.Empty,
                                ArtistCredit = Credit(t).Length > 0 ? Credit(t) : (recording.ValueKind == JsonValueKind.Object ? Credit(recording) : string.Empty),
                            });
                        }
                    }
                    release.Media.Add(medium);
                }
            }
            return release;
        }

        /// <summary>
        /// Text as MusicBrainz's search expects it: every kind of dash becomes a plain hyphen and curly quotes become plain ones (a
        /// Unicode hyphen in "blink-182" made the search find nothing), control characters are dropped, spaces are collapsed.
        /// </summary>
        internal static string CleanForQuery(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var sb = new StringBuilder(text!.Length);
            foreach (var ch in text.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsControl(ch)) continue;
                if (ch == '\u2212' || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.DashPunctuation) sb.Append('-');
                else if (ch == '\u2018' || ch == '\u2019' || ch == '\u201B' || ch == '\u2032') sb.Append('\'');
                else if (ch == '\u201C' || ch == '\u201D' || ch == '\u201F') sb.Append('"');
                else sb.Append(ch);
            }
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>The letters and digits of a text with everything else turned into single spaces.</summary>
        internal static string Words(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>Escapes the characters that mean something inside a quoted Lucene phrase (backslash and double quote).</summary>
        internal static string LuceneEscape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

        private async Task<string?> GetWithRetriesAsync(string url, CancellationToken cancellation)
        {
            for (int attempt = 0; ; attempt++)
            {
                await WaitTurnAsync(cancellation).ConfigureAwait(false);
                HttpResponseMessage response;
                try
                {
                    response = await _http.GetAsync(url, cancellation).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    // Offline or a network blip: the album stays waiting and is tried again, never failed.
                    throw new MusicBrainzUnavailableException("MusicBrainz could not be reached: " + e.Message);
                }
                using var _ = response;
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                bool busy = response.StatusCode == (HttpStatusCode)503 || response.StatusCode == (HttpStatusCode)429;
                if (!busy) throw new MusicBrainzUnavailableException($"MusicBrainz answered {(int)response.StatusCode}.");
                if (attempt >= MaxRetries) throw new MusicBrainzUnavailableException("MusicBrainz stayed busy; the lookup will be tried again later.");

                // A server-supplied wait is honoured but capped, so one odd header cannot stall the queue for hours.
                var delay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                if (delay > MaxWait) delay = MaxWait;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                await Task.Delay(delay, cancellation).ConfigureAwait(false);
            }
        }

        private async Task WaitTurnAsync(CancellationToken cancellation)
        {
            await _turn.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var wait = _lastRequest + MinInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellation).ConfigureAwait(false);
                _lastRequest = DateTime.UtcNow;
            }
            finally
            {
                _turn.Release();
            }
        }

        private static string Text(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return string.Empty;
            return value.GetString() ?? string.Empty;
        }

        private static int? Int(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : (int?)null;
        }

        /// <summary>The credited artist names joined the way MusicBrainz joins them ("A & B").</summary>
        private static string Credit(JsonElement root)
        {
            if (!root.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array) return string.Empty;
            var sb = new StringBuilder();
            foreach (var c in credits.EnumerateArray())
            {
                sb.Append(Text(c, "name"));
                sb.Append(Text(c, "joinphrase"));
            }
            return sb.ToString().Trim();
        }
    }
}
