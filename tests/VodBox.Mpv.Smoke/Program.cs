using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VodBox.Infrastructure;
using System.Diagnostics;
using VodBox.Core;
using VodBox.Playback.Mpv;

// 真 libmpv 冒烟（headless）：传输层 + 引擎观察态，不依赖显示环境。用法：
//   dotnet run --project tests/VodBox.Mpv.Smoke -- <本地视频路径>
// 需 VODBOX_MPV_LIB 指向 libmpv（或系统目录可见）。
if (args.Length == 2 && args[0] is "--proxy-hls" or "--proxy-dash")
{
    var directory = System.IO.Path.GetFullPath(args[1]);
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
    builder.WebHost.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
    await using var upstream = builder.Build();
    upstream.MapGet("/{name}", async context =>
    {
        var name = context.Request.RouteValues["name"]?.ToString() ?? "";
        if (name != System.IO.Path.GetFileName(name) || name.Contains("..")) { context.Response.StatusCode = 400; return; }
        var path = System.IO.Path.Combine(directory, name);
        if (!File.Exists(path)) { context.Response.StatusCode = 404; return; }
        context.Response.ContentType = name.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
        await context.Response.SendFileAsync(path);
    });
    await upstream.StartAsync();
    try
    {
        await using var proxy = new LocalControlServer();
        await proxy.StartAsync(_ => Task.CompletedTask, _ => Task.CompletedTask);
        var manifest = args[0] == "--proxy-dash" ? "/main.mpd" : "/main.m3u8";
        var address = proxy.RegisterMedia(upstream.Urls.Single() + manifest);
        await using var playback = new MpvEngine(headless: true);
        await playback.OpenAsync(new PlaybackRequest { Uri = address }, 101);
        await Until(() => playback.Snapshot.State == PlaybackState.Playing && playback.Snapshot.Position.TotalSeconds > 1, "proxy media decode");
        if (playback.Snapshot.Duration.TotalSeconds < 5) throw new InvalidOperationException("Proxy HLS duration missing.");
        await playback.SeekToAsync(TimeSpan.FromSeconds(4));
        await Until(() => playback.Snapshot.Position.TotalSeconds >= 3.8, "proxy media seek");
        await playback.SeekToAsync(playback.Snapshot.Duration);
        await Until(() => playback.Snapshot.State == PlaybackState.Ended, "proxy media EOF");
        Console.WriteLine($"Real MpvEngine proxy {args[0]}: OK | rewritten manifest/segments, decode, seek, actual EOF");
    }
    finally { await upstream.StopAsync(); }
    return;
}

if (args.Length is < 1 or > 2 || !File.Exists(args[0])) throw new ArgumentException("请传入本地视频 fixture 路径。");
using var fixture = new TemporaryFixture(args[0]);
using var client = new MpvClient(new Dictionary<string, string> { ["vo"] = "null", ["ao"] = "null", ["hwdec"] = "no" });
client.Command("loadfile", fixture.Path, "replace");
await Until(() => DrainLoaded(client), "file-loaded");
var duration = client.GetDouble("duration") ?? throw new InvalidOperationException("No duration.");
if (duration < 3) throw new InvalidOperationException("Fixture must be at least three seconds long.");
client.Command("set", "pause", "yes");
client.Command("seek", "1.5", "absolute+exact");
await Until(() => client.GetDouble("time-pos") is > 1.3 and < 1.7, "paused seek");
var paused = client.GetDouble("time-pos");
await Task.Delay(150);
if (Math.Abs((client.GetDouble("time-pos") ?? 0) - (paused ?? 0)) > .05) throw new InvalidOperationException("Pause drift.");
client.Command("set", "speed", "1.5");
if (client.GetDouble("speed") != 1.5) throw new InvalidOperationException("Speed mismatch.");
client.Command("set", "volume", "35");
if (client.GetDouble("volume") != 35) throw new InvalidOperationException("Volume mismatch.");
client.Command("set", "pause", "no");
await Until(() => client.GetDouble("time-pos") > paused + .2, "resume");
try { client.Command("set", "volume\0", "30"); throw new InvalidOperationException("NUL accepted."); }
catch (ArgumentException) { }
try { client.Command("vodbox-unknown-command"); throw new InvalidOperationException("Unknown command accepted."); }
catch (MpvException) { }
client.Command("stop");
client.Dispose(); client.Dispose();
try { client.PollEvent(); throw new InvalidOperationException("Disposed handle accepted."); }
catch (ObjectDisposedException) { }
Console.WriteLine($"Real libmpv: OK | duration={duration:F2}s | seek/pause/resume/rate/volume/UTF-8/error/dispose");

