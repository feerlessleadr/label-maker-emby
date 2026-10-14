using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LabelMaker.Core.Matching;
using LabelMaker.Core.Models;
using LabelMaker.Core.Naming;

namespace LabelMaker.Core.Pipeline
{
    public sealed class PlannerOptions
    {
        /// <summary>Picard's stock naming script, as used by the owner (Preset 1): Artist/Album/01 Title, the disc in the file name.</summary>
        public const string PicardDefaultScript =
            "$if2(%albumartist%,%artist%)/\n" +
            "$if(%albumartist%,%album%/,)\n" +
            "$if($gt(%totaldiscs%,1),$if($gt(%totaldiscs%,9),$num(%discnumber%,2),%discnumber%)-,)" +
            "$if($and(%albumartist%,%tracknumber%),$num(%tracknumber%,2) ,)" +
            "$if(%_multiartist%,%artist% - ,)%title%";

        public string NamingScript { get; set; } = PicardDefaultScript;

        /// <summary>The longest full destination path written (Windows allows 260; this leaves room).</summary>
        public int MaxPathLength { get; set; } = 240;

        /// <summary>Reuse an existing folder that matches the artist or album ignoring case, accents and punctuation (Beyonce versus Beyoncé).</summary>
        public bool ReuseExistingFolders { get; set; } = true;

        /// <summary>Lyric sidecars (.lrc) that belong to a track and move with it.</summary>
        public IList<string> TrackSidecarExtensions { get; } = new List<string> { ".lrc" };

        /// <summary>Album cover names kept, in order of preference. The first one found is moved as "cover" plus its extension.</summary>
        public IList<string> CoverNames { get; } = new List<string> { "cover", "folder", "albumart" };

        public IList<string> CoverExtensions { get; } = new List<string> { ".jpg", ".jpeg", ".png" };
    }

    public sealed class PlannedSidecar
    {
        public string Source { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
    }

    public sealed class PlannedFile
    {
        public string Source { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public TrackTags OldTags { get; set; } = new TrackTags();
        public TrackTags NewTags { get; set; } = new TrackTags();
        public List<PlannedSidecar> Sidecars { get; } = new List<PlannedSidecar>();
    }

    /// <summary>Everything that would be done for one album, worked out without touching a file.</summary>
    public sealed class AlbumPlan
    {
        public string AlbumId { get; set; } = string.Empty;
        public string InboxRoot { get; set; } = string.Empty;
        public string SourceFolder { get; set; } = string.Empty;
        public string LibraryRoot { get; set; } = string.Empty;
        public string ReleaseId { get; set; } = string.Empty;
        public List<PlannedFile> Files { get; } = new List<PlannedFile>();
        public List<PlannedSidecar> AlbumSidecars { get; } = new List<PlannedSidecar>();

        /// <summary>Plain sentences saying why the album cannot be applied (empty when it can).</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>True when a destination already holds a file: the album is a duplicate and nothing may be written.</summary>
        public bool IsDuplicate { get; set; }

        public bool CanApply => Problems.Count == 0;
    }

    public static class Planner
    {
        public static AlbumPlan Plan(
            string albumId,
            string inboxRoot,
            string sourceFolder,
            string libraryRoot,
            ReleaseVerification match,
            IFileOps ops,
            PlannerOptions options)
        {
            var plan = new AlbumPlan
            {
                AlbumId = albumId,
                InboxRoot = inboxRoot,
                SourceFolder = sourceFolder,
                LibraryRoot = libraryRoot,
                ReleaseId = match.Release.Id,
            };

            var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var release = match.Release;
            var reservedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assignment in match.Assignments)
            {
                if (assignment.Track == null)
                {
                    plan.Problems.Add($"\"{Path.GetFileName(assignment.File.Path)}\" has no track on the chosen release.");
                    continue;
                }

                var medium = release.Media.First(m => m.Position == assignment.Disc);
                var newTags = BuildTags(release, medium, assignment);
                var rendered = PicardScript.Render(options.NamingScript, NamingVariables.ForTrack(newTags));
                var parts = PathSanitiser.Path(rendered).ToList();
                if (parts.Count == 0)
                {
                    plan.Problems.Add($"The naming script gave no file name for \"{newTags.Title}\".");
                    continue;
                }

                var folders = parts.Take(parts.Count - 1).ToList();
                if (options.ReuseExistingFolders) folders = ReuseFolders(root, folders, ops, reservedFolders);
                var extension = Path.GetExtension(assignment.File.Path).ToLowerInvariant();
                var destination = Path.Combine(new[] { root }.Concat(folders).Append(parts[parts.Count - 1] + extension).ToArray());

                if (!IsInside(root, destination))
                {
                    plan.Problems.Add($"The destination for \"{newTags.Title}\" would be outside the library folder.");
                    continue;
                }
                if (destination.Length > options.MaxPathLength)
                {
                    plan.Problems.Add($"The path for \"{newTags.Title}\" is {destination.Length} characters long; the limit is {options.MaxPathLength}.");
                    continue;
                }

                var planned = new PlannedFile
                {
                    Source = assignment.File.Path,
                    Destination = destination,
                    OldTags = assignment.File.Tags,
                    NewTags = newTags,
                };
                foreach (var ext in options.TrackSidecarExtensions)
                {
                    var sidecar = Path.ChangeExtension(assignment.File.Path, ext);
                    if (ops.FileExists(sidecar))
                    {
                        planned.Sidecars.Add(new PlannedSidecar { Source = sidecar, Destination = Path.ChangeExtension(destination, ext) });
                    }
                }
                plan.Files.Add(planned);
            }

