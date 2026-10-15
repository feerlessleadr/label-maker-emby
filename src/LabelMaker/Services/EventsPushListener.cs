using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Notifications;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;

namespace LabelMaker.Services;

public class EventsPushState : WebSocketListenerState
{
	private static long _counter;

	/// <summary>Grows with every start message; when one socket subscribes again, only the newest subscription answers.</summary>
	public long Id { get; } = Interlocked.Increment(ref _counter);

	public EventSubscription? Subscription { get; set; }

	public long? AfterSeq { get; set; }

	/// <summary>The administrator token the app sent inside the start message (see the class comment). Never logged, echoed or stored.</summary>
	public string? Token { get; set; }
}

/// <summary>
/// Phone alerts over Emby's own websocket, the way Hall Monitor and Hall Pass push theirs (docs/NOTIFICATIONS.md section 12).
/// SendMessageToAdminSessions only reaches sockets whose token is in the address (?api_key=), but the app keeps its token in the X-Emby-Token
/// header, so it subscribes instead: it sends {"MessageType":"LabelMakerEventsStart","Data":"0,30000,&lt;afterSeq&gt;,&lt;token&gt;"} (afterSeq
/// may be empty) and gets {"MessageType":"LabelMakerEvents","Data":{ApiLevel, LatestSeq, Truncated, Events:[...]}} once right away (the events after
/// afterSeq, or only LatestSeq as a baseline when it is empty), within about a second of every new event, and as an empty heartbeat every 30 seconds.
/// Only administrators receive events; anyone else gets {"Error":"Administrator login required."}.
/// Who is an administrator: Emby fills <c>connection.User</c> only for sockets whose token is in the address, never for a token in the header, which
/// is how the app connects and which a plugin cannot read. So the app also sends its administrator token as the fourth value of the start message
/// (inside the websocket, never in an address); the plugin checks it with Emby's session manager and the owner's policy, and checks again on every push.
/// The token is never logged: every message this class writes to the log is a fixed sentence plus an exception type.
/// </summary>
public class EventsPushListener : BasePeriodicWebSocketListener<Dictionary<string, object?>, EventsPushState>
{
	private const int HeartbeatSeconds = 30;

	private static EventsPushListener? _instance;

	private static long _tick;

	private Timer? _heartbeat;

	protected override string Name => "LabelMakerEvents";

	protected override bool SendOnInitialConnection => true;

	public EventsPushListener(ILogManager logManager)
		: base(logManager.GetLogger("LabelMakerEvents"))
	{
		_instance = this;
		_heartbeat = new Timer(delegate
		{
			Interlocked.Increment(ref _tick);
			NotifyChanged();
		}, null, TimeSpan.FromSeconds(HeartbeatSeconds), TimeSpan.FromSeconds(HeartbeatSeconds));
	}

	/// <summary>Sends what is new to every subscribed connection. Safe to call from anywhere, any time.</summary>
	public static void NotifyChanged()
	{
		try
		{
			_instance?.SendData(force: true);
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not push events to subscribers: " + ex.GetType().Name);
		}
	}

	protected override void ParseMessageParams(string[] values, EventsPushState state)
	{
		// Emby takes the first two values (start delay and period); what is left is the optional afterSeq and the token.
		(long? after, string? token) = EventSubscription.ParseStart(values);
		state.AfterSeq = after;
		state.Token = token;
	}

	private static bool IsAdministrator(IWebSocketConnection connection, EventsPushState state)
	{
		// A socket Emby's own web UI opened has its user filled in.
		if (connection.User?.Policy?.IsAdministrator == true)
		{
			return true;
		}
		if (string.IsNullOrEmpty(state.Token))
		{
			return false;
		}
		try
		{
			string? deviceId = connection.QueryString?["deviceId"];
			MediaBrowser.Controller.Session.SessionInfo session = Plugin.Instance!.SessionManager.GetSessionByAuthenticationToken(state.Token, deviceId, connection.RemoteAddress, "http");
			if (session == null)
			{
				return false;
			}
			return Plugin.Instance!.UserManager.GetUserById(session.UserId)?.Policy?.IsAdministrator == true;
		}
		catch (Exception)
		{
			return false; // an invalid or expired token is simply not an administrator; nothing about it is logged
		}
	}

	protected override Task<Dictionary<string, object?>> GetDataToSend(IWebSocketConnection connection, EventsPushState state, CancellationToken cancellationToken)
	{
		// Several start messages on one socket: only the newest subscription answers, so nothing is sent twice.
		lock (ActiveConnections)
		{
			bool newer = ActiveConnections.Any((Tuple<IWebSocketConnection, CancellationTokenSource, EventsPushState> c) => c.Item1 == connection && c.Item3.Id > state.Id);
			if (newer)
			{
				return Task.FromResult<Dictionary<string, object?>>(null);
			}
			// A new start message on a socket replaces its earlier subscriptions (Emby's own stop message removes only one of them).
			foreach (Tuple<IWebSocketConnection, CancellationTokenSource, EventsPushState> old in ActiveConnections.Where((Tuple<IWebSocketConnection, CancellationTokenSource, EventsPushState> c) => c.Item1 == connection && c.Item3.Id < state.Id).ToList())
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
		bool isAdministrator = IsAdministrator(connection, state);
		try
		{
			EventSubscription subscription = state.Subscription ??= new EventSubscription(state.AfterSeq, Interlocked.Read(ref _tick));
			Dictionary<string, object?>? body;
			bool more;
			lock (subscription)
			{
				body = subscription.Next(LabelMakerHost.Engine.Events, isAdministrator, Interlocked.Read(ref _tick));
				more = subscription.MoreWaiting;
			}
			if (more)
			{
				// A backlog bigger than one message: the rest follows right away.
				Task.Run(NotifyChanged);
			}
			return Task.FromResult(body);
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not build an events push: " + ex.GetType().Name);
			return Task.FromResult<Dictionary<string, object?>>(null);
		}
	}
}
