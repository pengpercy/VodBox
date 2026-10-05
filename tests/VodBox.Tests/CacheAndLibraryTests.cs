using System.Net;
using System.Text.Json;
using VodBox.Application;
using Microsoft.Data.Sqlite;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class CacheAndLibraryTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(3, 2)]
    public void LiveRetryAttemptsEveryMirrorOnce(int count, int selected)
    {
        var policy = new LiveRetryPolicy(); policy.Reset(count, selected);
        var tried = new HashSet<int> { policy.Current };
        while (policy.TryAdvance(out int mirror)) Assert.True(tried.Add(mirror));
        Assert.Equal(count, tried.Count); Assert.False(policy.TryAdvance(out _));
        policy.Reset(count, selected); Assert.Equal(selected, policy.Current);
    }

    [Fact]
    public void OlderPreferencesRetainNewPlaybackDefaults()
    {
        var preferences = JsonSerializer.Deserialize("{\"volume\":35}", VodBoxJson.Default.AppPreferences)!;
        Assert.Equal(35, preferences.Volume); Assert.True(preferences.AutoLiveFallback); Assert.True(preferences.AutoNext);
        Assert.True(preferences.ResumePlayback); Assert.False(preferences.ResumeLiveOnStartup); Assert.Equal(1, preferences.Rate);
    }

    [Fact]
    public async Task PosterCacheReusesFilesAndEvictsOldEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-posters-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Posters(); using var http = new HttpClient(handler); using var cache = new PosterCache(http, directory, 8);
            string first = (await cache.GetAsync("https://images.example/one.png", default))!;
            Assert.Equal(first, await cache.GetAsync("https://images.example/one.png", default)); Assert.Equal(1, handler.Requests);
            File.SetLastAccessTimeUtc(first, DateTime.UtcNow.AddHours(-2));
            await cache.GetAsync("https://images.example/two.png", default);
            await cache.GetAsync("https://images.example/three.png", default);
            Assert.False(File.Exists(first)); Assert.Equal(2, Directory.GetFiles(directory, "*.image").Length);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Null(await cache.GetAsync("ftp://example.com/image.png", default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task HistoryDeletionMatchesFullIdentityAndClearsAllRecords()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LibraryStore(Path.Combine(directory, "library.db"));
            var entry = new HistoryEntry("a", "source", "movie", "episode", "title", "https://example.com", 42, DateTimeOffset.UtcNow);
            await store.SaveHistoryAsync(entry); await store.SaveHistoryAsync(entry with { ConfigId = "b" }); await store.SaveHistoryAsync(entry with { EpisodeId = "episode-2" });
            await store.DeleteHistoryAsync(entry); var remaining = await store.GetHistoryAsync(); Assert.Equal(2, remaining.Count);
            Assert.Contains(remaining, x => x.ConfigId == "b"); Assert.Contains(remaining, x => x.EpisodeId == "episode-2");
            await store.DeleteHistoryAsync(null); Assert.Empty(await store.GetHistoryAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private sealed class Posters : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) }); }
    }
}
