using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LabelMaker.Core.Notifications;
using LabelMaker.Core.Review;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace LabelMaker.Services;

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Info", "GET", Summary = "Plugin version, API level and features.")]
public class LmInfoRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Status", "GET", Summary = "Counts per state and lane, lane problems, MusicBrainz state, last check.")]
public class LmStatusRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Events", "GET", Summary = "The phone alerts after a sequence number (the last 500 are kept), oldest first.")]
public class LmEventsRequest : IReturn<object>
{
	public long After { get; set; }

	public int Limit { get; set; } = 100;
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Notifications/Catalog", "GET", Summary = "The phone alerts with their titles, parameters and suggested rules.")]
public class LmCatalogRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Preflight", "GET", Summary = "Tries every lane (read, create, rename, delete, free space) and reports what to fix.")]
public class LmPreflightRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Run", "POST", Summary = "Checks the inboxes now.")]
public class LmRunRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Pause", "POST", Summary = "Pauses or resumes Label Maker.")]
public class LmPauseRequest : IReturn<object>
{
	public bool Paused { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/DryRun", "POST", Summary = "Switches dry run on or off.")]
public class LmDryRunRequest : IReturn<object>
{
	public bool DryRun { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums", "GET", Summary = "The albums found in the inboxes (and filed ones), by state and lane.")]
public class LmAlbumsRequest : IReturn<object>
{
	/// <summary>Comma separated: Waiting, NeedsReview, NoMatch, Ready, Applying, Done, Failed, Skipped, Duplicate.</summary>
	public string State { get; set; }

	public string Lane { get; set; }

	public string Text { get; set; }

	public int Start { get; set; }

	public int Limit { get; set; } = 50;
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}", "GET", Summary = "One album: files and their tags, match notes and candidate editions.")]
public class LmAlbumRequest : IReturn<object>
{
	public string Id { get; set; }
}

/// <summary>A person's answer to "which track is this file?".</summary>
public class MappingDto
{
	/// <summary>The file's path below the album folder, as the album screen shows it.</summary>
	public string File { get; set; }

	public int Disc { get; set; } = 1;

	public int Position { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Preview", "POST", Summary = "Shows what choosing an edition would do. Changes nothing.")]
public class LmPreviewAlbumRequest : IReturn<object>
{
	public string Id { get; set; }

	public string ReleaseId { get; set; }

	public MappingDto[] Mapping { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Apply", "POST", Summary = "Tags and files the album with the chosen edition.")]
public class LmApplyAlbumRequest : IReturn<object>
{
	public string Id { get; set; }

	public string ReleaseId { get; set; }

	public MappingDto[] Mapping { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Search", "POST", Summary = "Searches MusicBrainz by artist and album text.")]
public class LmSearchAlbumRequest : IReturn<object>
{
	public string Id { get; set; }

	public string Artist { get; set; }

	public string Album { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Resolve", "POST", Summary = "Turns a pasted MusicBrainz link or id into candidate editions.")]
public class LmResolveAlbumRequest : IReturn<object>
{
	public string Id { get; set; }

	public string Text { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/KeepTags", "POST", Summary = "Files the album with its own tags unchanged.")]
public class LmKeepTagsRequest : IReturn<object>
{
	public string Id { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Skip", "POST", Summary = "Leaves the album alone until it is checked again.")]
public class LmSkipRequest : IReturn<object>
{
	public string Id { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Recheck", "POST", Summary = "Puts the album back in line for a fresh check.")]
public class LmRecheckRequest : IReturn<object>
{
	public string Id { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Discard", "POST", Summary = "Moves the album's files to the inbox's _Trash folder (nothing is deleted).")]
public class LmDiscardRequest : IReturn<object>
{
	public string Id { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Restore", "POST", Summary = "Moves a discarded album back.")]
public class LmRestoreRequest : IReturn<object>
{
	public string Id { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Undo", "POST", Summary = "Puts a filed album back in the inbox with its old tags and names.")]
public class LmUndoRequest : IReturn<object>
{
	public string Id { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMaker/Albums/{Id}/Split", "POST", Summary = "Puts each album of a mixed folder in a folder of its own.")]
public class LmSplitRequest : IReturn<object>
{
	public string Id { get; set; }
}

public class LabelMakerApiService : IService
{
	private static LabelMaker.Core.Engine.LabelMakerEngine Engine => LabelMakerHost.Engine;

	public object Get(LmInfoRequest request) => Engine.Info();

	public object Get(LmStatusRequest request) => Engine.Status();

	public object Get(LmEventsRequest request)
	{
		IReadOnlyList<NotificationEvent> found = Engine.Events.After(Math.Max(0, request.After), request.Limit <= 0 ? 100 : request.Limit, out bool truncated);
		return new Dictionary<string, object>
		{
			["LatestSeq"] = Engine.Events.LatestSeq,
			["Truncated"] = truncated,
			["Events"] = found.Select((NotificationEvent e) => (object)new Dictionary<string, object>
			{
				["Seq"] = e.Seq,
				["Name"] = e.Name,
				["AtUtc"] = e.AtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
				["Data"] = PlainJson.ToPlain(e.Data),
			}).ToList(),
		};
	}

	public object Get(LmCatalogRequest request) => NotificationCatalog.Build();

	public object Get(LmPreflightRequest request) => Engine.PreflightReport(LabelMakerHost.CurrentSettings());

	public object Post(LmRunRequest request)
	{
		// The check can take a while (MusicBrainz is asked politely, one request at a time), so it runs on its own; the page reads the status.
		Task.Run(async () =>
		{
			try
			{
				await Engine.RunPassAsync(true).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Plugin.Log("A check of the inboxes hit a problem: " + ex.GetType().Name + " " + ex.Message);
			}
		});
		return new { Started = true };
	}

	public object Post(LmPauseRequest request)
	{
		PluginConfiguration configuration = Plugin.Instance!.Configuration;
		configuration.Paused = request.Paused;
		Plugin.Instance.UpdateConfiguration(configuration);
		return new { Paused = configuration.Paused };
	}

	public object Post(LmDryRunRequest request)
	{
		PluginConfiguration configuration = Plugin.Instance!.Configuration;
		configuration.DryRun = request.DryRun;
		Plugin.Instance.UpdateConfiguration(configuration);
		return new { DryRun = configuration.DryRun };
	}

	public object Get(LmAlbumsRequest request) => Engine.Albums(request.State, request.Lane, request.Text, request.Start, request.Limit <= 0 ? 50 : request.Limit);

	public object Get(LmAlbumRequest request) => (object)Engine.Album(request.Id) ?? throw new ArgumentException("That album is not in the list (any more).");

	public object Post(LmPreviewAlbumRequest request) => Engine.PreviewAsync(request.Id, request.ReleaseId, Map(request.Mapping)).GetAwaiter().GetResult();

	public object Post(LmApplyAlbumRequest request) => Engine.ApplyAsync(request.Id, request.ReleaseId, Map(request.Mapping)).GetAwaiter().GetResult();

	public object Post(LmSearchAlbumRequest request) => Engine.SearchAsync(request.Id, request.Artist ?? string.Empty, request.Album ?? string.Empty).GetAwaiter().GetResult();

	public object Post(LmResolveAlbumRequest request) => Engine.ResolveAsync(request.Id, request.Text ?? string.Empty).GetAwaiter().GetResult();

	public object Post(LmKeepTagsRequest request) => Engine.KeepMyTagsAsync(request.Id).GetAwaiter().GetResult();

	public object Post(LmSkipRequest request) => Engine.SkipAsync(request.Id).GetAwaiter().GetResult();

	public object Post(LmRecheckRequest request) => Engine.RecheckAsync(request.Id).GetAwaiter().GetResult();

	public object Post(LmDiscardRequest request) => Engine.DiscardAsync(request.Id).GetAwaiter().GetResult();

	public object Post(LmRestoreRequest request) => Engine.RestoreAsync(request.Id).GetAwaiter().GetResult();

	public object Post(LmUndoRequest request) => Engine.UndoAsync(request.Id).GetAwaiter().GetResult();

	public object Post(LmSplitRequest request) => Engine.SplitAsync(request.Id).GetAwaiter().GetResult();

	private static IReadOnlyList<MappingEntry> Map(MappingDto[] mapping)
	{
		return (mapping ?? new MappingDto[0]).Select((MappingDto m) => new MappingEntry { File = m.File ?? string.Empty, Disc = m.Disc, Position = m.Position }).ToList();
	}
}
