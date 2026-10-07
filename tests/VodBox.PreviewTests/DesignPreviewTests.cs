using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using VodBox.Desktop.Design;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>
/// 设计时预览烟测：模拟预览器流程——以 Design.DataContext 为根构造每个视图、Attach 进窗口、
/// 走完一次 Measure/Arrange/Render，断言不抛异常。
/// 设计时约束：不加载 libmpv（MpvVideoSurface 永不实例化）、不枚举真实文件系统、不启动计时器。
/// </summary>
public sealed class DesignPreviewTests
{
    [AvaloniaFact]
    public void DesignData_构造不触碰运行时依赖()
    {
        var main = DesignData.Main;
        Assert.NotNull(main.Player);
        Assert.DoesNotContain(main.Files.Entries, e => e.IsDirectory && e.Name == ".."); // 停在空根目录，未枚举真实文件系统
        Assert.Equal("庆余年 第二季 · 第03集", main.Player.Title);
        Assert.Equal(TimeSpan.FromMinutes(45), main.Player.Duration);
        Assert.NotEmpty(main.Home.Recommendations);
        Assert.NotNull(main.Detail.Detail);
        Assert.NotEmpty(main.Detail.Detail!.Lines);
        Assert.NotEmpty(main.Live.Groups);
        Assert.NotEmpty(main.Settings.Sites);
    }

    [AvaloniaTheory]
    [InlineData(typeof(HomeView))]
    [InlineData(typeof(VodView))]
    [InlineData(typeof(DetailView))]
    [InlineData(typeof(LiveView))]
    [InlineData(typeof(SearchView))]
    [InlineData(typeof(FavoritesView))]
    [InlineData(typeof(HistoryView))]
    [InlineData(typeof(FilesView))]
    [InlineData(typeof(SettingsView))]
    [InlineData(typeof(PlayerOverlay))]
    public void 视图以设计DataContext装载渲染不抛异常([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type viewType)
    {
        var view = (Control)Activator.CreateInstance(viewType)!;
        view.DataContext = DesignData.Main; // 等价于 Design.DataContext 在预览器里的赋值
        var window = new Window { Width = 1280, Height = 800, Content = view };
        window.Show();
        window.UpdateLayout(); // 强制走完 Measure/Arrange/Render 一帧
        if (Environment.GetEnvironmentVariable("VODBOX_PREVIEW_SHOT") is { Length: > 0 })
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "preview-shots");
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()
                ?.Save(Path.Combine(dir, $"{viewType.Name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        Assert.Equal(view, window.Content);
        window.Close();
    }
}
