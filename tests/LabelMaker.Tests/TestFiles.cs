using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LabelMaker.Tests
{
    /// <summary>Fixture files from the Fixtures folder, and throw-away copies of them that are deleted after the test.</summary>
    public sealed class TestFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LabelMaker-tests-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _made = new List<string>();

        public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        /// <summary>A file inside the repository (found by walking up from the test folder to the one that holds docs and src).</summary>
        public static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !(Directory.Exists(Path.Combine(dir.FullName, "docs")) && Directory.Exists(Path.Combine(dir.FullName, "src")))) dir = dir.Parent;
            if (dir == null) throw new DirectoryNotFoundException("The repository folder was not found above the test folder.");
            return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        }

        /// <summary>A fresh copy of a fixture the test may change. The copy is removed by <see cref="Dispose"/>.</summary>
        public string Copy(string fixtureName, string asName)
        {
            Directory.CreateDirectory(_root);
            var target = Path.Combine(_root, asName);
            File.Copy(Fixture(fixtureName), target, overwrite: true);
            _made.Add(target);
            return target;
        }

        public void Dispose()
        {
            foreach (var path in _made)
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Nothing else to do: the temp folder is cleaned up by the OS.
                }
            }
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Same as above.
            }
        }
    }
}
