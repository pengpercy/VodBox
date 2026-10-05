using System.Diagnostics;
using VodBox.Playback.Mpv;

if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Pass a local video fixture path.");
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
    while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(stage); await Task.Delay(20); }
}

sealed class TemporaryFixture : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vodbox-mpv-" + Guid.NewGuid().ToString("N"));
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
