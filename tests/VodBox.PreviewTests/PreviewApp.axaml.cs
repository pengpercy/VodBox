using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;

namespace VodBox.PreviewTests;

/// <summary>
/// 无头测试专用应用：与桌面 App 同主题（Fluent 深色），但不进入桌面生命周期——不构造 AppServices / MainWindow / libmpv。
/// BuildAvaloniaApp 关闭 UseHeadlessDrawing 并启用 Skia：CaptureRenderedFrame 需要真实渲染管线才有帧可截。
/// </summary>
public class PreviewApp : Application
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<PreviewApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}
