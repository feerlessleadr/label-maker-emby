using System;
using MediaBrowser.Controller.Net;

namespace LabelMaker.Services;

/// <summary>
/// Who may subscribe to Label Maker's websocket channels (the events and the status). Emby fills <c>connection.User</c> only for sockets whose
/// token is in the address, never for a token in the X-Emby-Token header, which is how the app connects and which a plugin cannot read. So the app also
/// sends its administrator token inside the start message (never in an address); it is checked here with Emby's session manager and the owner's
/// policy, and checked again on every push. The token is never logged.
/// </summary>
internal static class SocketAuth
{
	public static bool IsAdministrator(IWebSocketConnection connection, string? token)
	{
		// A socket Emby's own web UI opened has its user filled in.
		if (connection.User?.Policy?.IsAdministrator == true)
		{
			return true;
		}
		if (string.IsNullOrEmpty(token))
		{
			return false;
		}
		try
		{
			string? deviceId = connection.QueryString?["deviceId"];
			MediaBrowser.Controller.Session.SessionInfo session = Plugin.Instance!.SessionManager.GetSessionByAuthenticationToken(token, deviceId, connection.RemoteAddress, "http");
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
}
