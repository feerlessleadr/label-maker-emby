using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using LabelMaker.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace LabelMaker;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IServerEntryPoint
{
	private readonly ILogger _logger;

	public static Plugin? Instance { get; private set; }

	public IApplicationPaths AppPaths { get; }

	public ILibraryMonitor LibraryMonitor { get; }

	public ISessionManager SessionManager { get; }

	public override string Name => "Label Maker";

	public override string Description => "Tags and files downloaded music the way MusicBrainz Picard would, and asks you only when it is not sure.";

	// Never changes: Emby identifies the plugin by this id.
	public override Guid Id => Guid.Parse("a29bf822-41ab-4e14-a814-a8d386e7f745");

	/// <summary>
	/// Emby loads a plugin from one file and never looks for a second DLL beside it, so the tag library travels inside this DLL (embedded
	/// unmodified) and is handed over the first time anything asks for it.
	/// </summary>
	static Plugin()
	{
		AssemblyLoadContext.Default.Resolving += (AssemblyLoadContext context, AssemblyName name) =>
			name.Name == "TagLibSharp" ? LoadEmbedded(context) : null;
	}

	private static Assembly? LoadEmbedded(AssemblyLoadContext context)
	{
		using Stream? stream = typeof(Plugin).Assembly.GetManifestResourceStream("TagLibSharp.dll");
		return stream == null ? null : context.LoadFromStream(stream);
	}

	public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer, ILibraryMonitor libraryMonitor, ISessionManager sessionManager, ILogManager logManager)
		: base(appPaths, xmlSerializer)
	{
		Instance = this;
		AppPaths = appPaths;
		LibraryMonitor = libraryMonitor;
		SessionManager = sessionManager;
		_logger = logManager.GetLogger("LabelMaker");
	}

	/// <summary>Called when the server has started: begins watching the inboxes.</summary>
	public void Run()
	{
		LabelMakerHost.Start();
	}

	public void Dispose()
	{
		LabelMakerHost.Stop();
	}

	/// <summary>Writes a line to Emby's log.</summary>
	public static void Log(string message)
	{
		Instance?._logger.Info("[Label Maker] " + message, Array.Empty<object>());
	}

	public override void UpdateConfiguration(BasePluginConfiguration configuration)
	{
		if (configuration is PluginConfiguration c)
		{
			c.MusicBrainzContact = (c.MusicBrainzContact ?? string.Empty).Trim();
			if (c.MusicBrainzContact.Length > 200)
			{
				throw new ArgumentException("The MusicBrainz contact must be at most 200 characters.");
			}
			string? problem = c.ToEngineSettings(string.Empty).Problem();
			if (problem != null)
			{
				throw new ArgumentException(problem);
			}
			// A lane cannot be removed while one of its albums is being checked or filed.
			foreach (LaneConfig old in Configuration.Lanes ?? new LaneConfig[0])
			{
				if ((c.Lanes ?? new LaneConfig[0]).Any((LaneConfig n) => n.Id == old.Id))
				{
					continue;
				}
				string? blocked = LabelMakerHost.RemovalBlockedReason(old.Id);
				if (blocked != null)
				{
					throw new ArgumentException(blocked);
				}
			}
		}
		base.UpdateConfiguration(configuration);
	}

	public IEnumerable<PluginPageInfo> GetPages()
	{
		return new PluginPageInfo[2]
		{
			new PluginPageInfo
			{
				Name = "LabelMakerConfiguration",
				EmbeddedResourcePath = GetType().Namespace + ".LabelMakerConfiguration.html",
				EnableInMainMenu = true,
				MenuIcon = "library_music"
			},
			new PluginPageInfo
			{
				Name = "LabelMakerConfigurationjs",
				EmbeddedResourcePath = GetType().Namespace + ".LabelMakerConfiguration.js"
			}
		};
	}
}
