using System.Collections.Generic;
using LabelMaker.Core.Pipeline;

namespace LabelMaker.Core.Review
{
    /// <summary>The answer to a review action. <see cref="Message"/> is written for the person using the review screen.</summary>
    public sealed class ReviewResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>The album's queue state after the action.</summary>
        public string State { get; set; } = string.Empty;

        public static ReviewResult Fail(string message, string state = "") => new ReviewResult { Success = false, Message = message, State = state };
    }

    /// <summary>A person's answer to "which track is this file?": the file (path below the album folder), the disc and the position on it.</summary>
    public sealed class MappingEntry
    {
        public string File { get; set; } = string.Empty;
        public int Disc { get; set; } = 1;
        public int Position { get; set; }
    }

    public sealed class PreviewFile
    {
        /// <summary>The file's path below the album folder.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Where it would go, below the library folder. Empty when it has no track on the release.</summary>
        public string Destination { get; set; } = string.Empty;
        public int Disc { get; set; }
        public int Position { get; set; }
        public string Title { get; set; } = string.Empty;
        public double Score { get; set; }

        /// <summary>No track on the release, or fits it poorly (and a person did not place it). The screen shows these first.</summary>
        public bool Weak { get; set; }
    }

    /// <summary>What would happen if one edition were used for an album: shown before anything is changed.</summary>
    public sealed class ReviewPreview
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ReleaseId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public double Score { get; set; }
        public List<PreviewFile> Files { get; } = new List<PreviewFile>();
        public List<string> Problems { get; } = new List<string>();
        public bool CanApply { get; set; }
        public bool IsDuplicate { get; set; }
        public string Destination { get; set; } = string.Empty;
    }

    /// <summary>Where an album got a candidate from, shown next to it on the review screen.</summary>
    public sealed class ReviewCandidates
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<CandidateSummary> Candidates { get; } = new List<CandidateSummary>();
    }
}
