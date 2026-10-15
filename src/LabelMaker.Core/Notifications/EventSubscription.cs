using System;
using System.Collections.Generic;
using System.Linq;

namespace LabelMaker.Core.Notifications
{
    /// <summary>
    /// What one websocket subscription (<c>LabelMakerEventsStart</c>) has already been sent, and what to send next (docs/NOTIFICATIONS.md section 12).
    /// Emby-free so it can be tested: the Emby listener only asks <see cref="Next"/> for each connection.
    /// </summary>
    public sealed class EventSubscription
    {
        public const string NotAllowed = "Administrator login required.";

        /// <summary>The most events sent in one message; a bigger backlog goes out in several messages, one right after another.</summary>
        public const int BatchSize = 100;

        private readonly long? _afterSeq;
        private bool _started;
        private bool _refused;
        private long _lastSent;
        private long _tick;

        /// <param name="afterSeq">The highest Seq the app has already seen (the optional third value of the start message), or null when it sent none.</param>
        /// <param name="tick">The heartbeat counter when the subscription was made.</param>
        public EventSubscription(long? afterSeq, long tick)
        {
            _afterSeq = afterSeq;
            _tick = tick;
        }

        public long LastSent => _lastSent;

        /// <summary>True when the last message was a full batch and more events are waiting, so the sender should call again right away.</summary>
        public bool MoreWaiting { get; private set; }

        /// <summary>
        /// The next message body for this connection, or null when there is nothing to say. The first call answers the subscription: the events
        /// after <c>afterSeq</c>, or, when the app sent none, only <c>LatestSeq</c> as a baseline (nothing old is replayed). Later calls send the
        /// events not yet sent, and an empty list as a heartbeat once per tick. A non-administrator gets the error once and nothing more.
        /// </summary>
        public Dictionary<string, object?>? Next(EventLog log, bool isAdministrator, long currentTick)
        {
            MoreWaiting = false;
            if (!isAdministrator)
            {
                if (_refused) return null;
                _refused = true;
                return new Dictionary<string, object?> { ["Error"] = NotAllowed };
            }

            bool truncated = false;
            IReadOnlyList<NotificationEvent> events;
            if (!_started)
            {
                _started = true;
                _tick = currentTick;
                if (_afterSeq.HasValue)
                {
                    events = log.After(Math.Max(0, _afterSeq.Value), BatchSize, out truncated);
                }
                else
                {
                    events = Array.Empty<NotificationEvent>();
                    _lastSent = log.LatestSeq;
                }
            }
            else
            {
                events = log.After(_lastSent, BatchSize, out _);
                if (events.Count == 0)
                {
                    if (_tick == currentTick) return null;   // nothing new and not yet time for a heartbeat
                    _tick = currentTick;
                }
            }

            if (events.Count > 0) _lastSent = events[events.Count - 1].Seq;
            else if (_afterSeq.HasValue && _lastSent < _afterSeq.Value) _lastSent = _afterSeq.Value;
            MoreWaiting = events.Count >= BatchSize && log.LatestSeq > _lastSent;

            return new Dictionary<string, object?>
            {
                ["ApiLevel"] = EventNames.ApiLevel,
                ["LatestSeq"] = log.LatestSeq,
                ["Truncated"] = truncated,
                ["Events"] = events.Select(ToPlain).ToList(),
            };
        }

        /// <summary>One event in the shape of the catch-up route: Seq, Name, AtUtc, Data.</summary>
        public static object ToPlain(NotificationEvent e) => new Dictionary<string, object?>
        {
            ["Seq"] = e.Seq,
            ["Name"] = e.Name,
            ["AtUtc"] = e.AtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["Data"] = PlainJson.ToPlain(e.Data),
        };

        /// <summary>
        /// Reads the values of a start message after Emby has taken the first two (delay and period): the optional last Seq the app has seen
        /// (empty or missing means none) and the administrator's access token. Negative or unreadable numbers count as none.
        /// </summary>
        public static (long? AfterSeq, string? Token) ParseStart(string[] values)
        {
            long? after = null;
            if (values.Length > 0 && long.TryParse(values[0], out var parsed) && parsed >= 0) after = parsed;
            string? token = values.Length > 1 && !string.IsNullOrWhiteSpace(values[1]) ? values[1].Trim() : null;
            return (after, token);
        }
    }
}
