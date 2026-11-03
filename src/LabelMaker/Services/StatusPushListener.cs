using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Notifications;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;

namespace LabelMaker.Services;

public class StatusPushState : WebSocketListenerState
{
	private static long _counter;

	/// <summary>Grows with every start message; when one socket subscribes again, only the newest subscription answers.</summary>
	public long Id { get; } = Interlocked.Increment(ref _counter);

	public StatusSubscription? Subscription { get; set; }

	/// <summary>The administrator token the app sent inside the start message (see <see cref="SocketAuth"/>). Never logged, echoed or stored.</summary>
	public string? Token { get; set; }
}

/// <summary>
/// The live status for the app's Library tab over Emby's own websocket (docs/NOTIFICATIONS.md section 13), the way Hall Monitor pushes its status.
/// The app sends {"MessageType":"LabelMakerStatusStart","Data":"0,30000,&lt;token&gt;"} and gets {"MessageType":"LabelMakerStatus","Data":{...}} with the same body
/// as <c>GET /emby/LabelMaker/Status</c> once right away, within about a second of every change (when the status Version grows), and as a heartbeat every
/// 30 seconds. Only administrators receive it; anyone else gets {"Error":"Administrator login required."} once. The token is checked as for the events
/// subscription (<see cref="SocketAuth"/>) and is never logged: every message this class writes to the log is a fixed sentence plus an exception type.
/// </summary>
public class StatusPushListener : BasePeriodicWebSocketListener<Dictionary<string, object?>, StatusPushState>
{
	private const int HeartbeatSeconds = 30;

	private static StatusPushListener? _instance;

	private static long _tick;

	private static long _lastSeenVersion = -1;

	private Timer? _heartbeat;

	private Timer? _watch;

	protected override string Name => "LabelMakerStatus";

	protected override bool SendOnInitialConnection => true;

	public StatusPushListener(ILogManager logManager)
		: base(logManager.GetLogger("LabelMakerStatus"))
	{
		_instance = this;
		_heartbeat = new Timer(delegate
		{
			Interlocked.Increment(ref _tick);
			NotifyChanged();
		}, null, TimeSpan.FromSeconds(HeartbeatSeconds), TimeSpan.FromSeconds(HeartbeatSeconds));
		// Once a second, a cheap look at the status version: only a change sends anything.
		_watch = new Timer(delegate
		{
			try
			{
				long version = LabelMakerHost.Engine.StatusVersion();
				if (version != Interlocked.Exchange(ref _lastSeenVersion, version))
				{
					NotifyChanged();
				}
			}
			catch (Exception)
			{
				// the engine is not running yet (or just stopped): nothing to say
			}
		}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
	}

	/// <summary>Sends the status to every subscribed connection whose subscription says it is time. Safe to call from anywhere, any time.</summary>
	public static void NotifyChanged()
	{
		try
		{
			_instance?.SendData(force: true);
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not push the status to subscribers: " + ex.GetType().Name);
		}
	}

	protected override void ParseMessageParams(string[] values, StatusPushState state)
	{
		// Emby takes the first two values (start delay and period); what is left is the token.
		state.Token = StatusSubscription.ParseStart(values);
	}

	protected override Task<Dictionary<string, object?>> GetDataToSend(IWebSocketConnection connection, StatusPushState state, CancellationToken cancellationToken)
	{
		// Several start messages on one socket: only the newest subscription answers, so nothing is sent twice.
		lock (ActiveConnections)
		{
			bool newer = ActiveConnections.Any((Tuple<IWebSocketConnection, CancellationTokenSource, StatusPushState> c) => c.Item1 == connection && c.Item3.Id > state.Id);
			if (newer)
			{
				return Task.FromResult<Dictionary<string, object?>>(null);
			}
			foreach (Tuple<IWebSocketConnection, CancellationTokenSource, StatusPushState> old in ActiveConnections.Where((Tuple<IWebSocketConnection, CancellationTokenSource, StatusPushState> c) => c.Item1 == connection && c.Item3.Id < state.Id).ToList())
			{
				ActiveConnections.Remove(old);
				try
				{
					old.Item2.Cancel();
				}
				catch (ObjectDisposedException)
				{
				}
			}
		}
		bool isAdministrator = SocketAuth.IsAdministrator(connection, state.Token);
		try
		{
			StatusSubscription subscription = state.Subscription ??= new StatusSubscription(Interlocked.Read(ref _tick));
			Dictionary<string, object?>? body;
			lock (subscription)
			{
				body = subscription.Next(LabelMakerHost.Engine.StatusVersion, LabelMakerHost.Engine.Status, isAdministrator, Interlocked.Read(ref _tick));
			}
			return Task.FromResult(body);
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not build a status push: " + ex.GetType().Name);
			return Task.FromResult<Dictionary<string, object?>>(null);
		}
	}
}
