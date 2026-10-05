using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using VodBox.Desktop;
using VodBox.Playback.Mpv;

internal static class Program
{
    public static MpvClient Client = null!;
    public static string FixturePath = "";
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0])) throw new ArgumentException("Pass a local 640px-wide video fixture of at least four seconds.");
        FixturePath = Path.GetFullPath(args[0]);
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        System.Diagnostics.Trace.AutoFlush = true;
        Client = Task.Run(() => new MpvClient(new Dictionary<string,string> { ["vo"] = "libmpv", ["ao"] = "null", ["hwdec"] = "no" })).GetAwaiter().GetResult();
        using var lifetime = Client;
        return AppBuilder.Configure<ProbeApp>().UsePlatformDetect().With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] }).WithInterFont().LogToTrace(Avalonia.Logging.LogEventLevel.Information).StartWithClassicDesktopLifetime(args);
    }
}
internal sealed class ProbeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var surface = new MpvVideoSurface(Program.Client);
            var grid = new Grid { Children = { surface, new TextBlock { Text="VodBox · 视频上方的 Avalonia 控件", Foreground=Brushes.White, Background=Brushes.DarkSlateBlue, HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment=Avalonia.Layout.VerticalAlignment.Top, FontSize=24 } } };
            var window = new Window { Width=800, Height=480, Content=grid, Title="VodBox mpv render probe" };
            window.Opened += async (_, _) => { await Task.Delay(10000); if (surface.RenderedFrames == 0) { Console.Error.WriteLine($"GPU initialization timed out: bounds={surface.Bounds}"); desktop.Shutdown(1); } };
            surface.Failed += (_, error) => { Console.Error.WriteLine(error); desktop.Shutdown(1); };
            surface.Ready += async (_, _) =>
            {
                try
                {
                    await Task.Run(() => Program.Client.Command("loadfile", Program.FixturePath, "replace"));
                    await Task.Delay(2500);
                    var position = await Task.Run(() => Program.Client.GetDouble("time-pos"));
                    var width = await Task.Run(() => Program.Client.GetDouble("video-params/w"));
                    if (surface.RenderedFrames < 10 || position is not > 1 || width != 640) throw new InvalidOperationException($"Render failed: frames={surface.RenderedFrames}, position={position}, width={width}");
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
