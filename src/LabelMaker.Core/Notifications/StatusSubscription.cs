using System;
using System.Collections.Generic;

namespace LabelMaker.Core.Notifications
{
    /// <summary>
    /// What one websocket subscription (<c>LabelMakerStatusStart</c>) needs to decide when to send the status again (docs/NOTIFICATIONS.md section 13).
    /// Emby-free so it can be tested: the Emby listener only asks <see cref="Next"/> for each connection.
    /// The body is the same as <c>GET /emby/LabelMaker/Status</c>. It is sent right after subscribing, again whenever the status
    /// <c>Version</c> has changed, and once per heartbeat tick (every 30 seconds) so the app can tell a quiet connection from a dead one.
    /// </summary>
    public sealed class StatusSubscription
    {
        public const string NotAllowed = EventSubscription.NotAllowed;

        private bool _started;
        private bool _refused;
        private long _lastVersion = -1;
        private long _tick;

        /// <param name="tick">The heartbeat counter when the subscription was made.</param>
        public StatusSubscription(long tick)
        {
            _tick = tick;
        }

        /// <summary>
        /// The token the app sent inside the start message: <c>"0,30000,&lt;token&gt;"</c>. Emby takes the first two values (start delay, period) itself;
        /// <paramref name="values"/> is what is left, so the token is the first of them.
        /// </summary>
        public static string? ParseStart(string[] values) =>
            values != null && values.Length > 0 && !string.IsNullOrWhiteSpace(values[0]) ? values[0].Trim() : null;

        /// <summary>
        /// The next message body for this connection, or null when there is nothing to say. A non-administrator gets the error once and nothing more.
        /// <paramref name="version"/> is cheap and is read first; <paramref name="status"/> builds the whole body and is called only when it is sent.
        /// </summary>
        public Dictionary<string, object?>? Next(Func<long> version, Func<Dictionary<string, object?>> status, bool isAdministrator, long currentTick)
        {
            if (!isAdministrator)
            {
                if (_refused) return null;
                _refused = true;
                return new Dictionary<string, object?> { ["Error"] = NotAllowed };
            }

            long current = version();
            bool changed = !_started || current != _lastVersion;
            bool heartbeat = _tick != currentTick;
            if (!changed && !heartbeat) return null;

            _started = true;
            _lastVersion = current;
            _tick = currentTick;
            return status();
        }
    }
}
