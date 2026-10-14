using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabelMaker.Core.Inbox;

namespace LabelMaker.Core.Pipeline
{
    public enum PreflightLevel
    {
        /// <summary>Checked and fine.</summary>
        Ok,

        /// <summary>Good to know, nothing to fix.</summary>
        Info,

        /// <summary>Label Maker will work, but something may bite later.</summary>
        Warning,

        /// <summary>Label Maker cannot work on this lane until it is fixed.</summary>
        Error,
    }

    public sealed class PreflightCheck
    {
        public string Name { get; set; } = string.Empty;
        public PreflightLevel Level { get; set; }

        /// <summary>What was found and, for a problem, how to fix it, in plain words.</summary>
        public string Message { get; set; } = string.Empty;
    }

    public sealed class LanePreflight
    {
        public string LaneId { get; set; } = string.Empty;
        public List<PreflightCheck> Checks { get; } = new List<PreflightCheck>();

        /// <summary>False when any check is an error: the worker leaves this lane alone and the settings page shows the problem in red.</summary>
        public bool CanRun => Checks.All(c => c.Level != PreflightLevel.Error);
    }

    public sealed class PreflightOptions
    {
        /// <summary>The Windows account Label Maker runs under (the account Emby runs as), named in the messages so the fix is exact. Empty leaves it out.</summary>
        public string AccountName { get; set; } = string.Empty;

        /// <summary>Under this much free space in the library the check is a warning.</summary>
        public long WarnFreeBytes { get; set; } = 1L * 1024 * 1024 * 1024;

        /// <summary>Under this much it is an error.</summary>
        public long ErrorFreeBytes { get; set; } = 100L * 1024 * 1024;

        public int MaxPathLength { get; set; } = 240;

        /// <summary>How long the part of a path below the library folder can get (artist, album and file name together).</summary>
        public int LongestRelativePath { get; set; } = 120;
    }

    /// <summary>
    /// Tries what Label Maker will actually do on a lane (read the inbox, create, rename and delete files in it, create folders and files in the
    /// library) with throw-away probe files, as the account the plugin runs under, and says in plain words what to fix. The probes are removed
    /// again; their names start with a dot so the inbox scanner never mistakes them for music.
    /// </summary>
    public static class Preflight
    {
        public static LanePreflight Run(Lane lane, IFileOps ops, PreflightOptions? options = null)
        {
            options ??= new PreflightOptions();
            var result = new LanePreflight { LaneId = lane.Id };
            var who = options.AccountName.Length > 0 ? $"the account Emby runs as ({options.AccountName})" : "the account Emby runs as";
            void Add(string name, PreflightLevel level, string message) => result.Checks.Add(new PreflightCheck { Name = name, Level = level, Message = message });

            // Inbox
            bool inboxFound = ops.DirectoryExists(lane.InboxPath);
            if (!inboxFound)
            {
                Add("Inbox found", PreflightLevel.Error, $"The inbox folder cannot be reached. Check that the folder exists and, for a network path, that the share is online and that {who} may open it.");
            }
            else
            {
                Add("Inbox found", PreflightLevel.Ok, "The inbox folder was found.");
                var failure = TryFileProbe(ops, lane.InboxPath);
                if (failure == null) Add("Inbox changes", PreflightLevel.Ok, "Label Maker can create, rename and delete files in the inbox.");
                else Add("Inbox changes", PreflightLevel.Error, $"Label Maker could not create, rename and delete a test file in the inbox ({failure}). It has to, because it moves albums out of the inbox. Give {who} Modify permission on that folder.");
            }

            // Library
            bool libraryFound = ops.DirectoryExists(lane.LibraryPath);
            if (!libraryFound)
            {
                Add("Library found", PreflightLevel.Error, $"The library folder cannot be reached. Check that the folder or share exists, that it is online, and that {who} may open it.");
            }
            else
            {
                Add("Library found", PreflightLevel.Ok, "The library folder was found.");
                var failure = TryFolderProbe(ops, lane.LibraryPath);
                if (failure == null) Add("Library changes", PreflightLevel.Ok, "Label Maker can create folders and files in the library.");
                else Add("Library changes", PreflightLevel.Error, $"Label Maker could not create a test folder and file in the library ({failure}). Give {who} Modify permission on the share and on its folder.");

                var free = ops.FreeBytes(lane.LibraryPath);
                if (free < 0) Add("Free space", PreflightLevel.Info, "The free space on the library disk could not be read. Label Maker checks again before each album.");
                else if (free < options.ErrorFreeBytes) Add("Free space", PreflightLevel.Error, $"Only {Describe(free)} is free on the library disk, which is too little to file music. Free some space.");
                else if (free < options.WarnFreeBytes) Add("Free space", PreflightLevel.Warning, $"Only {Describe(free)} is free on the library disk. Large albums may not fit soon.");
                else Add("Free space", PreflightLevel.Ok, $"{Describe(free)} is free on the library disk.");

                if (lane.LibraryPath.Length + 1 + options.LongestRelativePath > options.MaxPathLength)
                {
                    Add("Path length", PreflightLevel.Warning, $"The library folder path is {lane.LibraryPath.Length} characters long. Albums with long names may not fit under the {options.MaxPathLength} character limit and would wait for review. A shorter library path avoids that.");
                }
                else
                {
                    Add("Path length", PreflightLevel.Ok, "The library folder path leaves room for long album names.");
                }
            }

            if (inboxFound && libraryFound)
            {
                bool same;
                try { same = ops.SameVolume(lane.InboxPath, lane.LibraryPath); }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is NotSupportedException) { same = false; }
                Add("Same disk", PreflightLevel.Info, same
                    ? "The inbox and the library are on the same disk, so albums move instantly."
                    : "The inbox and the library are on different disks, so each file is copied, checked and then deleted from the inbox. The first big albums take longer.");
            }
            return result;
        }

