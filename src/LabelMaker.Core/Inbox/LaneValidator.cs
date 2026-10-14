using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LabelMaker.Core.Pipeline;

namespace LabelMaker.Core.Inbox
{
    /// <summary>One thing wrong with the lane settings. <see cref="Message"/> says what and how to fix it, in plain words.</summary>
    public sealed class LaneIssue
    {
        public string LaneId { get; set; } = string.Empty;

        /// <summary>A short stable code for the settings page to attach to a field: BadId, DuplicateId, NoName, NoInbox, NoLibrary, NotAbsolute, InboxIsLibrary, SharedInbox, InboxNesting, LibraryInsideInbox.</summary>
        public string Code { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Checks the lane list as a whole before it is saved (the rules in DESIGN 16.3). Pure text comparison, so it behaves the same on every system
    /// and works for Windows paths (drive letters and network shares) wherever the tests run. Paths compare without regard to case, the safe choice.
    /// </summary>
    public static class LaneValidator
    {
        private static readonly Regex SafeId = new Regex("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DriveRooted = new Regex(@"^[A-Za-z]:\\", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static IReadOnlyList<LaneIssue> Validate(IReadOnlyList<Lane> lanes)
        {
            var issues = new List<LaneIssue>();
            void Add(Lane lane, string code, string message) => issues.Add(new LaneIssue { LaneId = lane.Id, Code = code, Message = message });
            string Name(Lane l) => string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name;

            foreach (var lane in lanes)
            {
                var label = $"The lane \"{Name(lane)}\"";
                if (!SafeId.IsMatch(lane.Id ?? string.Empty)) Add(lane, "BadId", label + " needs an id of 1 to 40 letters, digits, dashes or underscores.");
                if (string.IsNullOrWhiteSpace(lane.Name)) Add(lane, "NoName", label + " needs a name.");
                if (string.IsNullOrWhiteSpace(lane.InboxPath)) Add(lane, "NoInbox", label + " needs an inbox folder.");
                else if (!IsAbsolute(lane.InboxPath)) Add(lane, "NotAbsolute", label + " has an inbox folder that is not a full path. Use a drive letter such as E:\\ or a network path such as \\\\server\\share.");
                if (string.IsNullOrWhiteSpace(lane.LibraryPath)) Add(lane, "NoLibrary", label + " needs a library folder.");
                else if (!IsAbsolute(lane.LibraryPath)) Add(lane, "NotAbsolute", label + " has a library folder that is not a full path. Use a drive letter such as E:\\ or a network path such as \\\\server\\share.");
            }

            foreach (var group in lanes.Where(l => !string.IsNullOrEmpty(l.Id)).GroupBy(l => l.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                foreach (var lane in group) Add(lane, "DuplicateId", $"Two lanes use the id \"{group.Key}\". Every lane needs its own.");
            }

            var usable = lanes.Where(l => IsAbsolute(l.InboxPath) && IsAbsolute(l.LibraryPath)).ToList();
            foreach (var lane in usable)
            {
                if (Related(Normalise(lane.InboxPath), Normalise(lane.LibraryPath)))
                {
                    Add(lane, "InboxIsLibrary", $"The inbox and the library folder of \"{Name(lane)}\" are the same folder or one is inside the other. Music in the library would be picked up again as new downloads. Use two separate folders.");
                }
            }

            for (int i = 0; i < usable.Count; i++)
            {
                for (int j = 0; j < usable.Count; j++)
                {
                    if (i == j) continue;
                    var a = usable[i];
                    var b = usable[j];
                    var aInbox = Normalise(a.InboxPath);
                    var bInbox = Normalise(b.InboxPath);

                    if (string.Equals(aInbox, bInbox, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(a, "SharedInbox", $"The lanes \"{Name(a)}\" and \"{Name(b)}\" use the same inbox. Each lane needs its own inbox, or albums would be filed in the wrong library.");
                    }
                    else if (Inside(bInbox, aInbox))
                    {
                        Add(b, "InboxNesting", $"The inbox of \"{Name(b)}\" is inside the inbox of \"{Name(a)}\", so its albums would be seen by both lanes. Move one of them.");
                    }

                    // A lane's inbox may not be inside, or hold, another lane's library folder (the same lane is reported above).
                    if (Related(aInbox, Normalise(b.LibraryPath)))
                    {
                        Add(a, "LibraryInsideInbox", $"The inbox of \"{Name(a)}\" and the library folder of \"{Name(b)}\" are the same folder or one is inside the other. Use separate folders.");
                    }
                }
            }

            return issues.GroupBy(i => (i.LaneId, i.Code, i.Message)).Select(g => g.First()).ToList();
        }

        /// <summary>The reason a lane may not be removed right now, or null. Albums waiting in review for a removed lane are kept (the screen offers another lane).</summary>
        public static string? RemovalBlockedReason(string laneId, IEnumerable<QueueItem> queue)
        {
            var busy = queue.Count(i => i.LaneId == laneId && (i.State == QueueState.Applying || i.State == QueueState.Matching));
            return busy > 0
                ? $"This lane cannot be removed while {busy} album(s) are being checked or filed. Try again in a minute."
                : null;
        }

        public static bool IsAbsolute(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var p = path.Trim();
            return p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)
                || DriveRooted.IsMatch(p.Replace('/', '\\')) || p.StartsWith("/", StringComparison.Ordinal);
        }

        /// <summary>Backslashes only, no trailing slash and no doubled separators (a leading double backslash for a network path stays).</summary>
        internal static string Normalise(string path)
        {
            var p = path.Trim().Replace('/', '\\');
            var network = p.StartsWith(@"\\", StringComparison.Ordinal);
            p = Regex.Replace(p, @"\\+", @"\");
            if (network) p = @"\" + p;
            return DriveRooted.IsMatch(p + "\\") && p.Length <= 3 ? p : p.TrimEnd('\\');
        }

        private static bool Inside(string child, string parent) => child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private static bool Related(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || Inside(a, b) || Inside(b, a);
    }
}
