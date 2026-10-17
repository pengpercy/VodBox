using System.Text.Json;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.Playback.Mpv;

// Run from the repository root. An optional origin targets the public synthetic fixtures.
if (args.Length < 1 || args[0] is not ("apptt" or "appsx" or "livegz" or "tingshu"))
{
    Console.Error.WriteLine("Usage: Probe apptt|appsx|livegz|tingshu [http://127.0.0.1:port] [--decode]");
    return 2;
}
string api = args[0] switch { "apptt" => "csp_AppTTGuard", "appsx" => "csp_AppSxGuard", "tingshu" => "csp_Tingshu275Guard", _ => "csp_LiveGzGuard" };
string? origin = args.Skip(1).FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal));
if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback || endpoint.Scheme != "http"))
    throw new ArgumentException("Fixture origin must be a loopback HTTP address.");
bool decode = args.Contains("--decode");
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
int failures = 0;
foreach (var saved in origin is null ? sites : sites.Take(1))
{
    string? ext = origin is null ? saved.Ext : args[0] == "appsx"
        ? JsonSerializer.Serialize(new Dictionary<string, string> { ["host"] = origin, ["key"] = "0123456789abcdef" }, Json.TypeInfo<Dictionary<string, string>>())
        : origin;
    var info = saved with { Ext = ext };
    string stage = "create";
    Console.WriteLine($"START {info.Key} mode={(origin is null ? "live" : "fixture")}");
    try
    {
        using var fixtureHttp = new HttpClient();
        var source = args[0] == "tingshu" && origin is not null
            ? new Tingshu275Source(saved, fixtureHttp, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new Uri(origin + "/listen/Apitzg2025/"))
            : (IResolvingContentSource)NativeSpiders.Create(info)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
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
                if (!decode) continue;
                if (play.Resolution != ResolutionKind.Direct) continue;
                stage = "decode";
                await using var proxy = new LocalControlServer();
                await proxy.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask);
                await using var engine = new MpvEngine(headless: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await engine.OpenAsync(play with { Uri = proxy.RegisterMedia(play.Uri, play.Headers) }, 1, timeout.Token);
                while (engine.Snapshot.Position.TotalSeconds < 1) await Task.Delay(200, timeout.Token);
                Console.WriteLine($"DECODE_OK {line.Name} position={engine.Snapshot.Position.TotalSeconds:F1}");
                decoded++;
                break;
            }
            catch (Exception error) when (!deadline.IsCancellationRequested)
            {
                // Keep signed URLs, parser tokens and response bodies out of persistent logs.
                Console.WriteLine($"LINE_FAIL {line.Name} stage={stage} type={error.GetType().Name}");
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
        Console.WriteLine($"FAIL {info.Key} stage={stage} type={error.GetType().Name}{status}");
    }
}
return failures == 0 && sites.Length > 0 ? 0 : 1;
