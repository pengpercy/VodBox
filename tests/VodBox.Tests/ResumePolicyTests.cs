using VodBox.Core;
using Xunit;
namespace VodBox.Tests;
public sealed class ResumePolicyTests
{
    [Theory]
    [InlineData(0, 90000, 0)]
    [InlineData(-1, 90000, 0)]
    [InlineData(45000, 90000, 45000)]
    [InlineData(89999, 90000, 89999)]
    [InlineData(90000, 90000, 0)]
    [InlineData(95000, 90000, 0)]
    [InlineData(45000, 0, 45000)]
    public void ValidatesLegacyResumePositions(long position, long duration, long expected) =>
        Assert.Equal(expected, ResumePolicy.Position(position, duration));
}
