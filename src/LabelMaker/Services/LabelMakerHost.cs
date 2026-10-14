using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Engine;
using LabelMaker.Core.MusicBrainz;
using LabelMaker.Core.Notifications;

namespace LabelMaker.Services;

/// <summary>
/// Connects the Emby-free engine to Emby: where its files live, the saved settings, the timer that looks at the inboxes, the push to
/// administrator sessions and the library refresh after an album arrives.
/// </summary>
internal static class LabelMakerHost
{
	private const string ProjectAddress = "https://github.com/feerlessleadr/label-maker-emby";

	private static LabelMakerEngine? _engine;

	private static CancellationTokenSource? _cts;

	public static LabelMakerEngine Engine => _engine ?? throw new InvalidOperationException("Label Maker has not started yet.");

	/// <summary>The saved settings as the engine sees them.</summary>
	public static EngineSettings CurrentSettings()
	{
		return Plugin.Instance!.Configuration.ToEngineSettings(Environment.UserName);
	}

	/// <summary>The reason a lane may not be removed right now, or null (also null while the engine has not started).</summary>
	public static string? RemovalBlockedReason(string laneId)
	{
		return _engine == null ? null : LabelMaker.Core.Inbox.LaneValidator.RemovalBlockedReason(laneId, _engine.Queue.All());
	}

	public static void Start()
	{
		Stop();
		Plugin plugin = Plugin.Instance!;
		string dataFolder = Path.Combine(plugin.AppPaths.PluginConfigurationsPath, "LabelMaker.Data");
		string version = typeof(Plugin).Assembly.GetName().Version!.ToString();
		string contact = plugin.Configuration.MusicBrainzContact;
		string userAgent = "LabelMaker-Emby/" + version + " (" + ProjectAddress + (contact.Length > 0 ? "; " + contact : string.Empty) + ")";

		MusicBrainzClient client = new MusicBrainzClient(new HttpClientHandler(), userAgent)
		{
			Cache = new DiskResponseCache(Path.Combine(dataFolder, "musicbrainz-cache")),
		};
		_engine = new LabelMakerEngine(new EngineHost
		{
			DataFolder = dataFolder,
			Settings = CurrentSettings,
			MusicBrainz = client,
			PluginVersion = version,
			RefreshFolder = RefreshFolder,
			OnEvent = Push,
			Log = Plugin.Log,
		});
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts.Token;
		Task.Run(() => LoopAsync(token));
		Plugin.Log("Started version " + version + ".");
	}

	public static void Stop()
	{
		_cts?.Cancel();
		_cts = null;
	}

	private static async Task LoopAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); // let Emby finish starting
			while (!token.IsCancellationRequested)
			{
				int seconds = 60;
				try
				{
					seconds = Math.Max(15, Math.Min(3600, Plugin.Instance!.Configuration.PassSeconds));
					await Engine.RunPassAsync(false, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					Plugin.Log("A check of the inboxes hit a problem: " + ex.GetType().Name + " " + ex.Message);
				}
				await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	/// <summary>Sends an event to every connected administrator session. Fire and forget: a failure is logged and never reaches the engine.</summary>
	private static void Push(NotificationEvent logged)
	{
		object? data = PlainJson.ToPlain(logged.Data);
		Task.Run(async () =>
		{
			try
			{
				await Plugin.Instance!.SessionManager.SendMessageToAdminSessions(logged.Name, data, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Plugin.Log("A phone alert could not be sent: " + ex.GetType().Name + " " + ex.Message);
			}
		});
	}

	/// <summary>Tells Emby that something changed in a library folder. Emby shows a new folder about 90 to 120 seconds later.</summary>
	private static void RefreshFolder(string folder)
	{
		var monitor = Plugin.Instance!.LibraryMonitor;
		monitor.ReportFileSystemChangeBeginning(folder);
		monitor.ReportFileSystemChangeComplete(folder, true);
	}
}
