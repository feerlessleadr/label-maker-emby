using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LabelMaker.Core.Pipeline;

namespace LabelMaker.Core.Inbox
{
    /// <summary>
    /// Finds what is waiting in an inbox. A folder that directly holds audio files is one album; the files of a "CD1" / "Disc 2" subfolder
    /// belong to the album folder above it; every track under a folder named "Singles" is a unit of its own (MusicGrabber's layout). Dot and
    /// underscore folders (MusicGrabber's .trash, our _Trash), playlist folders and the recycle bin are never looked into, and a folder with
    /// a temporary download file in it is marked unfinished. Nothing is read from the files here: only names, sizes and times.
    /// </summary>
    public sealed class InboxScanner
    {
        private static readonly string[] AudioExtensions = { ".mp3", ".flac", ".m4a", ".ogg", ".opus" };
        private static readonly string[] TemporaryExtensions = { ".part", ".partial", ".tmp", ".temp", ".crdownload", ".download", ".!sync", ".labelmaker-tmp" };
        private static readonly string[] IgnoredFolderNames = { "playlists", "$recycle.bin", "system volume information", "lost+found" };
        private static readonly Regex DiscFolder = new Regex(@"^(cd|disc|disk)[\s._-]*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        public static bool IsTemporary(string path)
        {
            var name = Path.GetFileName(path);
            return TemporaryExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()) || name.StartsWith("~", StringComparison.Ordinal) || name.StartsWith(".syncthing.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsIgnoredFolder(string name) =>
            name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("_", StringComparison.Ordinal) || IgnoredFolderNames.Contains(name.ToLowerInvariant());

        public IReadOnlyList<AlbumUnit> Scan(Lane lane, IFileOps ops)
        {
            var root = lane.InboxPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!ops.DirectoryExists(root)) return Array.Empty<AlbumUnit>();

            var folders = new List<string>();
            Walk(root, ops, folders);

            // The folder each audio folder counts under: a disc subfolder counts under its parent (when the parent is not the inbox itself).
            var effective = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var singleFiles = new List<(string Folder, string File)>();
            foreach (var folder in folders)
            {
                var audio = ops.EnumerateFiles(folder).Where(IsAudio).Where(f => !IsTemporary(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (audio.Count == 0) continue;

                if (IsUnderSingles(root, folder))
                {
                    foreach (var f in audio) singleFiles.Add((folder, f));
                    continue;
                }
                var key = folder;
                var parent = Path.GetDirectoryName(folder);
                if (DiscFolder.IsMatch(Path.GetFileName(folder)) && parent != null && !SamePath(parent, root)) key = parent;
                if (!effective.TryGetValue(key, out var list)) effective[key] = list = new List<string>();
                list.AddRange(audio);
            }

            var units = new List<AlbumUnit>();
            foreach (var pair in effective.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var unit = NewUnit(lane, root, pair.Key, UnitKind.Album, pair.Value.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(), null);
                FillFingerprint(unit, ops, WholeAlbumFiles(pair.Key, ops));
                units.Add(unit);
            }
            foreach (var (folder, file) in singleFiles.OrderBy(s => s.File, StringComparer.OrdinalIgnoreCase))
            {
                var unit = NewUnit(lane, root, folder, UnitKind.Single, new List<string> { file }, file);
                FillFingerprint(unit, ops, new List<string> { file });
                units.Add(unit);
            }
            return units;
        }

        private static void Walk(string folder, IFileOps ops, List<string> result)
        {
            result.Add(folder);
            foreach (var sub in ops.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (IsIgnoredFolder(Path.GetFileName(sub))) continue;
                Walk(sub, ops, result);
            }
        }

        private static bool IsUnderSingles(string root, string folder)
        {
            var relative = Path.GetRelativePath(root, folder);
            return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => string.Equals(s, "Singles", StringComparison.OrdinalIgnoreCase));
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        /// <summary>Every non-ignored file in the album folder and its disc subfolders: audio, covers, lyrics, .albuminfo and any temporary files.</summary>
        private static List<string> WholeAlbumFiles(string folder, IFileOps ops)
        {
            var files = new List<string>(ops.EnumerateFiles(folder));
            foreach (var sub in ops.EnumerateDirectories(folder))
            {
                if (DiscFolder.IsMatch(Path.GetFileName(sub))) files.AddRange(ops.EnumerateFiles(sub));
            }
            return files;
        }

        private static AlbumUnit NewUnit(Lane lane, string root, string folder, string kind, List<string> files, string? singleFile)
        {
            var relative = Path.GetRelativePath(root, folder);
            if (relative == ".") relative = string.Empty;
            return new AlbumUnit
            {
                Id = Hash($"{lane.Id}|{relative.ToLowerInvariant()}|{kind}|{(singleFile == null ? string.Empty : Path.GetFileName(singleFile).ToLowerInvariant())}"),
                LaneId = lane.Id,
                Folder = folder,
                RelativeFolder = relative,
                Kind = kind,
                Files = files,
            };
        }

        private static void FillFingerprint(AlbumUnit unit, IFileOps ops, List<string> allFiles)
        {
            var sb = new StringBuilder();
            foreach (var f in allFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (IsTemporary(f)) unit.HasTemporaryFiles = true;
                try
                {
                    sb.Append(Path.GetFileName(f)).Append('|').Append(ops.Length(f)).Append('|').Append(ops.LastWriteUtc(f).Ticks).Append('\n');
                }
                catch (IOException)
                {
                    sb.Append(Path.GetFileName(f)).Append("|unreadable\n"); // a file that is vanishing or locked counts as a change
                }
            }
            unit.Fingerprint = Hash(sb.ToString());
        }

        private static string Hash(string text)
        {
            using var sha = SHA1.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).Substring(0, 20).ToLowerInvariant();
        }
    }
}
