using System;

namespace LabelMaker.Core.Inbox
{
    /// <summary>One inbox folder paired with the library folder its albums go to (plus what is allowed to happen automatically).</summary>
    public sealed class Lane
    {
        /// <summary>A short stable id (letters, digits, '-' and '_'); part of every album's id, so it never changes once albums are queued.</summary>
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>The folder watched for downloads, as the server sees it.</summary>
        public string InboxPath { get; set; } = string.Empty;

        /// <summary>The library folder albums are filed into.</summary>
        public string LibraryPath { get; set; } = string.Empty;

        /// <summary>False means "always ask me": every album of this lane waits for review even when it is a certain match.</summary>
        public bool AutoApply { get; set; } = true;
    }

    /// <summary>What discovery finds in an inbox: one album folder, or one loose single track.</summary>
    public sealed class AlbumUnit
    {
        /// <summary>Stable across scans: the same folder (or single file) in the same lane always has the same id.</summary>
        public string Id { get; set; } = string.Empty;

        public string LaneId { get; set; } = string.Empty;

        /// <summary>The folder holding the audio (for a merged "CD1/CD2" album, the folder above the disc folders).</summary>
        public string Folder { get; set; } = string.Empty;

        /// <summary>The folder path below the inbox, for showing to people.</summary>
        public string RelativeFolder { get; set; } = string.Empty;

        /// <summary>"Album" (everything in a folder is one album) or "Single" (one loose track, from a "Singles" folder).</summary>
        public string Kind { get; set; } = UnitKind.Album;

        public System.Collections.Generic.List<string> Files { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Changes whenever a file is added, removed, resized or rewritten. An unchanged fingerprint for long enough means the download is finished.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>True while a temporary download file (.part, .crdownload, a sync temp file) sits in the folder: the album is not finished.</summary>
        public bool HasTemporaryFiles { get; set; }
    }

    public static class UnitKind
    {
        public const string Album = "Album";
        public const string Single = "Single";
    }
}
