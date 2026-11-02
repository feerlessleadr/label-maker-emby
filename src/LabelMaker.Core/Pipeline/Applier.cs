using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabelMaker.Core.Models;
using LabelMaker.Core.Tags;

namespace LabelMaker.Core.Pipeline
{
    public enum ApplyState
    {
        Applied,

        /// <summary>Dry run: the plan was checked and nothing was changed.</summary>
        DryRun,

        /// <summary>A file with the same name is already in the library. Nothing was changed.</summary>
        Duplicate,

        /// <summary>Something went wrong. Files are safe: the journal says what was done and Apply can be run again.</summary>
        Failed,
    }

    public sealed class ApplyResult
    {
        public ApplyState State { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public sealed class UndoResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Carries out an <see cref="AlbumPlan"/>: back up the old tags (journal), write the new tags in the inbox, check them, move
    /// each file and its sidecars into the library, and tidy the inbox. Every step is saved in the journal first, so a crash is
    /// resumed by running Apply again and any album can be undone. Nothing is overwritten and a source is deleted only after its
    /// copy is verified.
    /// </summary>
    public sealed class Applier
    {
        private static readonly string[] JunkNames = { ".albuminfo", "thumbs.db", "desktop.ini", ".ds_store" };
        private static readonly string[] CoverNames = { "cover", "folder", "albumart" };

        private readonly IFileOps _ops;
        private readonly JournalStore _journals;
        private readonly TagWriteOptions _tagOptions;
        private readonly bool _forceCopy;

        public Applier(IFileOps ops, JournalStore journals, TagWriteOptions tagOptions, bool forceCopy = false)
        {
            _ops = ops;
            _journals = journals;
            _tagOptions = tagOptions;
            _forceCopy = forceCopy;
        }

        public ApplyResult Apply(AlbumPlan plan, bool dryRun)
        {
            var existing = _journals.Load(plan.AlbumId);
            bool laterBatch = false;
            if (existing != null && existing.State == "Applied")
            {
                // The same finished plan sent again: its files are gone from the inbox, so there is nothing left to do.
                if (plan.Files.Count == 0 || plan.Files.All(f => !_ops.FileExists(f.Source)))
                {
                    return new ApplyResult { State = ApplyState.Applied, Message = "Already applied." };
                }
                laterBatch = true; // more files arrived later in a folder that was filed before: they are a new batch of their own
            }

            // A cut-off earlier run is finished from its journal, whatever the new plan says: the new plan may see the files the
            // earlier run already moved (a destination that exists is then not a duplicate, it is our own earlier work).
            bool resume = existing != null && (existing.State == "Applying" || existing.State == "Failed");
            if (!resume)
            {
                if (plan.IsDuplicate)
                {
                    return new ApplyResult { State = ApplyState.Duplicate, Message = string.Join(" ", plan.Problems) };
                }
                if (!plan.CanApply)
                {
                    return new ApplyResult { State = ApplyState.Failed, Message = string.Join(" ", plan.Problems) };
                }
            }
            if (dryRun)
            {
                return new ApplyResult { State = ApplyState.DryRun, Message = resume ? "Would finish the earlier, cut-off run." : $"Would tag and move {plan.Files.Count} file(s)." };
            }

            var journal = resume ? existing! : NewJournal(plan);
            if (laterBatch) journal.PreviousJournalId = _journals.Archive(existing!);
            return Run(journal);
        }

        /// <summary>Finishes an album whose earlier run was cut off or failed, from its journal alone.</summary>
        public ApplyResult Resume(string albumId)
        {
            var journal = _journals.Load(albumId);
            if (journal == null) return new ApplyResult { State = ApplyState.Failed, Message = "There is no record of this album." };
            if (journal.State == "Applied") return new ApplyResult { State = ApplyState.Applied, Message = "Already applied." };
            if (journal.State == "Undone") return new ApplyResult { State = ApplyState.Failed, Message = "This album was undone; plan it again." };
            return Run(journal);
        }

        private ApplyResult Run(AlbumJournal journal)
        {
            journal.State = "Applying";
            journal.Message = string.Empty;
            _journals.Save(journal);

            try
            {
                // Phase 1: new tags in every file, in the inbox, each checked by reading it back. If any file cannot be tagged
                // (read-only, damaged) nothing has reached the library yet.
                foreach (var file in journal.Files)
                {
                    if (file.Step == "Pending") TagInPlace(file, journal);
                }

                // Phase 1b: when the person chose "replace", the old library files are saved aside before any new file moves in.
                BackUpReplaced(journal);

                // Phase 2: move the files and their sidecars into the library.
                foreach (var file in journal.Files)
                {
                    if (file.Step == "Tagged") MoveFile(file, journal);
                    foreach (var sidecar in file.Sidecars) MoveSidecar(sidecar, journal);
                }
                foreach (var sidecar in journal.AlbumSidecars) MoveSidecar(sidecar, journal);

                // Phase 3: files the library already had are set aside, so the album folder can be tidied away.
                SetAsideDuplicates(journal);

                TidyInbox(journal);
                journal.State = "Applied";
                _journals.Save(journal);
                return new ApplyResult { State = ApplyState.Applied, Message = $"Tagged and moved {journal.Files.Count} file(s)." };
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is TagLib.CorruptFileException || e is TagLib.UnsupportedFormatException || e is InvalidOperationException)
            {
                journal.State = "Failed";
                journal.Message = PlainMessage(e);
                _journals.Save(journal);
                return new ApplyResult { State = ApplyState.Failed, Message = journal.Message };
            }
        }

        public UndoResult Undo(string albumId)
        {
            var journal = _journals.Load(albumId);
            if (journal == null) return new UndoResult { Success = false, Message = "There is no record of this album." };
            if (journal.State == "Undone") return new UndoResult { Success = true, Message = "Already undone." };

            try
            {
                // An undo must never leave the library without a track: if a saved-aside old file has gone missing, stop before anything moves.
                foreach (var old in journal.Replaced)
                {
                    if (old.Done && !_ops.FileExists(old.BackupPath) && _ops.FileExists(old.Destination))
                    {
                        return new UndoResult { Success = false, Message = $"\"{Path.GetFileName(old.Destination)}\" replaced an older file, and that older file is no longer in the inbox's _Replaced folder. Undoing would leave the library without it, so nothing was changed." };
                    }
                }

                foreach (var file in journal.Files)
                {
                    foreach (var sidecar in file.Sidecars) MoveSidecarBack(sidecar);
                    if (file.Step == "Moved")
                    {
                        if (!_ops.FileExists(file.Destination))
                        {
                            return new UndoResult { Success = false, Message = $"\"{Path.GetFileName(file.Destination)}\" is no longer in the library, so it cannot be put back." };
                        }
                        SafeMover.Move(_ops, file.Destination, file.Source, _forceCopy);
                    }
                    if (file.Step == "Moved" || file.Step == "Tagged")
                    {
                        TrackTagFile.Write(file.Source, file.OldTags, _tagOptions);
                    }
                    file.Step = "Pending";
                }
                foreach (var sidecar in journal.AlbumSidecars) MoveSidecarBack(sidecar);
                foreach (var backup in journal.Housekeeping)
                {
                    if (!_ops.FileExists(backup.Path))
                    {
                        _ops.CreateDirectory(Path.GetDirectoryName(backup.Path)!);
                        _ops.WriteAllBytes(backup.Path, Convert.FromBase64String(backup.Base64));
                    }
                }
                journal.Housekeeping.Clear();
                RestoreSetAside(journal);
                RestoreReplaced(journal);
                if (journal.AddedCover.Length > 0)
                {
                    if (_ops.FileExists(journal.AddedCover)) _ops.DeleteFile(journal.AddedCover);
                    journal.AddedCover = string.Empty;
                }
                RemoveEmptyLibraryFolders(journal);

                journal.State = "Undone";
                journal.Message = string.Empty;
                _journals.Save(journal);

                // An earlier batch of the same album folder is still filed: its record takes this one's place, so the next Undo steps back one more batch.
                if (journal.PreviousJournalId.Length > 0)
                {
                    var previous = _journals.LoadArchived(journal.PreviousJournalId);
                    if (previous != null)
                    {
                        _journals.Save(previous);
                        _journals.DeleteArchived(journal.PreviousJournalId);
                    }
                }
                return new UndoResult { Success = true, Message = "The album was put back in the inbox with its old tags." };
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new UndoResult { Success = false, Message = PlainMessage(e) };
            }
        }

        private static AlbumJournal NewJournal(AlbumPlan plan)
        {
            var journal = new AlbumJournal
            {
                AlbumId = plan.AlbumId,
                ReleaseId = plan.ReleaseId,
                InboxRoot = plan.InboxRoot,
                SourceFolder = plan.SourceFolder,
                LibraryRoot = plan.LibraryRoot,
            };
            foreach (var f in plan.Files)
            {
                var entry = new JournalFile { Source = f.Source, Destination = f.Destination, OldTags = f.OldTags, NewTags = f.NewTags };
                foreach (var s in f.Sidecars) entry.Sidecars.Add(new JournalSidecar { Source = s.Source, Destination = s.Destination });
                journal.Files.Add(entry);
            }
            foreach (var s in plan.AlbumSidecars) journal.AlbumSidecars.Add(new JournalSidecar { Source = s.Source, Destination = s.Destination });

            foreach (var aside in plan.SetAside)
            {
                journal.SetAside.Add(new SetAsideFile { Source = aside.Source, Target = SetAsidePathFor(plan, aside.Source) });
                foreach (var sidecar in aside.SidecarSources) journal.SetAside.Add(new SetAsideFile { Source = sidecar, Target = SetAsidePathFor(plan, sidecar) });
            }

            foreach (var f in plan.Files)
            {
                if (f.ReplacesExisting) journal.Replaced.Add(new ReplacedFile { Destination = f.Destination, BackupPath = BackupPathFor(plan, f.Destination) });
                foreach (var s in f.Sidecars.Where(x => x.ReplacesExisting))
                {
                    journal.Replaced.Add(new ReplacedFile { Destination = s.Destination, BackupPath = BackupPathFor(plan, s.Destination) });
                }
            }
            return journal;
        }

        /// <summary>Files the library already had wait under the inbox in _Duplicates/&lt;album id&gt;/, keeping their inbox-relative path.</summary>
        private static string SetAsidePathFor(AlbumPlan plan, string source) =>
            Path.Combine(plan.InboxRoot, "_Duplicates", plan.AlbumId, Path.GetRelativePath(plan.InboxRoot, source));

        /// <summary>Moves the files the library already had out of the album folder into the inbox's _Duplicates folder. Safe to run again.</summary>
        private void SetAsideDuplicates(AlbumJournal journal)
        {
            foreach (var entry in journal.SetAside.Where(e => !e.Done))
            {
                if (_ops.FileExists(entry.Source) && _ops.FileExists(entry.Target) && _ops.Length(entry.Source) == _ops.Length(entry.Target) && _ops.Sha256(entry.Source) == _ops.Sha256(entry.Target))
                {
                    _ops.DeleteFile(entry.Source); // the copy arrived and was checked, but the run was cut off before the original was removed
                }
                else if (_ops.FileExists(entry.Source))
                {
                    SafeMover.Move(_ops, entry.Source, entry.Target, _forceCopy);
                }
                entry.Done = true; // moved now, or by an earlier run that was cut off, or gone from the inbox
                _journals.Save(journal);
            }
        }

        /// <summary>Puts the set-aside files back where they were and tidies the _Duplicates folder.</summary>
        private void RestoreSetAside(AlbumJournal journal)
        {
            foreach (var entry in journal.SetAside)
            {
                if (entry.Done && _ops.FileExists(entry.Target) && !_ops.FileExists(entry.Source))
                {
                    SafeMover.Move(_ops, entry.Target, entry.Source, _forceCopy);
                }
                entry.Done = false;
            }
            if (journal.SetAside.Count == 0) return;

            var inbox = Path.GetFullPath(journal.InboxRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var entry in journal.SetAside)
            {
                var current = Path.GetDirectoryName(Path.GetFullPath(entry.Target)) ?? string.Empty;
                while (current.Length > inbox.Length && current.StartsWith(inbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_ops.DeleteDirectoryIfEmpty(current)) break;
                    current = Path.GetDirectoryName(current) ?? string.Empty;
                }
            }
        }

        /// <summary>Old library files saved aside by "replace" wait under the inbox in _Replaced/&lt;album id&gt;/, keeping their library-relative path.</summary>
        private static string BackupPathFor(AlbumPlan plan, string destination) =>
            Path.Combine(plan.InboxRoot, "_Replaced", plan.AlbumId, Path.GetRelativePath(plan.LibraryRoot, destination));

        /// <summary>
        /// Saves every old library file that is about to be replaced into the inbox's _Replaced folder. Runs after every new file has been tagged
        /// and before any new file moves, so a failure in between leaves the library as it was or has the old files safely aside. Safe to run again.
        /// </summary>
        private void BackUpReplaced(AlbumJournal journal)
        {
            foreach (var old in journal.Replaced.Where(r => !r.Done))
            {
                if (!_ops.FileExists(old.Destination))
                {
                    old.Done = true; // moved aside by an earlier run that was cut off before it noted so
                }
                else if (_ops.FileExists(old.BackupPath))
                {
                    if (_ops.Length(old.BackupPath) != _ops.Length(old.Destination) || _ops.Sha256(old.BackupPath) != _ops.Sha256(old.Destination))
                    {
                        throw new IOException($"\"{Path.GetFileName(old.Destination)}\" cannot be saved aside because a different file is already waiting in the _Replaced folder.");
                    }
                    _ops.DeleteFile(old.Destination); // the copy arrived and was checked, but the run was cut off before the original was removed
                    old.Done = true;
                }
                else
                {
                    SafeMover.Move(_ops, old.Destination, old.BackupPath, _forceCopy);
                    old.Done = true;
                }
                _journals.Save(journal);
            }
        }

        /// <summary>Puts the saved-aside old files back where they were and tidies the _Replaced folder.</summary>
        private void RestoreReplaced(AlbumJournal journal)
        {
            foreach (var old in journal.Replaced)
            {
                if (old.Done && _ops.FileExists(old.BackupPath) && !_ops.FileExists(old.Destination))
                {
                    SafeMover.Move(_ops, old.BackupPath, old.Destination, _forceCopy);
                }
                old.Done = false;
            }
            if (journal.Replaced.Count == 0) return;

            var inbox = Path.GetFullPath(journal.InboxRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var old in journal.Replaced)
            {
                var current = Path.GetDirectoryName(Path.GetFullPath(old.BackupPath)) ?? string.Empty;
                while (current.Length > inbox.Length && current.StartsWith(inbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_ops.DeleteDirectoryIfEmpty(current)) break;
                    current = Path.GetDirectoryName(current) ?? string.Empty;
                }
            }
        }

        /// <summary>Writes the new tags into the file where it is, then reads them back to be sure they were saved.</summary>
        private void TagInPlace(JournalFile file, AlbumJournal journal)
        {
            TrackTagFile.Write(file.Source, file.NewTags, _tagOptions);
            var check = TrackTagFile.Read(file.Source);
            if (check.Title != file.NewTags.Title || check.Album != file.NewTags.Album ||
                check.TrackNumber != file.NewTags.TrackNumber || check.DiscNumber != file.NewTags.DiscNumber ||
                check.ReleaseMbid != file.NewTags.ReleaseMbid)
            {
                throw new IOException($"The tags of \"{Path.GetFileName(file.Source)}\" did not read back as written.");
            }
            file.Step = "Tagged";
            _journals.Save(journal);
        }

        private void MoveFile(JournalFile file, AlbumJournal journal)
        {
            if (!_ops.FileExists(file.Source) && _ops.FileExists(file.Destination))
            {
                file.Step = "Moved"; // an earlier run finished this move before it was cut off
            }
            else if (_ops.FileExists(file.Source) && _ops.FileExists(file.Destination) &&
                     _ops.Length(file.Source) == _ops.Length(file.Destination) && _ops.Sha256(file.Source) == _ops.Sha256(file.Destination))
            {
                // The copy arrived and was checked, but the run was cut off before the source was deleted: finish that.
                _ops.DeleteFile(file.Source);
                file.Step = "Moved";
            }
            else
            {
                SafeMover.Move(_ops, file.Source, file.Destination, _forceCopy);
                file.Step = "Moved";
            }
            _journals.Save(journal);
        }

        private void MoveSidecar(JournalSidecar sidecar, AlbumJournal journal)
        {
            if (sidecar.Done) return;
            if (!_ops.FileExists(sidecar.Source) && _ops.FileExists(sidecar.Destination))
            {
                sidecar.Done = true;
            }
            else if (_ops.FileExists(sidecar.Source))
            {
                SafeMover.Move(_ops, sidecar.Source, sidecar.Destination, _forceCopy);
                sidecar.Done = true;
            }
            _journals.Save(journal);
        }

        private void MoveSidecarBack(JournalSidecar sidecar)
        {
            if (sidecar.Done && _ops.FileExists(sidecar.Destination) && !_ops.FileExists(sidecar.Source))
            {
                SafeMover.Move(_ops, sidecar.Destination, sidecar.Source, _forceCopy);
            }
            sidecar.Done = false;
        }

        /// <summary>
        /// After the audio has left: removes the album folder's leftover housekeeping files (MusicGrabber's .albuminfo, a second copy
        /// of the cover, Thumbs.db) and then the empty folder and its empty parents, never the lane's inbox itself. A folder that
        /// still holds anything else (a playlist, an unmatched track) is left alone.
        /// </summary>
        private void TidyInbox(AlbumJournal journal)
        {
            var folder = journal.SourceFolder;
            if (!_ops.DirectoryExists(folder)) return;

            var remaining = _ops.EnumerateFiles(folder).ToList();
            bool onlyHousekeeping = remaining.All(f => IsHousekeeping(f));
            if (!onlyHousekeeping) return;
            foreach (var f in remaining)
            {
                if (_ops.Length(f) <= 64 * 1024)
                {
                    journal.Housekeeping.Add(new HousekeepingBackup { Path = f, Base64 = Convert.ToBase64String(_ops.ReadAllBytes(f)) });
                }
                _ops.DeleteFile(f);
            }
            _journals.Save(journal);

            var inbox = Path.GetFullPath(journal.InboxRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (current.Length > inbox.Length && current.StartsWith(inbox + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                if (!_ops.DeleteDirectoryIfEmpty(current)) break;
                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
        }

        private static bool IsHousekeeping(string path)
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            if (JunkNames.Contains(name)) return true;
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            return CoverNames.Contains(stem) && (ext == ".jpg" || ext == ".jpeg" || ext == ".png");
        }

        private void RemoveEmptyLibraryFolders(AlbumJournal journal)
        {
            var root = Path.GetFullPath(journal.LibraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folders = journal.Files.Select(f => Path.GetDirectoryName(f.Destination)!).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var start in folders)
            {
                var current = Path.GetFullPath(start).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                while (current.Length > root.Length && current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (!_ops.DeleteDirectoryIfEmpty(current)) break; // other albums live here: leave it
                    current = Path.GetDirectoryName(current) ?? string.Empty;
                }
            }
        }

        /// <summary>A sentence a person can act on, without a stack trace.</summary>
        private static string PlainMessage(Exception e)
        {
            if (e is UnauthorizedAccessException) return "Access was denied. Check that the account running Emby can write to both folders. " + e.Message;
            return e.Message;
        }
    }
}
