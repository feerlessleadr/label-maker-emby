using System.Collections.Generic;

namespace LabelMaker.Core.Notifications
{
    /// <summary>
    /// The list of events with their titles, parameters, suggested rules and lock-screen texts, so the phone app can build its rule screen from the
    /// server (docs/NOTIFICATIONS.md section 5). Plain dictionaries and lists, so any JSON writer produces the same shape.
    /// </summary>
    public static class NotificationCatalog
    {
        public static Dictionary<string, object?> Build()
        {
            return new Dictionary<string, object?>
            {
                ["ApiLevel"] = EventNames.ApiLevel,
                ["Service"] = new Dictionary<string, object?>
                {
                    ["Id"] = "LabelMaker", ["Title"] = "Label Maker", ["ChannelId"] = "music-ingest", ["ChannelTitle"] = "Music ingest",
                },
                ["Events"] = new List<object>
                {
                    Entry(EventNames.NeedsReview, "Albums need review",
                        "Sent when downloaded albums could not be matched with certainty and wait for you.",
                        new List<object> { IntParam("MinCount", "When this many albums need review", 1, 500, 1) },
                        true, new Dictionary<string, object?> { ["MinCount"] = 1 },
                        "Music needs review. Unlock to see which.", "LabelMaker/Review"),
                    Entry(EventNames.AutoApplied, "An album was tagged automatically",
                        "Sent after a pass that tagged and filed albums by itself (or, in a dry run, would have). One summary per pass.",
                        new List<object>(), false, new Dictionary<string, object?>(),
                        "Music was filed. Unlock to see what.", "LabelMaker/Done"),
                    Entry(EventNames.AlbumFailed, "An album failed",
                        "Sent when an album could not be handled and needs fixing (a damaged file, a folder that cannot be written).",
                        new List<object>(), true, new Dictionary<string, object?>(),
                        "An album failed. Unlock to see which.", "LabelMaker/Review"),
                    Entry(EventNames.LaneProblem, "A lane has a problem",
                        "Sent when an inbox or library folder cannot be reached or written, the disk is nearly full, or the lane settings break a rule.",
                        new List<object>(), true, new Dictionary<string, object?>(),
                        "A music lane has a problem. Unlock to see which.", "LabelMaker/Summary"),
                    Entry(EventNames.ServiceTrouble, "Music lookups are stuck",
                        "Sent when MusicBrainz could not be reached for a while and albums are waiting.",
                        new List<object> { IntParam("Minutes", "After this many minutes", 15, 240, 15) },
                        false, new Dictionary<string, object?> { ["Minutes"] = 15 },
                        "Music lookups are stuck. Unlock for details.", "LabelMaker/Summary"),
                },
            };
        }

        private static Dictionary<string, object?> IntParam(string name, string label, int min, int max, int @default) => new Dictionary<string, object?>
        {
            ["Name"] = name, ["Label"] = label, ["Type"] = "int", ["Min"] = min, ["Max"] = max, ["Default"] = @default,
        };

        private static Dictionary<string, object?> Entry(string shortName, string title, string description, List<object> parameters, bool enabled,
            Dictionary<string, object?> defaultParams, string lockScreenText, string opensTab) => new Dictionary<string, object?>
        {
            ["Id"] = EventNames.Full(shortName),
            ["Title"] = title,
            ["Description"] = description,
            ["Params"] = parameters,
            ["DefaultRule"] = new Dictionary<string, object?> { ["Enabled"] = enabled, ["Params"] = defaultParams },
            ["LockScreenText"] = lockScreenText,
            ["OpensTab"] = opensTab,
        };
    }
}
