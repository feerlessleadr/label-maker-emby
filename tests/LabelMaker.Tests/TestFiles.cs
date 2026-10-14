using System;
using System.Collections.Generic;
using System.IO;

namespace LabelMaker.Tests
{
    /// <summary>Fixture files from the Fixtures folder, and throw-away copies of them that are deleted after the test.</summary>
    public sealed class TestFiles : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "LabelMaker-tests-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _made = new List<string>();

        public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

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