        /// <summary>Create a file, rename it, delete it. Returns null when all worked, otherwise the plain reason.</summary>
        private static string? TryFileProbe(IFileOps ops, string folder)
        {
            var name = ".labelmaker-probe-" + Guid.NewGuid().ToString("N");
            var first = Path.Combine(folder, name);
            var second = first + ".renamed";
            try
            {
                ops.WriteAllBytes(first, new byte[] { 1 });
                ops.Move(first, second);
                ops.DeleteFile(second);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Cleanup(ops, first);
                Cleanup(ops, second);
                return Reason(e);
            }
        }

        /// <summary>Create a folder with a file in it, rename the file, remove both. Returns null when all worked.</summary>
        private static string? TryFolderProbe(IFileOps ops, string folder)
        {
            var dir = Path.Combine(folder, ".labelmaker-probe-" + Guid.NewGuid().ToString("N"));
            var first = Path.Combine(dir, "probe.tmp");
            var second = Path.Combine(dir, "probe.renamed");
            try
            {
                ops.CreateDirectory(dir);
                ops.WriteAllBytes(first, new byte[] { 1 });
                ops.Move(first, second);
                ops.DeleteFile(second);
                ops.DeleteDirectoryIfEmpty(dir);
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Cleanup(ops, first);
                Cleanup(ops, second);
                try { ops.DeleteDirectoryIfEmpty(dir); } catch (Exception inner) when (inner is IOException || inner is UnauthorizedAccessException) { }
                return Reason(e);
            }
        }

        private static void Cleanup(IFileOps ops, string file)
        {
            try { if (ops.FileExists(file)) ops.DeleteFile(file); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        private static string Reason(Exception e) => e is UnauthorizedAccessException ? "access was denied" : e.Message.TrimEnd('.');

        private static string Describe(long bytes)
        {
            double gb = bytes / (1024.0 * 1024 * 1024);
            return gb >= 1 ? $"{gb:0.#} GB" : $"{bytes / (1024.0 * 1024):0} MB";
        }
    }
}
