using System;
using System.Collections.Generic;
using LabelMaker.Core.Pipeline;

namespace LabelMaker.Core.Inbox
{
    /// <summary>
    /// Decides when a download has finished arriving. A unit counts as finished when its fingerprint (names, sizes and times of every file)
    /// has not changed for the stable time, it has no temporary download file, and every audio file can be opened (nothing is still
    /// writing to it). This is the real trigger: file-system notifications are unreliable over network shares and Docker bind mounts, so
    /// the worker simply looks again on every pass.
    /// </summary>
    public sealed class StabilityTracker
    {
        private sealed class Seen
        {
            public string Fingerprint = string.Empty;
            public DateTime SinceUtc;
        }

        private readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>();

        /// <summary>Records what the scan found and says whether the unit is finished.</summary>
        public bool IsFinished(AlbumUnit unit, DateTime nowUtc, TimeSpan stableFor, IFileOps ops)
        {
            if (!_seen.TryGetValue(unit.Id, out var seen) || seen.Fingerprint != unit.Fingerprint)
            {
                _seen[unit.Id] = new Seen { Fingerprint = unit.Fingerprint, SinceUtc = nowUtc };
                return false;
            }
            if (unit.HasTemporaryFiles) return false;
            if (nowUtc - seen.SinceUtc < stableFor) return false;
            foreach (var file in unit.Files)
            {
                if (!ops.CanOpenForRead(file)) return false;
            }
            return true;
        }

        /// <summary>Forgets units that are no longer in the inbox.</summary>
        public void Keep(IReadOnlyCollection<string> unitIds)
        {
            var keep = new HashSet<string>(unitIds);
            foreach (var id in new List<string>(_seen.Keys))
            {
                if (!keep.Contains(id)) _seen.Remove(id);
            }
        }
    }
}
