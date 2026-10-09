using System.Xml;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class XmlTvParserTests
{
    [Fact]
    public async Task ProgrammeCacheRoundTripsUnicodeOffsetsAndDoesNotDependOnImportFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"programmes-{Guid.NewGuid():N}.xml");
        try
        {
            var store = new ProgrammeStore(path);
            Assert.Empty(await store.LoadAsync(ct: TestContext.Current.CancellationToken));
            var start = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.FromHours(8));
            var entry = new VodBox.Core.Programme("央1", "新闻 & <综合>", start, start.AddHours(1), "央视综合");
            await store.SaveAsync([entry], ct: TestContext.Current.CancellationToken);
            Assert.Equal(entry, Assert.Single(await new ProgrammeStore(path).LoadAsync(ct: TestContext.Current.CancellationToken)));
            await store.SaveAsync([], ct: TestContext.Current.CancellationToken);
            Assert.Empty(await store.LoadAsync(ct: TestContext.Current.CancellationToken));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ProgrammeLabelUpdatesNotifyBindings()
    {
        var channel = new VodBox.Core.LiveChannel { Name = "频道" };
        var changes = new List<string?>();
        channel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        channel.EpgNow = "当前节目";
        channel.EpgNow = "当前节目";
        channel.EpgNext = "下一节目";
        Assert.Equal(new[] { "EpgNow", "EpgNext" }, changes);
        Assert.IsAssignableFrom<System.ComponentModel.INotifyPropertyChanged>(channel);
    }

    [Fact]
    public void ParsesOffsetsChannelNamesAndSortsWithoutDuplicatingProgrammes()
    {
        const string xml = """
        <tv><channel id="c1"><display-name>综合</display-name></channel>
          <programme channel="c1" start="20261009010000 +0800" stop="20261009020000 +0800"><title>凌晨新闻</title></programme>
          <programme channel="c1" start="20261008160000 +0000" stop="20261008170000 +0000"><title>午夜新闻</title></programme>
          <programme channel="c1" start="20261009010000 +0800" stop="20261009020000 +0800"><title>凌晨新闻</title></programme>
        </tv>
        """;
        var result = XmlTvParser.Parse(xml);
        Assert.Equal(2, result.Count);
        Assert.Equal("午夜新闻", result[0].Title);
        Assert.Equal("综合", result[1].ChannelName);
        Assert.Equal(TimeSpan.FromHours(8), result[1].Start.Offset);
        Assert.Equal(result[0].End, result[1].Start);
    }

    [Fact]
    public void RejectsExternalEntitiesAndWrongDocumentType()
    {
        Assert.Throws<XmlException>(() => XmlTvParser.Parse("<!DOCTYPE tv [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><tv>&x;</tv>"));
        Assert.Throws<InvalidDataException>(() => XmlTvParser.Parse("<html/>"));
    }

    [Fact]
    public void InvalidOrUnzonedTimesAndEmptyTitlesAreSkipped()
    {
        const string xml = """
        <tv>
          <programme channel="c1" start="20261009010000" stop="20261009020000"><title>无时区</title></programme>
          <programme channel="c1" start="20261009020000 +0800" stop="20261009010000 +0800"><title>倒置</title></programme>
          <programme channel="c1" start="20261009010000 +0800" stop="20261009020000 +0800"><title> </title></programme>
          <programme channel="c1" start="20261009010000 +9999" stop="20261009020000 +9999"><title>坏时区</title></programme>
        </tv>
        """;
        Assert.Empty(XmlTvParser.Parse(xml));
    }
}
