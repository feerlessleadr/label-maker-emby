using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LabelMaker.Core.Notifications
{
    /// <summary>
    /// The numbered list of events sent to the phone, so an app that missed a push can ask for what it missed. Sequence numbers grow by one per
    /// event and survive restarts; the last 500 events are kept in one JSON file replaced atomically. A file that cannot be read is set aside;
    /// the sequence then continues from a separate small file (or, if that is lost too, from the clock) so numbers never go back and an app that
    /// remembers a higher number than the new events is not fooled into ignoring them.
    /// </summary>
    public sealed class EventLog
    {
        public const int Capacity = 500;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = false };

        private readonly string _path;
        private readonly string _seqPath;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();
        private readonly List<NotificationEvent> _events = new List<NotificationEvent>();
        private long _latest;

        public EventLog(string path, Func<DateTime>? utcNow = null)
        {
            _path = path;
            _seqPath = path + ".seq";
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            Load();
        }

        public long LatestSeq
        {
            get { lock (_lock) return _latest; }
        }

        /// <summary>Adds an event, gives it the next number and the common fields, saves, and returns it.</summary>
        public NotificationEvent Append(PendingEvent pending)
        {
            lock (_lock)
            {
                var now = _utcNow();
                _latest++;
                var data = new Dictionary<string, object?>
                {
                    ["ApiLevel"] = EventNames.ApiLevel,
                    ["Seq"] = _latest,
                    ["EventId"] = Guid.NewGuid().ToString(),
                    ["ServerTimeUtc"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    ["Event"] = pending.Event,
                };
                foreach (var pair in pending.Fields) data[pair.Key] = pair.Value;

                var logged = new NotificationEvent
                {
                    Seq = _latest,
                    Name = EventNames.Full(pending.Event),
                    AtUtc = now,
                    Data = JsonSerializer.SerializeToElement(data),
                };
                _events.Add(logged);
                while (_events.Count > Capacity) _events.RemoveAt(0);
                Save();
                return logged;
            }
        }

        /// <summary>The events after a number, oldest first. <paramref name="truncated"/> is true when the log no longer reaches back that far (the app should read the status instead).</summary>
        public IReadOnlyList<NotificationEvent> After(long after, int limit, out bool truncated)
        {
            lock (_lock)
            {
                truncated = _events.Count > 0 && after < _events[0].Seq - 1;
                return _events.Where(e => e.Seq > after).Take(Math.Max(1, Math.Min(limit, Capacity))).ToList();
            }
        }

        private void Load()
        {
            long fromSeqFile = 0;
            try
            {
                if (File.Exists(_seqPath) && long.TryParse(File.ReadAllText(_seqPath).Trim(), out var s)) fromSeqFile = s;
            }
            catch (IOException) { }

            bool damaged = false;
            if (File.Exists(_path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<List<NotificationEvent>>(File.ReadAllText(_path), Json);
                    if (loaded != null) _events.AddRange(loaded.OrderBy(e => e.Seq));
                }
                catch (JsonException)
                {
                    damaged = true;
                }
                if (damaged)
                {
                    var aside = _path + ".corrupt";
                    try
                    {
                        if (File.Exists(aside)) File.Delete(aside);
                        File.Move(_path, aside);
                    }
                    catch (IOException) { }
                }
            }

            _latest = Math.Max(fromSeqFile, _events.Count == 0 ? 0 : _events[_events.Count - 1].Seq);
            // Everything is lost but events existed once: start high enough that no app remembers a larger number.
            if (damaged && _latest == 0) _latest = (long)(_utcNow() - DateTime.UnixEpoch).TotalSeconds;
        }

        private void Save()
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_events, Json));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temp, _path);
            File.WriteAllText(_seqPath, _latest.ToString());
        }
    }
}
