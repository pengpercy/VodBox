using System.Text.Json;
using MonoTorrent;
using MonoTorrent.Client;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.Playback.Mpv;

// Run from the repository root. An optional origin targets the public synthetic fixtures.
// Modes: apptt | appsx | livegz | tingshu | sixv | <csp_ApiName>  [http://127.0.0.1:port] [--decode] [--seek]
const string Usage = "Usage: Probe apptt|appsx|livegz|tingshu|sixv|<csp_ApiName> [http://127.0.0.1:port] [--decode] [--seek]";
if (args.Length < 1)
{
    Console.Error.WriteLine(Usage);
    return 2;
}
// 诊断：打印 fixture 中各站点解析出的 host（不含 token/路径），便于在线验收定位真实域名。
if (args[0] == "hosts")
{
    var hostConfig = ConfigLoader.ParseBytes(File.ReadAllBytes("tests/VodBox.Tests/fixtures/fty-in.bmp"))!;
    foreach (var site in ConfigLoader.ToSources(hostConfig))
        Console.WriteLine($"{site.Api}\t{site.Key}\t{ProbeHost(site.Ext)}");
    return 0;
}
static string ProbeHost(string? ext)
{
    if (string.IsNullOrWhiteSpace(ext)) return "";
    string text = ext.Trim();
    if (!(Uri.TryCreate(text, UriKind.Absolute, out _) || text.StartsWith('{')))
    {
        try { text = FtyExtCodec.JsonConfig(text); } catch (Exception e) when (e is InvalidDataException or FormatException) { return ""; }
    }
    if (text.StartsWith('{'))
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("siteUrl", out var url) && url.ValueKind == JsonValueKind.String)
                text = url.GetString() ?? "";
        }
        catch (JsonException) { }
    }
    return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.Host : "";
}
// 受控 swarm 自检：本地播种一个合成媒体磁力（LSD 本机互联），走完整的
// 磁力元数据 → 文件选择 → loopback Range → libmpv 解码/seek 路径。不依赖外部 peer。
if (args[0] == "torrent")
{
    bool torrentSeek = args.Contains("--seek");
    await using var torrentPreflight = new MpvEngine(headless: true);
    if (torrentPreflight.Client is null)
    {
        Console.Error.WriteLine("libmpv unavailable; set VODBOX_MPV_LIB to the installed library before decoding.");
        return 2;
    }
    string media = Path.GetFullPath("build/media-fixtures/h264-aac.mp4");
    if (!File.Exists(media)) { Console.Error.WriteLine($"missing fixture {media}"); return 2; }
    string seedDir = Path.Combine(Path.GetTempPath(), "vodbox-torrent-seed-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(seedDir);
    string seeded = Path.Combine(seedDir, Path.GetFileName(media));
    File.Copy(media, seeded, overwrite: true);
    using var seeder = new ClientEngine();
    var torrent = Torrent.Load(await new TorrentCreator().CreateAsync(new TorrentFileSource(seeded), CancellationToken.None));
    await seeder.AddAsync(torrent, seedDir);
    await seeder.StartAllAsync();
    var magnet = new MagnetLink(torrent.InfoHashes, torrent.Name, new List<string>(), Array.Empty<string>(), torrent.Size);
    Console.WriteLine($"SEED name={torrent.Name} size={torrent.Size}");
    using var torrentDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    string local = await TorrentStreamServer.Shared.RegisterAsync(magnet.ToV1String(), torrentDeadline.Token);
    foreach (var session in TorrentStreamServer.Shared.DescribeSessions())
        Console.WriteLine($"SESSION state={session.State} open={session.OpenConnections} seeds={session.Seeds} leechs={session.Leeches} known={session.KnownPeers} trackers={session.Trackers}");
    await using var torrentEngine = new MpvEngine(headless: true);
    using var torrentTimeout = CancellationTokenSource.CreateLinkedTokenSource(torrentDeadline.Token);
    torrentTimeout.CancelAfter(TimeSpan.FromSeconds(60));
    await torrentEngine.OpenAsync(new PlaybackRequest { Uri = local }, 1, torrentTimeout.Token);
    while (torrentEngine.Snapshot.Position.TotalSeconds < 1) await Task.Delay(200, torrentTimeout.Token);
    Console.WriteLine($"DECODE_OK position={torrentEngine.Snapshot.Position.TotalSeconds:F1} duration={torrentEngine.Snapshot.Duration.TotalSeconds:F1}");
    if (torrentSeek)
    {
        var target = TimeSpan.FromSeconds(3);
        await torrentEngine.SeekToAsync(target, torrentTimeout.Token);
        bool moved = false;
        for (int i = 0; i < 100 && !moved; i++)
        {
            await Task.Delay(200, torrentTimeout.Token);
            moved = torrentEngine.Snapshot.Position >= target - TimeSpan.FromSeconds(2);
        }
        if (!moved) throw new InvalidDataException("Seek did not advance playback position.");
        Console.WriteLine($"SEEK_OK position={torrentEngine.Snapshot.Position.TotalSeconds:F1} duration={torrentEngine.Snapshot.Duration.TotalSeconds:F1}");
    }
    await TorrentStreamServer.Shared.StopAllAsync();
    Console.WriteLine("PASS torrent-selfcheck");
    return 0;
}
string api = args[0] switch
{
    "apptt" => "csp_AppTTGuard",
    "appsx" => "csp_AppSxGuard",
    "livegz" => "csp_LiveGzGuard",
    "tingshu" => "csp_Tingshu275Guard",
    "sixv" => "csp_SixVGuard",
    _ => args[0],
};
bool sixv = api == "csp_SixVGuard";
if (args[0] is not ("apptt" or "appsx" or "livegz" or "tingshu" or "sixv") &&
    !NativeSpiders.TryGetFactory(args[0], out _))
{
    Console.Error.WriteLine(Usage);
    return 2;
}
string apiMode = sixv ? "sixv" : args[0];
string? origin = args.Skip(1).FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal));
if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback || endpoint.Scheme != "http"))
    throw new ArgumentException("Fixture origin must be a loopback HTTP address.");
