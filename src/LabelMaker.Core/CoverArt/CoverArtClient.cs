using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LabelMaker.Core.CoverArt
{
    /// <summary>A front cover image as downloaded: its bytes and the file extension that fits them (".jpg" or ".png").</summary>
    public sealed class CoverImage
    {
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public string Extension { get; set; } = ".jpg";
    }

    public interface ICoverArt
    {
        /// <summary>The front cover of one MusicBrainz release, or null when the release has none or the answer could not be used. Never throws for a missing or broken image.</summary>
        Task<CoverImage?> GetFrontAsync(string releaseId, CancellationToken cancellation = default);
    }

    /// <summary>
    /// Reads the front cover of a release from the Cover Art Archive (coverartarchive.org). The only thing sent is the release's
    /// MusicBrainz id. The image is checked to really be a JPEG or a PNG of a sensible size before it is used.
    /// </summary>
    public sealed class CoverArtClient : ICoverArt
    {
        public const string DefaultBaseUrl = "https://coverartarchive.org";
        public const long MaxBytes = 8 * 1024 * 1024;

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        /// <summary>The thumbnail width asked for: 250, 500 or 1200 (500 is what the owner's Picard uses).</summary>
        public int Size { get; set; } = 500;

        public CoverArtClient(HttpMessageHandler handler, string userAgent, string baseUrl = DefaultBaseUrl)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
            if (!string.IsNullOrWhiteSpace(userAgent)) _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        public async Task<CoverImage?> GetFrontAsync(string releaseId, CancellationToken cancellation = default)
        {
            var id = (releaseId ?? string.Empty).Trim().ToLowerInvariant();
            if (!Guid.TryParse(id, out _)) return null;
            int size = Size == 250 || Size == 1200 ? Size : 500;

            try
            {
                using var response = await _http.GetAsync($"{_baseUrl}/release/{id}/front-{size}", HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK) return null; // 404 means the release has no front cover; anything else is not worth failing an album over
                if (response.Content.Headers.ContentLength > MaxBytes) return null;

                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellation).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxBytes) return null;
                }
                return Identify(buffer.ToArray());
            }
            catch (Exception e) when (e is HttpRequestException || e is IOException || (e is TaskCanceledException && !cancellation.IsCancellationRequested))
            {
                return null;
            }
        }

        /// <summary>A JPEG or PNG judged by its first bytes (the server's content type is not trusted), or null.</summary>
        internal static CoverImage? Identify(byte[] bytes)
        {
            if (bytes.Length < 1024) return null;
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return new CoverImage { Bytes = bytes, Extension = ".jpg" };
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return new CoverImage { Bytes = bytes, Extension = ".png" };
            return null;
        }
    }
}
