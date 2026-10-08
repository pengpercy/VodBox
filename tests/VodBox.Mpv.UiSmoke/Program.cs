using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using VodBox.Playback.Mpv;

internal static class Program
{
    public static MpvClient Client = null!;
    public static string FixturePath = "";
    public static string Referer = "";
    public static string UserAgent = "";

    /// <summary>读命令行参数：支持 --key=value 与 --key value 两种形式。</summary>
    private static string GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(key + "=", StringComparison.Ordinal)) return args[i][(key.Length + 1)..];
            if (args[i] == key && i + 1 < args.Length) return args[i + 1];
        }
        return "";
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var fixture = args.FirstOrDefault();
        var isUrl = fixture is not null && (fixture.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || fixture.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (fixture is null || (!isUrl && !File.Exists(fixture)))
            throw new ArgumentException("请传入本地视频 fixture 路径，或网络播放地址（http/https/m3u8）。可附加 --referer=<url> --ua=<ua> 传请求头（真实站点 CDN 普遍要求）。");
        // 本地路径归一化；网络地址原样（mpv 直接解 HLS/mp4）
        FixturePath = isUrl ? fixture : Path.GetFullPath(fixture);
        Referer = GetArg(args, "--referer");
        UserAgent = GetArg(args, "--ua");
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;
        Client = Task.Run(() => new MpvClient(new Dictionary<string, string> { ["vo"] = "libmpv", ["ao"] = "null", ["hwdec"] = "no" })).GetAwaiter().GetResult();
        // 打开 mpv 内部日志（诊断网络流到底卡在哪一步）
        Client.RequestLogs("info");
        // 后台线程轮询日志事件并打印
        var logThread = new Thread(() =>
        {
            while (true)
            {
                var evt = Client.PollEvent();
                if (evt.LogText is { Length: > 0 } log) Console.Error.WriteLine("[mpv] " + log.TrimEnd());
                else Thread.Sleep(50);
            }
        }) { IsBackground = true };
        logThread.Start();
        return AppBuilder.Configure<ProbeApp>()
            .UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
            .WithInterFont()
            .LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
            .StartWithClassicDesktopLifetime(args);
    }
}

internal sealed class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var surface = new MpvVideoSurface(Program.Client);
            var grid = new Grid
            {
                Children =
                {
                    surface,
                    new TextBlock
                    {
                        Text = "VodBox · 视频上方的 Avalonia 控件",
                        Foreground = Brushes.White, Background = Brushes.DarkSlateBlue,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                        FontSize = 24,
                    },
                },
            };
            var window = new Window { Width = 800, Height = 480, Content = grid, Title = "VodBox mpv render probe" };
            window.Opened += async (_, _) =>
            {
                await Task.Delay(10000);
                if (surface.RenderedFrames == 0)
                {
                    Console.Error.WriteLine($"GPU initialization timed out: bounds={surface.Bounds}");
                    desktop.Shutdown(1);
                }
            };
            surface.Failed += (_, error) => { Console.Error.WriteLine(error); desktop.Shutdown(1); };
            surface.Ready += async (_, _) =>
            {
                try
                {
                    var isUrl = Program.FixturePath.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                    await Task.Run(() =>
                    {
                        if (isUrl)
                        {
                            if (Program.UserAgent.Length > 0) Program.Client.Command("set", "user-agent", Program.UserAgent);
                            if (Program.Referer.Length > 0) Program.Client.Command("set", "referrer", Program.Referer);
                            // 诊断：确认头真的设进去了
                            Console.Error.WriteLine("[probe] set user-agent → " + Program.Client.GetString("user-agent"));
                            Console.Error.WriteLine("[probe] set referrer → " + Program.Client.GetString("referrer"));
                        }
                        Program.Client.Command("loadfile", Program.FixturePath, "replace");
                    });
                    await Task.Delay(isUrl ? 8000 : 2500); // 网络流需更长缓冲
                    var position = await Task.Run(() => Program.Client.GetDouble("time-pos"));
                    var width = await Task.Run(() => Program.Client.GetDouble("video-params/w"));
                    // 网络流宽度不定，仅验帧数+进度；本地合成视频才验 640 宽
                    if (surface.RenderedFrames < 10 || position is not > 1)
                        throw new InvalidOperationException($"Render failed: frames={surface.RenderedFrames}, position={position}, width={width}");
                    if (!isUrl && width != 640)
                        throw new InvalidOperationException($"Local fixture width mismatch: width={width}, expected 640");
                    await Task.Run(() => Program.Client.Command("set", "pause", "yes"));
                    Console.WriteLine($"Avalonia libmpv OpenGL: OK | frames={surface.RenderedFrames} | position={position:F2}s | width={width} | overlay shares UI tree");
                    await Task.Delay(300);
                    window.Close();
                }
                catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
