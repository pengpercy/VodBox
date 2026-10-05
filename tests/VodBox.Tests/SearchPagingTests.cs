using System.Net;
using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class SearchPagingTests
{
    [Fact]
    public async Task CollectionSearchRetainsQueryWhileFollowingServerCursor()
    {
        using var http = new HttpClient(new Pages());
        await using var provider = new ProviderFactory(http, "unused", "unused").Create(new() { Id = "site", Name = "site", Provider = "maccms-json", Entry = "https://api.example/?token=fixture", ResolverId = "_direct" });
        var first = await provider.SearchPageAsync("movie & 中文", null, default);
        Assert.Equal("1", Assert.Single(first.Items).Id); Assert.Equal("2", first.NextCursor);
        var second = await provider.SearchPageAsync("movie & 中文", first.NextCursor, default);
        Assert.Equal("2", Assert.Single(second.Items).Id); Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task CollectionFiltersReplaceEndpointDefaultsWithoutChangingCredentials()
    {
        using var http = new HttpClient(new Filters());
        await using var provider = new ProviderFactory(http, "unused", "unused").Create(new() { Id = "site", Name = "site", Provider = "maccms-json", Entry = "https://api.example/?token=fixture&year=2000&isend=1", ResolverId = "_direct" });
        var result = await provider.GetItemsFilteredAsync("7", "2", new Dictionary<string, string> { ["year"] = "2024", ["isend"] = "0" }, default);
        Assert.Empty(result.Items);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetItemsFilteredAsync(null, null, new Dictionary<string, string> { ["ac"] = "list" }, default));
    }

    [Fact]
    public void ScriptFilterArgumentsUseGeneratedJsonMetadata()
    {
        var element = JsonSerializer.SerializeToElement(new ScriptParams(CategoryId: "7", Cursor: "2", Filters: new() { ["year"] = "2024" }), VodBoxJson.Default.ScriptParams);
        Assert.Equal("2024", element.GetProperty("filters").GetProperty("year").GetString()); Assert.Equal("2", element.GetProperty("cursor").GetString());
    }

    [Fact]
    public void SourceAndPlaybackWireModelsPreserveOptionalDefaults()
    {
        using var document = JsonDocument.Parse("{\"sources\":[{\"id\":\"s\",\"name\":\"source\"}]}");
        var config = JsonSerializer.Deserialize(document.RootElement, VodBoxJson.Default.VodBoxConfig)!;
        Assert.Equal("catalog", config.Sources[0].Provider); Assert.Empty(config.Sources[0].Options);
        var request = JsonSerializer.Deserialize("{\"uri\":\"https://example.com\"}", VodBoxJson.Default.PlaybackRequest)!;
        Assert.Empty(request.Headers); Assert.Empty(request.Subtitles); Assert.Equal("local", request.SourceId); Assert.Equal("媒体", request.Title);
    }

    private sealed class Filters : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string query = request.RequestUri!.Query;
            Assert.Contains("token=fixture", query); Assert.Contains("year=2024", query); Assert.Contains("isend=0", query); Assert.Contains("t=7", query); Assert.Contains("pg=2", query);
            Assert.DoesNotContain("year=2000", query); Assert.DoesNotContain("isend=1", query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"page\":2,\"pagecount\":2,\"list\":[]}") });
        }
    }

    private sealed class Pages : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Contains("wd=movie%20%26%20%E4%B8%AD%E6%96%87", request.RequestUri!.Query); Assert.Contains("token=fixture", request.RequestUri.Query);
            int page = request.RequestUri.Query.Contains("pg=2", StringComparison.Ordinal) ? 2 : 1;
            string json = "{\"page\":" + page + ",\"pagecount\":2,\"list\":[{\"vod_id\":" + page + ",\"vod_name\":\"movie\",\"vod_play_url\":\"main$https://example.com/video.mp4\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
