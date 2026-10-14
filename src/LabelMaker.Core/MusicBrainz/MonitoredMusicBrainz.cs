using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LabelMaker.Core.MusicBrainz
{
    /// <summary>
    /// Wraps a MusicBrainz source and notes when it stops answering and when it answers again, so the plugin can say "music lookups are stuck"
    /// (a "not found" is an answer, a busy or unreachable service is not).
    /// </summary>
    public sealed class MonitoredMusicBrainz : IMusicBrainz
    {
        private readonly IMusicBrainz _inner;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();
        private DateTime? _troubleSince;

        public MonitoredMusicBrainz(IMusicBrainz inner, Func<DateTime>? utcNow = null)
        {
            _inner = inner;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>When the service started failing (and has not answered since), or null while it works.</summary>
        public DateTime? TroubleSince
        {
            get { lock (_lock) return _troubleSince; }
        }

        public Task<ReleaseInfo?> LookupReleaseAsync(string releaseId, CancellationToken cancellation = default) =>
            Watch(() => _inner.LookupReleaseAsync(releaseId, cancellation));

        public Task<IReadOnlyList<ReleaseGroupHit>> SearchReleaseGroupsAsync(string artist, string album, int limit, SearchShape shape, CancellationToken cancellation = default) =>
            Watch(() => _inner.SearchReleaseGroupsAsync(artist, album, limit, shape, cancellation));

        public Task<IReadOnlyList<ReleaseInfo>> BrowseOfficialReleasesAsync(string releaseGroupId, CancellationToken cancellation = default) =>
            Watch(() => _inner.BrowseOfficialReleasesAsync(releaseGroupId, cancellation));

        private async Task<T> Watch<T>(Func<Task<T>> call)
        {
            try
            {
                var result = await call().ConfigureAwait(false);
                lock (_lock) _troubleSince = null;
                return result;
            }
            catch (MusicBrainzUnavailableException)
            {
                lock (_lock) { if (_troubleSince == null) _troubleSince = _utcNow(); }
                throw;
            }
        }
    }
}
