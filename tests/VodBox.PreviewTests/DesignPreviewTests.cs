using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop.Design;
using VodBox.Desktop.Views;
using Xunit;

// 指定无头会话使用的应用基座（Fluent 深色主题 + Inter 字体），保证截图与真实 App 同视觉基座。
[assembly: AvaloniaTestApplication(typeof(VodBox.PreviewTests.PreviewApp))]

namespace VodBox.PreviewTests;

/// <summary>
/// 设计时预览烟测：模拟预览器流程——以 Design.DataContext 为根构造每个视图、Attach 进窗口、
/// 走完一次 Measure/Arrange/Render，断言不抛异常。
/// 设计时约束：不加载 libmpv（MpvVideoSurface 永不实例化）、不枚举真实文件系统、不启动计时器。
/// VODBOX_PREVIEW_SHOT=1 时导出 preview-shots/&lt;ViewName&gt;.png 截图，用于与 design/screenshots 对拍。
/// </summary>
public sealed class DesignPreviewTests
{
    [AvaloniaFact]
    public void DesignData_构造不触碰运行时依赖()
    {
        var main = DesignData.Main;
        Assert.NotNull(main.Player);
        Assert.DoesNotContain(main.Files.Entries, e => e.IsDirectory && e.Name == ".."); // 停在空根目录，未枚举真实文件系统
        Assert.Equal("庆余年 第二季", main.Player.Title);
        Assert.Equal("第12集 · 抱月楼风波", main.Player.Subtitle);
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
        window.UpdateLayout(); // 强制走完一次 Measure/Arrange/Render
        Assert.All(view.GetVisualDescendants().OfType<PathIcon>(), icon =>
        {
            Assert.NotNull(icon.Data);
            // Fluent 控件模板自身的图标（如滚动条箭头）使用主题尺寸；播放器控制条另用16px小图标。
            if (icon.TemplatedParent is not null) return;
            Assert.Equal(icon.Width, icon.Height);
            Assert.Contains(icon.Width, new[] { 12d, 16d, 18d, 24d, 48d });
            Assert.False(icon.IsSet(PathIcon.ForegroundProperty));
        });
        if (view is SearchView)
        {
            var hot = view.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "热");
            var fire = Assert.IsType<StackPanel>(hot.Parent).Children.OfType<PathIcon>().Single();
            Assert.Equal(hot.Foreground, fire.Foreground);
        }
        if (Environment.GetEnvironmentVariable("VODBOX_PREVIEW_SHOT") is { Length: > 0 })
        {
            var frame = CaptureFrame(window);
            Assert.True(frame is not null, $"{viewType.Name} 渲染帧未完成（多次强制渲染 tick 后仍无帧）");
            var dir = Path.Combine(Environment.CurrentDirectory, "preview-shots");
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, $"{viewType.Name}.png"), new PngBitmapEncoderOptions());
        }
        Assert.Equal(view, window.Content);
        window.Close();
    }

    /// <summary>强制渲染 tick + 泵 UI 队列，直到拿到非空帧（最多 8 轮，跨过懒加载首帧）。</summary>
    private static Bitmap? CaptureFrame(Window window)
    {
        Bitmap? frame = null;
        for (var i = 0; i < 8 && frame is null; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            frame = window.CaptureRenderedFrame();
        }
        return frame;
    }
}
