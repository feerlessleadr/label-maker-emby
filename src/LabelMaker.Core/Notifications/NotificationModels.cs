using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LabelMaker.Core.Notifications
{
    /// <summary>The message names the phone app listens for (docs/NOTIFICATIONS.md section 4).</summary>
    public static class EventNames
    {
        public const string Prefix = "LabelMaker.";
        public const string NeedsReview = "NeedsReview";
        public const string AutoApplied = "AutoApplied";
        public const string AlbumFailed = "AlbumFailed";
        public const string LaneProblem = "LaneProblem";
        public const string LaneRecovered = "LaneRecovered";
        public const string ServiceTrouble = "ServiceTrouble";
        public const string ServiceRecovered = "ServiceRecovered";
        public const string PassFinished = "PassFinished";

        /// <summary>The level of the notification contract; grows only when a field is removed or changes meaning.</summary>
        public const int ApiLevel = 1;

        public static string Full(string shortName) => Prefix + shortName;
    }

    /// <summary>What a lane problem is about (the "Kind" field of LaneProblem and LaneRecovered).</summary>
    public static class LaneProblemKinds
    {
        public const string InboxUnreachable = "InboxUnreachable";
        public const string LibraryUnreachable = "LibraryUnreachable";
        public const string NotWritable = "NotWritable";
        public const string LowDiskSpace = "LowDiskSpace";
        public const string InboxInsideLibrary = "InboxInsideLibrary";
        public const string PathTooLong = "PathTooLong";

        /// <summary>The lane list breaks a rule (shared inbox, relative path and so on); the message says which.</summary>
        public const string BadSettings = "BadSettings";
    }

    /// <summary>One current problem of one lane. Blocking problems stop the lane from being worked on.</summary>
    public sealed class LaneProblemInfo
    {
        public string LaneId { get; set; } = string.Empty;
        public string LaneName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool Blocking { get; set; }
    }

    /// <summary>The "Send phone alerts" switches on the plugin page: a master switch and one per event. An event that is off is neither sent nor logged.</summary>
    public sealed class NotificationSettings
    {
        public bool SendPhoneAlerts { get; set; } = true;
        public bool NeedsReview { get; set; } = true;
        public bool AutoApplied { get; set; }
        public bool AlbumFailed { get; set; } = true;
        public bool LaneProblem { get; set; } = true;
        public bool ServiceTrouble { get; set; }
        public bool PassFinished { get; set; } = true;

        /// <summary>Whether this event may be sent. Recovery events follow the switch of the problem they end.</summary>
        public bool IsOn(string shortName)
        {
            if (!SendPhoneAlerts) return false;
            switch (shortName)
            {
                case EventNames.NeedsReview: return NeedsReview;
                case EventNames.AutoApplied: return AutoApplied;
                case EventNames.AlbumFailed: return AlbumFailed;
                case EventNames.LaneProblem:
                case EventNames.LaneRecovered: return LaneProblem;
                case EventNames.ServiceTrouble:
                case EventNames.ServiceRecovered: return ServiceTrouble;
                case EventNames.PassFinished: return PassFinished;
                default: return false;
            }
        }
    }

    /// <summary>An event the aggregator decided to send, before it has a sequence number.</summary>
    public sealed class PendingEvent
    {
        public PendingEvent(string shortName, Dictionary<string, object?> fields)
        {
            Event = shortName;
            Fields = fields;
        }

        public string Event { get; }
        public Dictionary<string, object?> Fields { get; }
    }

    /// <summary>An event as sent and logged: the full message name, the time and the whole payload (common fields included).</summary>
    public sealed class NotificationEvent
    {
        public long Seq { get; set; }

        /// <summary>The message name, for example LabelMaker.NeedsReview.</summary>
        public string Name { get; set; } = string.Empty;

        public DateTime AtUtc { get; set; }

        public JsonElement Data { get; set; }
    }
}
