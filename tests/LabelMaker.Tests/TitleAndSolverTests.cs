using LabelMaker.Core.Matching;
using Xunit;

namespace LabelMaker.Tests
{
    public class TitleNormaliserTests
    {
        [Theory]
        [InlineData("Don’t Stop", "Dont Stop")]
        [InlineData("Don't Stop", "Dont Stop")]
        [InlineData("Rock & Roll", "Rock and Roll")]
        [InlineData("Beyoncé", "Beyonce")]
        [InlineData("HALLELUJAH", "hallelujah")]
        [InlineData("Hello,  World!", "hello world")]
        public void TitlesThatDifferOnlyInStyleAreTheSame(string a, string b)
        {
            Assert.Equal(1.0, TitleNormaliser.Similarity(a, b));
        }

        [Theory]
        [InlineData("Hotel California (Remastered 2013)", "Hotel California")]
        [InlineData("Hallelujah - Live", "Hallelujah")]
        [InlineData("Song [Explicit]", "Song")]
        public void ATrailingRemarkLowersTheScoreButKeepsItHigh(string a, string b)
        {
            var s = TitleNormaliser.Similarity(a, b);
            Assert.InRange(s, 0.9, 0.999);
        }

        [Fact]
        public void DifferentSongsScoreLow()
        {
            Assert.True(TitleNormaliser.Similarity("Hallelujah", "Hercules") < 0.5);
        }

        [Fact]
        public void EmptyTitlesScoreZero()
        {
            Assert.Equal(0.0, TitleNormaliser.Similarity("", "Song"));
            Assert.Equal(0.0, TitleNormaliser.Similarity("Song", null!));
        }
    }

    public class AssignmentSolverTests
    {
        [Fact]
        public void TheBestTotalBeatsTheGreedyChoice()
        {
            // Greedy would take 0.9 for file 0 and leave 0.1 for file 1 (total 1.0); the best pairing totals 1.65.
            var scores = new[,] { { 0.9, 0.8 }, { 0.85, 0.1 } };
            Assert.Equal(new[] { 1, 0 }, AssignmentSolver.MaximiseScore(scores));
        }

        [Fact]
        public void FewerFilesThanTracksLeavesTracksUnused()
        {
            var scores = new[,] { { 0.1, 0.9, 0.2 } };
            Assert.Equal(new[] { 1 }, AssignmentSolver.MaximiseScore(scores));
        }

        [Fact]
        public void MoreFilesThanTracksLeavesTheWeakestFileWithNothing()
        {
            var scores = new[,] { { 0.9, 0.1 }, { 0.8, 0.2 }, { 0.1, 0.7 } };
            Assert.Equal(new[] { 0, -1, 1 }, AssignmentSolver.MaximiseScore(scores));
        }

        [Fact]
        public void EmptyInputGivesEmptyOutput()
        {
            Assert.Empty(AssignmentSolver.MaximiseScore(new double[0, 0]));
            Assert.Equal(new[] { -1 }, AssignmentSolver.MaximiseScore(new double[1, 0]));
        }
    }
}
