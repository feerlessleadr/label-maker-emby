using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Notifications;
using LabelMaker.Core.Pipeline;
using LabelMaker.Core.Tags;

namespace LabelMaker.Core.Engine
{
    /// <summary>Everything the engine needs from the plugin's saved settings, in Emby-free form.</summary>
    public sealed class EngineSettings
    {
        public List<Lane> Lanes { get; set; } = new List<Lane>();

        /// <summary>While true nothing is looked at or changed; only lane problems are still reported.</summary>
        public bool Paused { get; set; }

        /// <summary>True (the default) means nothing is changed automatically: albums that would be applied show as Ready.</summary>
        public bool DryRun { get; set; } = true;

        /// <summary>How long an album's files must stay unchanged before it counts as finished downloading.</summary>
        public int StableSeconds { get; set; } = 60;

        public int MaxPerPass { get; set; } = 20;

        /// <summary>How often the inbox is looked at.</summary>
        public int PassSeconds { get; set; } = 60;

        public double AutoThreshold { get; set; } = 0.92;
        public double ReviewThreshold { get; set; } = 0.60;
        public double MinFileScore { get; set; } = 0.5;
        public double Margin { get; set; } = 0.05;
        public int MaxReleaseGroups { get; set; } = 5;

        /// <summary>False (the default): editions that list the same titles in the same places are interchangeable. True: they must also be the same recordings.</summary>
        public bool EditionsMustBeSameRecordings { get; set; }

        public List<string> PreferredCountries { get; set; } = new List<string> { "US", "XW", "GB" };

        /// <summary>A Picard file naming script; empty means Picard's stock script.</summary>
        public string NamingScript { get; set; } = string.Empty;

        public int Id3Version { get; set; } = 4;
        public bool WriteId3v1 { get; set; } = true;

        public NotificationSettings Notifications { get; set; } = new NotificationSettings();

        /// <summary>The account Label Maker runs as, named in preflight fixes. Empty leaves it out.</summary>
        public string AccountName { get; set; } = string.Empty;

        /// <summary>The first thing wrong with these settings, in plain words, or null when they can be saved. Lane rules are checked too.</summary>
        public string? Problem()
        {
            if (ReviewThreshold < 0.3 || ReviewThreshold > 0.95) return "The review threshold must be between 0.30 and 0.95.";
            if (AutoThreshold < 0.5 || AutoThreshold > 1.0 || AutoThreshold < ReviewThreshold) return "The automatic threshold must be between 0.50 and 1.00 and at least the review threshold.";
            if (MinFileScore < 0.1 || MinFileScore > 0.9) return "The minimum score per file must be between 0.10 and 0.90.";
            if (Margin < 0 || Margin > 0.3) return "The margin between editions must be between 0 and 0.30.";
            if (MaxReleaseGroups < 1 || MaxReleaseGroups > 10) return "The number of albums to check per search must be between 1 and 10.";
            if (StableSeconds < 10 || StableSeconds > 3600) return "The time files must stay unchanged must be between 10 and 3600 seconds.";
            if (MaxPerPass < 1 || MaxPerPass > 200) return "The number of albums handled per check must be between 1 and 200.";
            if (PassSeconds < 15 || PassSeconds > 3600) return "How often the inbox is checked must be between 15 and 3600 seconds.";
            if (Id3Version != 3 && Id3Version != 4) return "The ID3 version must be 3 or 4.";
            if (PreferredCountries.Any(c => c == null || c.Trim().Length != 2)) return "Preferred countries are two-letter codes such as US, GB or XW.";
            if (!string.IsNullOrWhiteSpace(NamingScript) && NamingScript.Length > 4000) return "The naming script is too long (at most 4000 characters).";
            var issue = LaneValidator.Validate(Lanes).FirstOrDefault();
            return issue?.Message;
        }


        /// <summary>A text that changes exactly when something that shapes matching, naming, tagging or the worker changes (not the lanes, pause or alerts).</summary>
        internal string BuildSignature() => JsonSerializer.Serialize(new
        {
            DryRun, StableSeconds, MaxPerPass, AutoThreshold, ReviewThreshold, MinFileScore, Margin, MaxReleaseGroups,
            EditionsMustBeSameRecordings, PreferredCountries, NamingScript, Id3Version, WriteId3v1,
        });

        internal MatchSettings ToMatchSettings()
        {
            var match = new MatchSettings
            {
                AutoThreshold = AutoThreshold, ReviewThreshold = ReviewThreshold, MinFileScore = MinFileScore, Margin = Margin,
                MaxReleaseGroups = MaxReleaseGroups,
                EditionRule = EditionsMustBeSameRecordings ? EditionRule.SameRecordings : EditionRule.SameTitles,
            };
            match.PreferredCountries.Clear();
            foreach (var country in PreferredCountries.Where(c => !string.IsNullOrWhiteSpace(c))) match.PreferredCountries.Add(country.Trim());
            return match;
        }

        internal PlannerOptions ToPlannerOptions() => string.IsNullOrWhiteSpace(NamingScript)
            ? new PlannerOptions()
            : new PlannerOptions { NamingScript = NamingScript };

        internal TagWriteOptions ToTagOptions() => new TagWriteOptions { Id3Version = (byte)(Id3Version == 3 ? 3 : 4), WriteId3v1 = WriteId3v1 };

        internal WorkerOptions ToWorkerOptions() => new WorkerOptions
        {
            DryRun = DryRun, StableFor = TimeSpan.FromSeconds(Math.Max(5, StableSeconds)), MaxPerPass = Math.Max(1, MaxPerPass),
        };
    }
}
