using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace LabelMaker.Core.Pipeline
{
    /// <summary>
    /// Every file system call the pipeline makes, behind one interface so the tests can make any of them fail at a chosen moment
    /// (disk full, permission denied, the process killed mid-move) and check that nothing is lost.
    /// </summary>
    public interface IFileOps
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        void Move(string source, string destination);
        void Copy(string source, string destination);
        void DeleteFile(string path);

        /// <summary>Deletes the folder only when it is empty. Returns false when it still holds something.</summary>
        bool DeleteDirectoryIfEmpty(string path);

        IEnumerable<string> EnumerateFiles(string directory);
        IEnumerable<string> EnumerateDirectories(string directory);
        long Length(string path);
        string Sha256(string path);
        byte[] ReadAllBytes(string path);
        void WriteAllBytes(string path, byte[] bytes);

        /// <summary>True when both paths are on the same volume (or share), so a move is an instant rename.</summary>
        bool SameVolume(string a, string b);

        DateTime LastWriteUtc(string path);

        /// <summary>False while another program has the file open (a download still being written, a virus scan): the file is not finished.</summary>
        bool CanOpenForRead(string path);
    }

    public sealed class RealFileOps : IFileOps
    {
        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void Move(string source, string destination) => File.Move(source, destination);

        public void Copy(string source, string destination) => File.Copy(source, destination, overwrite: false);

        public void DeleteFile(string path) => File.Delete(path);

        public bool DeleteDirectoryIfEmpty(string path)
        {
            if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()) return false;
            Directory.Delete(path);
            return true;
        }

        public IEnumerable<string> EnumerateFiles(string directory) =>
            Directory.Exists(directory) ? Directory.EnumerateFiles(directory) : Enumerable.Empty<string>();

        public IEnumerable<string> EnumerateDirectories(string directory) =>
            Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : Enumerable.Empty<string>();

        public long Length(string path) => new FileInfo(path).Length;

        public string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

        public DateTime LastWriteUtc(string path) => File.GetLastWriteTimeUtc(path);

        public bool CanOpenForRead(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves one file without ever losing it. On the same volume it is a rename. Across volumes (a local disk to a network share)
    /// the file is copied to a temporary name next to its destination, checked (size and SHA-256), renamed into place, and only
    /// then is the source deleted. Nothing is overwritten.
    /// </summary>
    public static class SafeMover
    {
        public const string TempSuffix = ".labelmaker-tmp";

        public static void Move(IFileOps ops, string source, string destination, bool forceCopy = false)
        {
            if (ops.FileExists(destination)) throw new IOException("The destination already exists: " + destination);
            ops.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);

            if (!forceCopy && ops.SameVolume(source, destination))
            {
                ops.Move(source, destination);
                return;
            }

            var temp = destination + TempSuffix;
            if (ops.FileExists(temp)) ops.DeleteFile(temp); // left over from an earlier attempt that was cut off
            ops.Copy(source, temp);
            try
            {
                if (ops.Length(temp) != ops.Length(source) || ops.Sha256(temp) != ops.Sha256(source))
                {
                    throw new IOException("The copy does not match the original, so the original was kept: " + source);
                }
                ops.Move(temp, destination);
            }
            catch
            {
                if (ops.FileExists(temp)) ops.DeleteFile(temp);
                throw;
            }
            ops.DeleteFile(source);
        }
    }
}
