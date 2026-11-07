using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>一次测速结果：分辨率档位、首个分片下载速度、清单延迟。</summary>
public sealed record SpeedTestResult(string Quality, double SpeedKBps, int PingMs, string? Error = null)
{
    public bool Failed => Error is not null;
    public string SpeedLabel => SpeedKBps <= 0 ? "未知" : SpeedKBps >= 1024 ? $"{SpeedKBps / 1024:0.0} MB/s" : $"{SpeedKBps:0} KB/s";
    public string Summary => Failed ? "测速失败" : string.Join(" · ", new[] { Quality == "未知" ? null : Quality, SpeedLabel, PingMs > 0 ? $"{PingMs}ms" : null }.Where(part => part is not null));
}

/// <summary>
/// 播放源测速与优选（LunaTV play 页 preferBestSource / getVideoResolutionFromM3u8）。
/// 桌面端不需要浏览器 video 元素：直接拉清单测延迟，主清单按 RESOLUTION 定档，下载首个分片测速度。
/// 评分权重与 LunaTV 一致：分辨率 40%、速度 40%（相对最快源线性映射）、延迟 20%（最低~最高线性映射）。
/// </summary>
public sealed partial class SourceSpeedTester : IDisposable
{
    private readonly HttpClient _http;
    /// <summary>清单阶段（含主清单→码流清单）的时限；超过即判定测速失败。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);
    /// <summary>分片测速窗口：窗口内读到多少算多少，读不到只记速度未知，不算失败。</summary>
    public static readonly TimeSpan MeasureWindow = TimeSpan.FromSeconds(3);
    public const int MaxConcurrent = 4;
    private const int SegmentBytes = 2 * 1024 * 1024;

