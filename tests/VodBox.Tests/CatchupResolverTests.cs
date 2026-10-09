using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class CatchupResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.FromHours(8));
    private static readonly Programme Past = new("c1", "节目", Now.AddHours(-2), Now.AddHours(-1));

    [Fact]
    public void ParsesAndResolvesExplicitUnixTimeTemplate()
    {
        const string m3u = "#EXTM3U\n#EXTINF:-1 catchup-days=\"7\" catchup-source=\"https://example.com/archive?start={utc}&end={utcend}&duration={duration}\",频道\nhttps://example.com/live";
        var channel = Assert.Single(Assert.Single(M3uParser.ParseGroups(m3u)).Channels);
        Assert.Equal(7, channel.CatchupDays);
        var url = CatchupResolver.Resolve(channel, Past, Now);
        Assert.Contains("start=" + Past.Start.ToUnixTimeSeconds(), url);
        Assert.Contains("duration=3600", url);
    }

    [Fact]
    public void FutureExpiredUnsupportedTemplatesAndProtocolsAreRejected()
    {
        var channel = new LiveChannel { Name = "频道", CatchupDays = 7, CatchupSource = "https://example.com/{utc}" };
        Assert.Throws<InvalidOperationException>(() => CatchupResolver.Resolve(channel, Past with { End = Now.AddHours(1) }, Now));
        Assert.Throws<InvalidOperationException>(() => CatchupResolver.Resolve(channel, Past with { Start = Now.AddDays(-8) }, Now));
        Assert.Throws<InvalidDataException>(() => CatchupResolver.Resolve(channel with { CatchupSource = "https://example.com/{unknown}" }, Past, Now));
        Assert.Throws<InvalidDataException>(() => CatchupResolver.Resolve(channel with { CatchupSource = "file:///secret" }, Past, Now));
    }
}
