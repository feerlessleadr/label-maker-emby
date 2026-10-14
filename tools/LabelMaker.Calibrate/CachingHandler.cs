using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LabelMaker.Calibrate
{
    /// <summary>
    /// Stores every successful (or "not found") MusicBrainz answer on disk by URL, so a calibration rerun costs no requests, and spaces
    /// the requests that do go out by at least <see cref="_spacing"/>. Busy answers (503, 429) are never stored.
    /// </summary>
    public sealed class CachingHandler : DelegatingHandler
    {
        private readonly string _folder;
        private readonly TimeSpan _spacing;
        private DateTime _lastNetwork = DateTime.MinValue;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public int NetworkCalls { get; private set; }
        public int CacheHits { get; private set; }

        public CachingHandler(HttpMessageHandler inner, string folder, TimeSpan spacing) : base(inner)
        {
            _folder = folder;
            _spacing = spacing;
            Directory.CreateDirectory(folder);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Path.Combine(_folder, Hash(request.RequestUri!.ToString()) + ".txt");
            if (File.Exists(path))
            {
                var lines = File.ReadAllText(path, Encoding.UTF8);
                int newline = lines.IndexOf('\n');
                var status = (HttpStatusCode)int.Parse(lines.Substring(0, newline).Trim());
                CacheHits++;
                return new HttpResponseMessage(status) { Content = new StringContent(lines.Substring(newline + 1), Encoding.UTF8, "application/json") };
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wait = _lastNetwork + _spacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _lastNetwork = DateTime.UtcNow;
                NetworkCalls++;
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    File.WriteAllText(path, (int)response.StatusCode + "\n" + body, new UTF8Encoding(false));
                    response.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }
                return response;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