    public SourceSpeedTester(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(4) })
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CmsClient.UserAgent);
    }

    public async Task<SpeedTestResult> TestAsync(string url, CancellationToken ct = default)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scope.CancelAfter(Timeout);
        try
        {
            var clock = Stopwatch.StartNew();
            var uri = new Uri(url);
            var (text, final) = await GetTextAsync(uri, scope.Token).ConfigureAwait(false);
            var ping = (int)clock.ElapsedMilliseconds;
            if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal))
                return new SpeedTestResult("未知", await MeasureAsync(final, ct).ConfigureAwait(false), ping);
            var quality = "未知";
            if (text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var (variant, width) = PickVariant(text, final);
                if (width > 0) quality = QualityOf(width);
                if (variant is null) return new SpeedTestResult(quality, 0, ping, "主清单没有可用码流");
                (text, final) = await GetTextAsync(variant, scope.Token).ConfigureAwait(false);
            }
            var segment = FirstSegment(text, final);
            var speed = segment is null ? 0 : await MeasureAsync(segment, ct).ConfigureAwait(false);
            if (quality == "未知" && Resolution().Match(text) is { Success: true } inline) quality = QualityOf(int.Parse(inline.Groups[1].Value, CultureInfo.InvariantCulture));
            return new SpeedTestResult(quality, speed, ping);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new SpeedTestResult("未知", 0, 0, error is OperationCanceledException ? "超时" : error.Message);
        }
    }

    private async Task<(string Text, Uri Final)> GetTextAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri ?? uri;
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentLength > 4 * 1024 * 1024) return ("", final);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = await BoundedContent.ReadAsync(stream, 4 * 1024 * 1024, ct).ConfigureAwait(false);
        return (TextEncoding.Decode(bytes), final);
    }

    /// <summary>在测速窗口内下载至多 2 MiB（KB/s）；窗口结束前读到的字节都计入，连不上返回 0（未知）。</summary>
    private async Task<double> MeasureAsync(Uri uri, CancellationToken outer)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(outer);
        window.CancelAfter(MeasureWindow);
        var ct = window.Token;
        var clock = Stopwatch.StartNew();
        long total = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, SegmentBytes - 1);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            clock.Restart();
            int read;
            while (total < SegmentBytes && (read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) total += read;
        }
        catch (Exception error) when (!outer.IsCancellationRequested && (error is OperationCanceledException or HttpRequestException or IOException))
        {
            if (total == 0) return 0;
        }
        var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        return total / 1024.0 / seconds;
    }

    internal static (Uri? Variant, int Width) PickVariant(string master, Uri baseUri)
    {
        Uri? best = null; long bestBandwidth = -1; var bestWidth = 0;
        var lines = master.Split('\n').Select(line => line.Trim()).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;
            var next = lines.Skip(i + 1).FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
            if (next is null || !Uri.TryCreate(baseUri, next, out var variant)) continue;
            var bandwidth = Bandwidth().Match(lines[i]) is { Success: true } b ? long.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var width = Resolution().Match(lines[i]) is { Success: true } r ? int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (bandwidth > bestBandwidth) { best = variant; bestBandwidth = bandwidth; bestWidth = width; }
        }
        return (best, bestWidth);
    }

    internal static Uri? FirstSegment(string playlist, Uri baseUri)
    {
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            return Uri.TryCreate(baseUri, line, out var segment) ? segment : null;
        }
        return null;
    }

    /// <summary>按宽度分档（与 LunaTV 一致）。</summary>
    public static string QualityOf(int width) => width >= 3840 ? "4K" : width >= 2560 ? "2K" : width >= 1920 ? "1080p" : width >= 1280 ? "720p" : width >= 854 ? "480p" : width > 0 ? "SD" : "未知";

    public static double QualityScore(string quality) => quality switch
    {
        "4K" => 100, "2K" => 85, "1080p" => 75, "720p" => 60, "480p" => 40, "SD" => 20, _ => 0,
    };

    /// <summary>LunaTV calculateSourceScore。</summary>
    public static double Score(SpeedTestResult result, double maxSpeed, int minPing, int maxPing)
    {
        var score = QualityScore(result.Quality) * 0.4;
        var speed = result.SpeedKBps <= 0 ? 30 : Math.Clamp(result.SpeedKBps / maxSpeed * 100, 0, 100);
        score += speed * 0.4;
        double ping;
        if (result.PingMs <= 0) ping = 0;
        else if (maxPing == minPing) ping = 100;
        else ping = Math.Clamp((maxPing - result.PingMs) / (double)(maxPing - minPing) * 100, 0, 100);
        score += ping * 0.2;
        return Math.Round(score, 2);
    }

    /// <summary>对一组候选并发测速（最多 4 路）并按评分排序；全部失败时保持原顺序。</summary>
    public async Task<IReadOnlyList<(T Item, SpeedTestResult Result, double Score)>> RankAsync<T>(IReadOnlyList<T> items, Func<T, string?> url,
        Action<T, SpeedTestResult>? progress = null, CancellationToken ct = default)
    {
        var results = new SpeedTestResult[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrent, CancellationToken = ct }, async (index, token) =>
        {
            var address = url(items[index]);
            results[index] = address is null ? new SpeedTestResult("未知", 0, 0, "没有可用地址") : await TestAsync(address, token).ConfigureAwait(false);
            progress?.Invoke(items[index], results[index]);
        }).ConfigureAwait(false);
        var ok = results.Where(result => !result.Failed).ToArray();
        var speeds = ok.Select(result => result.SpeedKBps).Where(speed => speed > 0).ToArray();
        var maxSpeed = speeds.Length > 0 ? speeds.Max() : 1024;
        var pings = ok.Select(result => result.PingMs).Where(ping => ping > 0).ToArray();
        var minPing = pings.Length > 0 ? pings.Min() : 50;
        var maxPing = pings.Length > 0 ? pings.Max() : 1000;
        return items.Select((item, index) => (item, results[index], results[index].Failed ? -1 : Score(results[index], maxSpeed, minPing, maxPing)))
            .OrderByDescending(entry => entry.Item3).ToArray();
    }

    [GeneratedRegex(@"RESOLUTION=(\d+)x(\d+)")] private static partial Regex Resolution();
    [GeneratedRegex(@"BANDWIDTH=(\d+)")] private static partial Regex Bandwidth();

    public void Dispose() => _http.Dispose();
}
