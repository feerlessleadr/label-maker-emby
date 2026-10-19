using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LabelMaker.Core.Models;

namespace LabelMaker.Core.Pipeline
{
    public sealed class JournalSidecar
    {
        public string Source { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public bool Done { get; set; }
    }

    public sealed class HousekeepingBackup
    {
        public string Path { get; set; } = string.Empty;
        public string Base64 { get; set; } = string.Empty;
    }

    public sealed class JournalFile
    {
        public string Source { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;

        /// <summary>The tags the file had before Label Maker touched it (the backup that Undo restores).</summary>
        public TrackTags OldTags { get; set; } = new TrackTags();

        public TrackTags NewTags { get; set; } = new TrackTags();

        /// <summary>"Pending", then "Tagged" (new tags written in place, checked), then "Moved".</summary>
        public string Step { get; set; } = "Pending";

        public List<JournalSidecar> Sidecars { get; set; } = new List<JournalSidecar>();
    }

    /// <summary>The record of one album's changes, saved after every step so a crash can be resumed and any album can be undone.</summary>
    public sealed class AlbumJournal
    {
        public string AlbumId { get; set; } = string.Empty;

        /// <summary>"Applying", "Applied", "Failed" or "Undone".</summary>
        public string State { get; set; } = "Applying";

        public string Message { get; set; } = string.Empty;
        public string ReleaseId { get; set; } = string.Empty;
        public string InboxRoot { get; set; } = string.Empty;
        public string SourceFolder { get; set; } = string.Empty;
        public string LibraryRoot { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public List<JournalFile> Files { get; set; } = new List<JournalFile>();
        public List<JournalSidecar> AlbumSidecars { get; set; } = new List<JournalSidecar>();

        /// <summary>Small housekeeping files deleted from the inbox after the move (.albuminfo, a second cover), kept so Undo can bring them back.</summary>
        public List<HousekeepingBackup> Housekeeping { get; set; } = new List<HousekeepingBackup>();

        /// <summary>The cover image Label Maker downloaded into the library folder after filing (empty when none); Undo removes it.</summary>
        public string AddedCover { get; set; } = string.Empty;
    }

    /// <summary>One JSON file per album in a folder (the plugin's data folder). Writes are atomic: a crash never leaves half a journal.</summary>
    public sealed class JournalStore
    {
        private static readonly Regex SafeId = new Regex("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        private readonly string _folder;

        public JournalStore(string folder)
        {
            _folder = folder;
            Directory.CreateDirectory(folder);
        }

        public AlbumJournal? Load(string albumId)
        {
            var path = PathFor(albumId);
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<AlbumJournal>(File.ReadAllText(path), Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Save(AlbumJournal journal)
        {
            var path = PathFor(journal.AlbumId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(journal, Json));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private string PathFor(string albumId)
        {
            // The id becomes part of a file name: only plain characters are accepted, so it cannot point anywhere else.
            if (albumId == null || !SafeId.IsMatch(albumId)) throw new ArgumentException("The album id may hold only letters, digits, '-' and '_'.", nameof(albumId));
            return Path.Combine(_folder, albumId + ".json");
        }
    }
}