bool seek = args.Contains("--seek");
bool decode = args.Contains("--decode") || seek;
if (origin is not null && decode) throw new ArgumentException("Synthetic media URLs cannot be decoded.");
if (decode)
{
    await using var preflight = new MpvEngine(headless: true);
    if (preflight.Client is null)
    {
        Console.Error.WriteLine("libmpv unavailable; set VODBOX_MPV_LIB to the installed library before decoding.");
        return 2;
    }
}
var config = ConfigLoader.ParseBytes(File.ReadAllBytes("tests/VodBox.Tests/fixtures/fty-in.bmp"))!;
var sites = ConfigLoader.ToSources(config).Where(site => site.Api == api).ToArray();
if (sites.Length == 0)
{
    Console.Error.WriteLine($"No site definition for api {api} in the FTY fixture.");
    return 2;
}
int failures = 0;
foreach (var saved in origin is null ? sites : sites.Take(1))
{
    string? ext = origin is null ? saved.Ext : apiMode == "appsx"
        ? JsonSerializer.Serialize(new Dictionary<string, string> { ["host"] = origin, ["key"] = "0123456789abcdef" }, Json.TypeInfo<Dictionary<string, string>>())
        : origin;
    var info = saved with { Ext = ext };
    string stage = "create";
    Console.WriteLine($"START {info.Key} api={api} mode={(origin is null ? "live" : "fixture")}");
    try
    {
        using var fixtureHttp = new HttpClient();
        var source = apiMode == "tingshu" && origin is not null
            ? new Tingshu275Source(saved, fixtureHttp, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new Uri(origin + "/listen/Apitzg2025/"))
            : (IResolvingContentSource)NativeSpiders.Create(info)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(sixv ? 180 : 90));
        stage = "categories";
        var categories = await source.GetCategoriesAsync(deadline.Token);
        Console.WriteLine($"CATEGORIES {categories.Count}");
        stage = "home";
        var home = await source.GetHomeAsync(deadline.Token);
        Console.WriteLine($"HOME {home.Items.Count}");
        stage = "list";
        var list = await source.GetItemsAsync(categories.First(category => category.Id != "0").Id, 1, null, deadline.Token);
        Console.WriteLine($"LIST {list.Items.Count}");
        var item = home.Items.FirstOrDefault() ?? list.Items.FirstOrDefault();
        if (item is null) throw new InvalidDataException("No item available for detail verification.");
        stage = "search";
        Console.WriteLine($"SEARCH {(await source.SearchAsync(origin is null ? item.Title : "中文", 1, deadline.Token)).Items.Count}");
        stage = "detail";
        var detail = await source.GetDetailAsync(item.Id, deadline.Token);
        Console.WriteLine($"LINES {detail.Lines.Count}");
        int resolved = 0, decoded = 0;
        foreach (var line in detail.Lines)
        {
            try
            {
                stage = "resolve";
                var play = await source.ResolvePlaybackAsync(item.Id, line.Episodes.First().Id, deadline.Token);
                resolved++;
                Console.WriteLine($"RESOLVED {line.Name} kind={play.Resolution} live={play.IsLive} headers={play.Headers.Count}");
                if (sixv) ReportSessions();
                if (!decode) continue;
                if (play.Resolution != ResolutionKind.Direct) continue;
                stage = "decode";
                await using var engine = new MpvEngine(headless: true);
                LocalControlServer? proxy = null;
                try
                {
                    // SixV 的解析结果本身就是回环磁力串流地址，直接交给 libmpv；其余站点经媒体代理解析防盗链。
                    var request = play;
                    if (!sixv)
                    {
                        proxy = new LocalControlServer();
                        await proxy.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask);
                        request = play with { Uri = proxy.RegisterMedia(play.Uri, play.Headers) };
                    }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(sixv ? 120 : 15));
                    await engine.OpenAsync(request, 1, timeout.Token);
                    while (engine.Snapshot.Position.TotalSeconds < 1) await Task.Delay(200, timeout.Token);
                    Console.WriteLine($"DECODE_OK {line.Name} position={engine.Snapshot.Position.TotalSeconds:F1}");
                    decoded++;
                    if (seek)
                    {
                        stage = "seek";
                        var duration = engine.Snapshot.Duration;
                        var target = duration > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(5);
                        await engine.SeekToAsync(target, timeout.Token);
                        var moved = false;
                        for (int i = 0; i < 100 && !moved; i++)
                        {
                            await Task.Delay(200, timeout.Token);
                            moved = engine.Snapshot.Position >= target - TimeSpan.FromSeconds(2);
                        }
                        if (!moved) throw new InvalidDataException("Seek did not advance playback position.");
                        if (sixv) ReportSessions();
                        Console.WriteLine($"SEEK_OK {line.Name} target={target.TotalSeconds:F0} position={engine.Snapshot.Position.TotalSeconds:F1} duration={duration.TotalSeconds:F1}");
                    }
                    break;
                }
                finally { if (proxy is not null) await proxy.DisposeAsync(); }
            }
            catch (Exception error) when (!deadline.IsCancellationRequested)
            {
                // Keep signed URLs, parser tokens and response bodies out of persistent logs.
                Console.WriteLine($"LINE_FAIL {line.Name} stage={stage} type={error.GetType().Name} msg={Brief(error.Message)}");
                if (origin is not null) throw;
            }
        }
        if (resolved == 0 || decode && decoded == 0) throw new InvalidDataException("No verified playback line.");
        Console.WriteLine($"PASS {info.Key}");
    }
    catch (Exception error)
    {
        failures++;
        string status = error is HttpRequestException { StatusCode: { } code } ? $" http={(int)code}" : "";
        Console.WriteLine($"FAIL {info.Key} stage={stage} type={error.GetType().Name}{status} msg={Brief(error.Message)}");
    }
}
return failures == 0 && sites.Length > 0 ? 0 : 1;

static string Brief(string message) => message.Length <= 140 ? message : message[..140] + "…";

// SixV 在线验收证据：记录当前串流会话的 peer/tracker 连接，不打印磁力链接或媒体 URL。
void ReportSessions()
{
    foreach (var session in TorrentStreamServer.Shared.DescribeSessions())
        Console.WriteLine($"SESSION state={session.State} open={session.OpenConnections} seeds={session.Seeds} leechs={session.Leeches} known={session.KnownPeers} trackers={session.Trackers}");
}
