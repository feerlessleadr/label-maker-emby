using System;
using System.Linq;
using LabelMaker.Core.Engine;
using LabelMaker.Core.Inbox;
using LabelMaker.Core.Notifications;
using MediaBrowser.Model.Plugins;

namespace LabelMaker;

/// <summary>One lane: an inbox folder watched for downloads, and the library folder albums are filed into.</summary>
public class LaneConfig
{
	/// <summary>A short id that never changes once albums are queued (letters, digits, dashes, underscores).</summary>
	public string Id { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public string InboxPath { get; set; } = string.Empty;

	public string LibraryPath { get; set; } = string.Empty;

	/// <summary>False means always ask: every album of this lane waits for review, even a certain match.</summary>
	public bool AutoApply { get; set; } = true;
}

/// <summary>Saved settings (LabelMaker.xml).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
	public LaneConfig[] Lanes { get; set; } = new LaneConfig[0];

	/// <summary>While true nothing is looked at or changed (only lane problems are still reported).</summary>
	public bool Paused { get; set; }

	/// <summary>While true nothing is changed automatically: albums that would be filed show as Ready. A person's choice on the review screen is still carried out. The owner switches this off.</summary>
	public bool DryRun { get; set; } = true;

	/// <summary>How often the inbox is looked at, in seconds.</summary>
	public int PassSeconds { get; set; } = 60;

	/// <summary>How long an album's files must stay unchanged before it counts as finished downloading, in seconds.</summary>
	public int StableSeconds { get; set; } = 60;

	/// <summary>The most albums handled in one check.</summary>
	public int MaxPerPass { get; set; } = 20;

	public double AutoThreshold { get; set; } = 0.92;

	public double ReviewThreshold { get; set; } = 0.60;

	public double MinFileScore { get; set; } = 0.5;

	public double Margin { get; set; } = 0.05;

	public int MaxReleaseGroups { get; set; } = 5;

	/// <summary>False: editions that list the same titles in the same places are interchangeable. True: they must also be the same recordings.</summary>
	public bool EditionsMustBeSameRecordings { get; set; }

	/// <summary>Two-letter codes, comma separated, in order of preference.</summary>
	public string PreferredCountries { get; set; } = "US, XW, GB";

	/// <summary>A Picard file naming script. Empty means Picard's stock script.</summary>
	public string NamingScript { get; set; } = string.Empty;

	public int Id3Version { get; set; } = 4;

	public bool WriteId3v1 { get; set; } = true;

	/// <summary>An email or web address added to the User-Agent sent to MusicBrainz, so they can reach you if something goes wrong. Optional.</summary>
	public string MusicBrainzContact { get; set; } = string.Empty;

	/// <summary>Phone alerts (docs/NOTIFICATIONS.md): a master switch and one per event.</summary>
	public bool SendPhoneAlerts { get; set; } = true;

	public bool AlertNeedsReview { get; set; } = true;

	public bool AlertAutoApplied { get; set; }

	public bool AlertAlbumFailed { get; set; } = true;

	public bool AlertLaneProblem { get; set; } = true;

	public bool AlertServiceTrouble { get; set; }

	public bool AlertPassFinished { get; set; } = true;

	/// <summary>The engine's view of these settings.</summary>
	public EngineSettings ToEngineSettings(string accountName)
	{
		return new EngineSettings
		{
			Lanes = (Lanes ?? new LaneConfig[0]).Select((LaneConfig l) => new Lane
			{
				Id = (l.Id ?? string.Empty).Trim(),
				Name = (l.Name ?? string.Empty).Trim(),
				InboxPath = (l.InboxPath ?? string.Empty).Trim(),
				LibraryPath = (l.LibraryPath ?? string.Empty).Trim(),
				AutoApply = l.AutoApply,
			}).ToList(),
			Paused = Paused,
			DryRun = DryRun,
			PassSeconds = PassSeconds,
			StableSeconds = StableSeconds,
			MaxPerPass = MaxPerPass,
			AutoThreshold = AutoThreshold,
			ReviewThreshold = ReviewThreshold,
			MinFileScore = MinFileScore,
			Margin = Margin,
			MaxReleaseGroups = MaxReleaseGroups,
			EditionsMustBeSameRecordings = EditionsMustBeSameRecordings,
			PreferredCountries = (PreferredCountries ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select((string c) => c.Trim().ToUpperInvariant()).ToList(),
			NamingScript = NamingScript ?? string.Empty,
			Id3Version = Id3Version,
			WriteId3v1 = WriteId3v1,
			AccountName = accountName,
			Notifications = new NotificationSettings
			{
				SendPhoneAlerts = SendPhoneAlerts,
				NeedsReview = AlertNeedsReview,
				AutoApplied = AlertAutoApplied,
				AlbumFailed = AlertAlbumFailed,
				LaneProblem = AlertLaneProblem,
				ServiceTrouble = AlertServiceTrouble,
				PassFinished = AlertPassFinished,
			},
		};
	}
}
