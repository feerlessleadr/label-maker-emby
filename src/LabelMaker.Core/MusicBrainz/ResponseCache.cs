using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LabelMaker.Core.MusicBrainz
{
    /// <summary>Remembers MusicBrainz answers so the same question is never asked twice within its shelf life.</summary>
    public interface IResponseCache
    {
        /// <summary>The stored answer for this request address (200 with a body, or 404 with none), or false when there is none or it has expired.</summary>
        bool TryGet(string url, out HttpStatusCode status, out string body);

        /// <summary>Stores an answer. Only answers worth keeping are offered: success and "not found".</summary>
        void Put(string url, HttpStatusCode status, string body);
    }

    /// <summary>
    /// The cache on disk: one small file per request address, written atomically, so it survives restarts and a crash never leaves a half
    /// file (an unreadable file simply counts as a miss). How long an answer is trusted depends on what was asked: a release's own data rarely
    /// changes, the list of editions of an album changes now and then, search results change most, and "not found" is rechecked daily.
    /// Expired files are removed when the cache opens, and the oldest go first when there are more than <see cref="MaxEntries"/>.
    /// </summary>
    public sealed class DiskResponseCache : IResponseCache
    {
        public static readonly TimeSpan ReleaseLife = TimeSpan.FromDays(30);
        public static readonly TimeSpan EditionsLife = TimeSpan.FromDays(3);
        public static readonly TimeSpan SearchLife = TimeSpan.FromDays(1);
        public static readonly TimeSpan NotFoundLife = TimeSpan.FromDays(1);

        private readonly string _folder;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();

        public int MaxEntries { get; }

        public DiskResponseCache(string folder, Func<DateTime>? utcNow = null, int maxEntries = 20000)
        {
            MaxEntries = maxEntries;
            _folder = folder;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            Directory.CreateDirectory(folder);
            Purge();
        }

        public bool TryGet(string url, out HttpStatusCode status, out string body)
        {
            status = HttpStatusCode.OK;
            body = string.Empty;
            var path = PathFor(url);
            try
            {
                if (!File.Exists(path)) return false;
                var text = File.ReadAllText(path, Encoding.UTF8);
                int newline = text.IndexOf('\n');
                if (newline < 0) return false;
                var head = text.Substring(0, newline).Split('|');
                if (head.Length != 2 || !int.TryParse(head[0], out var code) || !long.TryParse(head[1], out var stored)) return false;
                var storedAt = DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(stored), DateTimeKind.Utc);
                var code2 = (HttpStatusCode)code;
                if (_utcNow() - storedAt > LifeOf(url, code2)) return false;
                status = code2;
                body = text.Substring(newline + 1);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Put(string url, HttpStatusCode status, string body)
        {
            if (status != HttpStatusCode.OK && status != HttpStatusCode.NotFound) return;
            var path = PathFor(url);
            var seconds = (long)(_utcNow() - DateTime.UnixEpoch).TotalSeconds;
            try
            {
                lock (_lock)
                {
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, (int)status + "|" + seconds + "\n" + body, new UTF8Encoding(false));
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temp, path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // A cache that cannot be written is only slower, never wrong.
            }
        }

        /// <summary>How long an answer to this request may be reused.</summary>
        internal static TimeSpan LifeOf(string url, HttpStatusCode status)
        {
            if (status == HttpStatusCode.NotFound) return NotFoundLife;
            if (url.Contains("/release-group?")) return SearchLife;
            if (url.Contains("/release?release-group=")) return EditionsLife;
            return ReleaseLife;
        }

        private void Purge()
        {
            try
            {
                var files = new DirectoryInfo(_folder).GetFiles("*.mbc");
                var now = _utcNow();
                // The longest life any answer has: anything older than that is certainly expired.
                foreach (var file in files.Where(f => now - f.LastWriteTimeUtc > ReleaseLife))
                {
                    try { file.Delete(); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
                var left = files.Where(f => f.Exists).OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxEntries).ToList();
                foreach (var file in left)
                {
                    try { file.Delete(); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
                foreach (var temp in new DirectoryInfo(_folder).GetFiles("*.tmp"))
                {
                    try { temp.Delete(); } catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        private string PathFor(string url)
        {
            using var sha = SHA256.Create();
            var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", string.Empty).ToLowerInvariant();
            return Path.Combine(_folder, hash + ".mbc");
        }
    }
}
