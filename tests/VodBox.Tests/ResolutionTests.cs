using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class ResolutionTests
{
    [Fact]
    public void SourceGeneratedConfigurationKeepsOptionalDefaults()
    {
        var config = JsonSerializer.Deserialize("{\"resolvers\":[{\"id\":\"p\",\"name\":\"parser\",\"entry\":\"https://example.com/?url={url}\"}]}", VodBoxJson.Default.VodBoxConfig)!;
        Assert.Empty(config.Sources); Assert.Empty(config.LiveSources);
        var parser = Assert.Single(config.Resolvers); Assert.Equal(ResolutionKind.Json, parser.Kind);
        Assert.Equal("data.url", parser.UrlPath); Assert.Equal(20, parser.TimeoutSeconds); Assert.Empty(parser.Headers);
    }

    [Fact]
    public async Task JsonChainEscapesInputAndKeepsOriginalAddress()
    {
        using var http = new HttpClient(new Parser());
        await using var resolver = new PlaybackResolutionService(http);
        resolver.Configure(new() { Resolvers = [new() { Id = "json", Name = "解析", Entry = "https://parser.example/?url={url}" }] });
        const string input = "https://page.example/watch?id=7&title=中文";
        var result = await resolver.ResolveAsync(new() { Uri = input, ResolverId = "json" }, default);
        Assert.Equal(input, result.OriginalUri);
        Assert.Equal("https://media.example/movie.mp4", result.Uri);
        Assert.Equal("Fixture", result.Headers["User-Agent"]);
        resolver.Configure(new() { Resolvers = [new() { Id = "cycle", Name = "循环", Kind = ResolutionKind.Direct, NextResolverId = "cycle" }] });
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync(new() { Uri = input, ResolverId = "cycle" }, default));
    }

    [Fact]
    public async Task ProxyRewritesHlsKeysAndPreservesByteRanges()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/folder/list.m3u8";
        var upstream = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var client = await origin.AcceptTcpClientAsync(); var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var lines = new List<string>(); string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) lines.Add(line);
                Assert.Contains(lines, x => x.Equals("Authorization: Bearer fixture", StringComparison.OrdinalIgnoreCase));
                string body; string status; string extra = "";
                if (i == 0) { body = "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\nsegment.ts\n"; status = "200 OK"; }
                else if (i == 1) { Assert.Contains("/folder/key.bin", lines[0]); body = "key"; status = "200 OK"; }
                else { Assert.Contains("/folder/segment.ts", lines[0]); Assert.Contains("Range: bytes=2-4", lines); body = "234"; status = "206 Partial Content"; extra = "Content-Range: bytes 2-4/10\r\n"; }
                string mime = i == 0 ? "application/vnd.apple.mpegurl" : "application/octet-stream";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {mime}\r\nContent-Length: {body.Length}\r\n{extra}Connection: close\r\n\r\n{body}"));
            }
        });
        await using var proxy = new MediaProxy(); var request = proxy.Register(new() { Uri = url, Headers = new() { ["Authorization"] = "Bearer fixture" } });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string playlist = await http.GetStringAsync(request.Uri); string key = playlist.Split('"')[1];
        Assert.Equal("key", await http.GetStringAsync(key));
        string segment = playlist.Split('\n').First(x => x.StartsWith("http", StringComparison.Ordinal));
        using var range = new HttpRequestMessage(HttpMethod.Get, segment); range.Headers.Range = new(2, 4);
        using var response = await http.SendAsync(range); Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 2-4/10", response.Content.Headers.ContentRange!.ToString()); Assert.Equal("234", await response.Content.ReadAsStringAsync());
        await upstream.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(url, request.OriginalUri);
    }

    [Fact]
    public async Task MigratesPreviousDatabaseWithoutLosingHistory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vodbox-migration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "library.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE history(config TEXT,source TEXT,media TEXT,episode TEXT,title TEXT NOT NULL,uri TEXT NOT NULL,position INTEGER NOT NULL,updated TEXT NOT NULL,PRIMARY KEY(config,source,media,episode)); INSERT INTO history VALUES('a','s','m','e','old','https://example.com',123,'2026-10-04T00:00:00Z'); PRAGMA user_version=1;";
                command.ExecuteNonQuery();
            }
            var store = new LibraryStore(path); var old = Assert.Single(await store.GetHistoryAsync()); Assert.Equal(123, old.PositionMs);
            await store.SaveHistoryAsync(old with { ResolutionKind = ResolutionKind.Json, ResolverId = "parser" });
            var saved = Assert.Single(await store.GetHistoryAsync()); Assert.Equal("parser", saved.ResolverId); Assert.Equal(ResolutionKind.Json, saved.ResolutionKind);
            _ = new LibraryStore(path);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private sealed class Parser : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Contains("%26title%3D", request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{\"data\":{\"url\":\"https://media.example/movie.mp4\",\"headers\":{\"User-Agent\":\"Fixture\"}}}") });
        }
    }
}
