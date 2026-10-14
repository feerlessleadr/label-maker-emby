using System;
using System.Text.RegularExpressions;

namespace LabelMaker.Core.Review
{
    public enum ReferenceKind
    {
        /// <summary>Nothing that looks like a MusicBrainz release or release group.</summary>
        None,

        /// <summary>A bare id: could be a release or a release group, so both are tried.</summary>
        Unknown,
        Release,
        ReleaseGroup,
    }

    /// <summary>What a person pasted into "use this MusicBrainz album": a link to a release or release group, or a bare id.</summary>
    public readonly struct ReleaseReference
    {
        private static readonly Regex Link = new Regex(@"musicbrainz\.org/(release-group|release)/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex Bare = new Regex(@"^\s*([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public ReleaseReference(ReferenceKind kind, string id)
        {
            Kind = kind;
            Id = id;
        }

        public ReferenceKind Kind { get; }

        /// <summary>The id in lower case, or empty.</summary>
        public string Id { get; }

        public static ReleaseReference Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new ReleaseReference(ReferenceKind.None, string.Empty);
            var link = Link.Match(text);
            if (link.Success)
            {
                var kind = link.Groups[1].Value == "release-group" ? ReferenceKind.ReleaseGroup : ReferenceKind.Release;
                return new ReleaseReference(kind, link.Groups[2].Value.ToLowerInvariant());
            }
            var bare = Bare.Match(text);
            if (bare.Success) return new ReleaseReference(ReferenceKind.Unknown, bare.Groups[1].Value.ToLowerInvariant());
            return new ReleaseReference(ReferenceKind.None, string.Empty);
        }
    }
}
