using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LabelMaker.Core.Pipeline;

namespace LabelMaker.Core.CoverArt
{
    /// <summary>
    /// After an album has been filed: when its library folder holds no cover image, downloads the release's front cover and saves it as
    /// "cover.jpg" (or ".png") next to the music, where Emby looks for it. A cover that came with the download or was already in the library
    /// is never replaced. The file is recorded in the album's journal so Undo removes it again. Failing to get a cover never fails the album.
    /// </summary>
    public sealed class CoverArtFiller
    {
        private static readonly string[] CoverNames = { "cover", "folder", "albumart" };
        private static readonly string[] CoverExtensions = { ".jpg", ".jpeg", ".png" };

        private readonly ICoverArt _coverArt;
        private readonly IFileOps _ops;
        private readonly JournalStore _journals;

        public CoverArtFiller(ICoverArt coverArt, IFileOps ops, JournalStore journals)
        {
            _coverArt = coverArt;
            _ops = ops;
            _journals = journals;
        }

        /// <summary>Returns a short sentence when a cover was added ("Cover art added."), otherwise an empty string.</summary>
        public async Task<string> AddIfMissingAsync(string albumId, CancellationToken cancellation)
        {
            try
            {
                var journal = _journals.Load(albumId);
                if (journal == null || journal.State != "Applied" || journal.AddedCover.Length > 0) return string.Empty;
                var first = journal.Files.FirstOrDefault(f => f.Step == "Moved");
                if (first == null) return string.Empty;

                var folder = Path.GetDirectoryName(first.Destination)!;
                if (!_ops.DirectoryExists(folder)) return string.Empty;
                if (_ops.EnumerateFiles(folder).Any(IsCover)) return string.Empty;

                var image = await _coverArt.GetFrontAsync(journal.ReleaseId, cancellation).ConfigureAwait(false);
                if (image == null) return string.Empty;

                var destination = Path.Combine(folder, "cover" + image.Extension);
                if (_ops.FileExists(destination)) return string.Empty;
                _ops.WriteAllBytes(destination, image.Bytes);
                journal.AddedCover = destination;
                _journals.Save(journal);
                return "Cover art added.";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return string.Empty; // a library folder that cannot take one more file does not undo a filed album
            }
        }

        private static bool IsCover(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return CoverNames.Contains(stem) && CoverExtensions.Contains(ext);
        }
    }
}