            CheckDestinations(plan, ops);
            AddCover(plan, ops, options);
            return plan;
        }

        /// <summary>The tags a track gets from the chosen release: the way Picard fills them.</summary>
        public static TrackTags BuildTags(MusicBrainz.ReleaseInfo release, MusicBrainz.MediumInfo medium, FileAssignment assignment)
        {
            int? year = null;
            if (release.Date.Length >= 4 && int.TryParse(release.Date.Substring(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 0) year = y;
            var track = assignment.Track!;
            return new TrackTags
            {
                Title = track.Title,
                Artist = track.ArtistCredit.Length > 0 ? track.ArtistCredit : release.ArtistCredit,
                AlbumArtist = release.ArtistCredit,
                Album = release.Title,
                TrackNumber = track.Position,
                TotalTracks = medium.Tracks.Count,
                DiscNumber = medium.Position,
                TotalDiscs = release.Media.Count,
                Year = year,
                ReleaseMbid = release.Id,
            };
        }

        private static List<string> ReuseFolders(string root, List<string> folders, IFileOps ops, Dictionary<string, string> reserved)
        {
            var result = new List<string>();
            var current = root;
            foreach (var wanted in folders)
            {
                var key = current + "|" + TitleNormaliser.Normalise(wanted);
                if (!reserved.TryGetValue(key, out var chosen))
                {
                    chosen = ops.EnumerateDirectories(current)
                        .Select(Path.GetFileName)
                        .FirstOrDefault(name => TitleNormaliser.Normalise(name!) == TitleNormaliser.Normalise(wanted)) ?? wanted;
                    reserved[key] = chosen!;
                }
                result.Add(chosen!);
                current = Path.Combine(current, chosen!);
            }
            return result;
        }

        private static bool IsInside(string root, string path)
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void CheckDestinations(AlbumPlan plan, IFileOps ops)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in plan.Files)
            {
                if (!seen.Add(file.Destination))
                {
                    plan.Problems.Add($"Two files would get the same name: {Path.GetFileName(file.Destination)}.");
                }
                else if (ops.FileExists(file.Destination))
                {
                    plan.IsDuplicate = true;
                    plan.Problems.Add($"\"{Path.GetFileName(file.Destination)}\" is already in the library.");
                }
            }
        }

        /// <summary>One cover image per album: the first of cover, folder, albumart found in the folder, moved as "cover" plus its extension.</summary>
        private static void AddCover(AlbumPlan plan, IFileOps ops, PlannerOptions options)
        {
            if (plan.Files.Count == 0) return;
            var albumFolder = Path.GetDirectoryName(plan.Files[0].Destination)!;
            var inFolder = ops.EnumerateFiles(plan.SourceFolder).ToList();
            foreach (var name in options.CoverNames)
            {
                var found = inFolder.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase) &&
                    options.CoverExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                if (found == null) continue;
                var destination = Path.Combine(albumFolder, "cover" + Path.GetExtension(found).ToLowerInvariant());
                if (!ops.FileExists(destination)) // never replace the cover the library already has
                {
                    plan.AlbumSidecars.Add(new PlannedSidecar { Source = found, Destination = destination });
                }
                return;
            }
        }
    }
}
