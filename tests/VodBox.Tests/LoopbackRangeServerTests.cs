using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class LoopbackRangeServerTests
{
    [Theory]
    [InlineData("bytes=0-99", 0, 99)]
    [InlineData("bytes=100-", 100, 999)]
    [InlineData("bytes=-200", 800, 999)]
    [InlineData("bytes=-5000", 0, 999)]
    [InlineData("bytes=900-5000", 900, 999)]
    public void AcceptsSingleByteRangesClampedToTheFile(string header, long start, long end)
    {
        Assert.True(LoopbackRangeServer.ParseRange(header, 1000, out var actualStart, out var actualEnd));
        Assert.Equal((start, end), (actualStart, actualEnd));
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=50-10")]
    [InlineData("bytes=0-10,20-30")]
    [InlineData("bytes=-0")]
    [InlineData("items=0-10")]
    [InlineData("bytes=+1-10")]
    [InlineData("bytes=a-b")]
    public void RejectsUnsatisfiableOrMultipartRanges(string header) =>
        Assert.False(LoopbackRangeServer.ParseRange(header, 1000, out _, out _));

    [Fact]
    public void EmptyFilesHaveNoSatisfiableRange() =>
        Assert.False(LoopbackRangeServer.ParseRange("bytes=0-", 0, out _, out _));

    [Fact]
    public void HtmlRegexReusesOneInstancePerPattern() =>
        Assert.Same(HtmlRegex.Get("<a\\b[^>]*>"), HtmlRegex.Get("<a\\b[^>]*>"));
}
