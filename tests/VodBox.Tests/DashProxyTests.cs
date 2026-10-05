using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using VodBox.Core;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class DashProxyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DashNestedBaseUrlsAndTemplatesReachOriginalSegmentDirectory(bool inherited)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/dash/manifest.mpd";
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var client = await origin.AcceptTcpClientAsync(); var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string request = (await reader.ReadLineAsync())!; var headers = new List<string>(); string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync())) headers.Add(header);
                Assert.Contains(headers, x => x.Equals("Authorization: Bearer dash", StringComparison.OrdinalIgnoreCase));
                string body = i == 0 ? "<MPD xmlns='urn:mpeg:dash:schema:mpd:2011'><BaseURL>assets/</BaseURL><Period><BaseURL>video/</BaseURL><AdaptationSet><Representation id='main'><SegmentTemplate media='$RepresentationID$/chunk-$Number%05d$.m4s?key=fixture' initialization='init.mp4' /></Representation></AdaptationSet></Period></MPD>" : "segment";
                if (i == 0 && inherited)
                    body = "<MPD xmlns='urn:mpeg:dash:schema:mpd:2011'><BaseURL>assets/</BaseURL><Period><AdaptationSet><SegmentTemplate media='$RepresentationID$/chunk-$Number%05d$.m4s?key=fixture' initialization='init.mp4' /><Representation id='main'><BaseURL>video/</BaseURL></Representation></AdaptationSet></Period></MPD>";
                if (i == 1) Assert.Contains("/dash/assets/video/main/chunk-00001.m4s?key=fixture", request);
                byte[] data = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(i == 0 ? "application/dash+xml" : "video/mp4")}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n")); await stream.WriteAsync(data);
            }
        });
        await using var proxy = new MediaProxy(); var request = proxy.Register(new() { Uri = url, Headers = new() { ["Authorization"] = "Bearer dash" } });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var manifest = XDocument.Parse(await http.GetStringAsync(request.Uri)); var template = manifest.Descendants().Single(x => x.Name.LocalName == "Representation").Elements().Single(x => x.Name.LocalName == "SegmentTemplate");
        string media = template.Attribute("media")!.Value; Assert.Contains("$RepresentationID$", media); Assert.Contains("$Number%05d$", media);
        string segment = media.Replace("$RepresentationID$", "main").Replace("$Number%05d$", "00001");
        Assert.Equal("segment", await http.GetStringAsync(segment)); await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(manifest.Descendants().Where(x => x.Name.LocalName == "BaseURL"), x => Assert.StartsWith("http://127.0.0.1:", x.Value));
    }
}
