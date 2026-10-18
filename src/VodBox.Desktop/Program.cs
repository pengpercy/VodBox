using Avalonia;
using System;

namespace VodBox.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        // 更新器在等本进程退出；残留的前台线程（播放器、局域网服务等）不能再拖住它。
        if (Services.AppUpdater.UpdaterLaunched) Environment.Exit(0);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { RenderingMode=[AvaloniaNativeRenderingMode.OpenGl,AvaloniaNativeRenderingMode.Software] })
            .WithInterFont()
            .LogToTrace();
}
