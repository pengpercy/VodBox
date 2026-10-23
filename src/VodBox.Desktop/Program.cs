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
            // Avalonia 默认会用 Application.Name 调 setProcessName，平台初始化时 App.axaml 的 Name 尚未加载，
            // 进程名会先被登记为 "Avalonia Application"；关掉后由 Info.plist 的 CFBundleName 决定。
            .With(new MacOSPlatformOptions { DisableSetProcessName = true })
            .WithInterFont()
            .LogToTrace();
}
