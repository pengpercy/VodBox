using Avalonia;

namespace VodBox.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--diagnostics")) return Diagnostics.RunAsync(args).GetAwaiter().GetResult();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); return 0;
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
