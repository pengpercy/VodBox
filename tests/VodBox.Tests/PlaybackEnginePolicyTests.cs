using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

public sealed class PlaybackEnginePolicyTests
{
    [Theory]
    [InlineData("https://example.com/movie.m3u8", PlaybackEngineKind.Mpv)]
    [InlineData("file:///tmp/movie.mp4", PlaybackEngineKind.Mpv)]
    [InlineData("rtsp://example.com/live", PlaybackEngineKind.Mpv)]
    [InlineData("smb://server/share/movie.mp4", PlaybackEngineKind.LibVlc)]
    [InlineData("ftp://server/movie.mp4", PlaybackEngineKind.LibVlc)]
    [InlineData("sftp://server/movie.mp4", PlaybackEngineKind.LibVlc)]
    [InlineData("nfs://server/movie.mp4", PlaybackEngineKind.LibVlc)]
    public void AutomaticSelectionUsesMediaNeeds(string location, PlaybackEngineKind expected) =>
        Assert.Equal(expected, PlaybackEnginePolicy.Select(PlaybackEngineMode.Automatic, location).Kind);

    [Fact]
    public void CastingAndBrowsingChooseVlcEvenForHttp()
    {
        foreach (var needs in new[] { PlaybackNeeds.Casting, PlaybackNeeds.NetworkBrowsing })
        {
            Assert.Equal(PlaybackEngineKind.LibVlc, PlaybackEnginePolicy.Select(PlaybackEngineMode.Automatic, "https://example.com/movie", needs).Kind);
            Assert.Null(PlaybackEnginePolicy.Fallback(PlaybackEngineMode.Automatic, PlaybackEngineKind.LibVlc, "https://example.com/movie", needs));
            Assert.Throws<NotSupportedException>(() => PlaybackEnginePolicy.Select(PlaybackEngineMode.Mpv, "https://example.com/movie", needs));
        }
    }
    [Fact]
    public void ManualSelectionDoesNotFallback()
    {
        Assert.Equal(PlaybackEngineKind.LibVlc, PlaybackEnginePolicy.Select(PlaybackEngineMode.LibVlc, "https://example.com/movie").Kind);
        Assert.Equal(PlaybackEngineKind.Mpv, PlaybackEnginePolicy.Select(PlaybackEngineMode.Mpv, "https://example.com/movie").Kind);
        Assert.Null(PlaybackEnginePolicy.Fallback(PlaybackEngineMode.Mpv, PlaybackEngineKind.Mpv, "https://example.com/movie"));
        Assert.Null(PlaybackEnginePolicy.Fallback(PlaybackEngineMode.LibVlc, PlaybackEngineKind.LibVlc, "https://example.com/movie"));
    }
    [Fact]
    public void AutomaticPlaybackHasAlternateButNetworkFilesDoNot()
    {
        Assert.Equal(PlaybackEngineKind.LibVlc, PlaybackEnginePolicy.Fallback(PlaybackEngineMode.Automatic, PlaybackEngineKind.Mpv, "https://example.com/movie"));
        Assert.Null(PlaybackEnginePolicy.Fallback(PlaybackEngineMode.Automatic, PlaybackEngineKind.LibVlc, "smb://server/movie"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackEnginePolicy.Select((PlaybackEngineMode)99, "https://example.com/movie"));
    }
}