await using var engine = new MpvEngine(headless: true);
if (!engine.Available) throw new InvalidOperationException("Headless MpvEngine unavailable; libmpv missing?");
await engine.SetVolumeAsync(35, default); await engine.SetRateAsync(1.5, default);
await engine.OpenAsync(new PlaybackRequest { Uri = new Uri(fixture.Path).AbsoluteUri, StartPositionMs = 1500 }, 7, default);
await Until(() => engine.Snapshot.State == PlaybackState.Playing && engine.Snapshot.CanSeek && engine.Snapshot.Position.TotalSeconds > 1.4, "engine observed state/start");
await engine.PauseAsync(default); await Until(() => engine.Snapshot.State == PlaybackState.Paused, "engine pause event");
await engine.SeekToAsync(TimeSpan.FromSeconds(2.5), default); await Until(() => Math.Abs(engine.Snapshot.Position.TotalSeconds - 2.5) < .15, "engine seek event");
if (engine.GetTracks(TrackKind.Audio).Count == 0 || engine.GetTracks(TrackKind.Subtitle).All(x => x.Id != "no"))
    throw new InvalidOperationException("Engine track cache missing.");
if (engine.Client is not { } realClient || realClient.GetString("track-list/0/type") is null)
    throw new InvalidOperationException("String property ABI missing.");
if (args.Length == 2)
{
    var subtitle = Path.GetFullPath(args[1]);
    await engine.LoadSubtitleAsync(subtitle, 6); // stale session must not add a track.
    var before = engine.GetTracks(TrackKind.Subtitle).Count;
    await engine.LoadSubtitleAsync(subtitle, 7);
    await Until(() => engine.GetTracks(TrackKind.Subtitle).Count > before, "external subtitle track");
    var track = engine.GetTracks(TrackKind.Subtitle).First(x => x.Id != "no");
    await engine.SelectTrackAsync(TrackKind.Subtitle, track.Id);
    await Until(() => engine.GetTracks(TrackKind.Subtitle).Any(x => x.Id == track.Id && x.IsSelected), "subtitle selected");
    await engine.SelectTrackAsync(TrackKind.Subtitle, "no");
    Console.WriteLine("Real MpvEngine subtitle: OK | stale-session rejected, sub-add, track cache, select/off");
}
await engine.PlayAsync(default); await Until(() => engine.Snapshot.State == PlaybackState.Playing, "engine resume");
await engine.SeekToAsync(engine.Snapshot.Duration);
await Until(() => engine.Snapshot.State == PlaybackState.Ended, "seek-to-end EOF");
Console.WriteLine("Real MpvEngine EOF: OK | seek-to-duration produced actual Ended event");
await engine.StopAsync(default); if (engine.Snapshot.State != PlaybackState.Idle) throw new InvalidOperationException("Engine stop state.");
Console.WriteLine("Real MpvEngine: OK | observed position/duration/pause/seekable, UTF-8 tracks, lifecycle");

static bool DrainLoaded(MpvClient client)
{
    for (var i = 0; i < 256; i++)
    {
        var item = client.PollEvent();
        if (item.Id == 8) return true; // MPV_EVENT_FILE_LOADED
        if (item.Id == 7) throw new InvalidOperationException("Playback ended before file-loaded.");
        if (item.Id == 0) break;
    }
    return false;
}

static async Task Until(Func<bool> condition, string stage)
{
    var watch = Stopwatch.StartNew();
    while (!condition())
    {
        if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(stage);
        await Task.Delay(20);
    }
}

sealed class TemporaryFixture : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(AppContext.BaseDirectory, "vodbox-mpv-" + Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public TemporaryFixture(string source)
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "合成视频 播放测试" + System.IO.Path.GetExtension(source));
        try { File.Copy(source, Path); }
        catch { Directory.Delete(_directory, true); throw; }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
