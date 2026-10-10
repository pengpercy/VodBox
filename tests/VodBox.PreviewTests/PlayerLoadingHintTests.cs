using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>
/// 播放窗口加载/缓冲提示的验收：等帧期间必须有反馈（原问题是窗口打开后纯黑、毫无提示），
/// 出画后必须收干净，中途缓冲要克制不闪。
///
/// 这里只驱动 PlayerViewModel.State / Error，不伪造引擎事件——目的是验证
/// 「视图层是否真的绑上了这些状态」，而不是重测引擎的状态机。
/// </summary>
public sealed class PlayerLoadingHintTests : IDisposable
{
    private readonly List<string> _directories = [];

    private MainViewModel NewHost()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vodbox-hint-{Guid.NewGuid():N}");
        _directories.Add(directory);
        var services = new AppServices(directory);
        var main = new MainViewModel(services);
        main.AttachDispatcher(Dispatcher.UIThread);
        return main;
    }

    public void Dispose()
    {
        foreach (var directory in _directories) TempDataDirectory.TryDelete(directory);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        window.UpdateLayout();
    }

    /// <summary>等待条件成立。必须用 await（不能 Thread.Sleep）：DispatcherTimer 靠 UI 线程计时，阻塞它就永远不响。</summary>
    private static async Task<bool> Until(Func<bool> condition, int milliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [AvaloniaFact]
    public void ResolvingAndLoadingBothReportLoading()
    {
        // 起始加载链路是 Resolving → Loading，两个阶段都必须有反馈，否则就是纯黑窗。
        var player = NewHost().Player;
        player.State = PlaybackState.Resolving;
        Assert.True(player.IsLoading);

        player.State = PlaybackState.Loading;
        Assert.True(player.IsLoading);
    }

    [AvaloniaFact]
    public void PlayingClearsEveryTransientHint()
    {
        var player = NewHost().Player;
        player.State = PlaybackState.Loading;
        Assert.True(player.IsLoading);

        player.State = PlaybackState.Playing;
        Assert.False(player.IsLoading);
        Assert.False(player.IsBuffering);
        Assert.False(player.BufferingHintVisible);
    }

    [AvaloniaFact]
    public void LoadingTextCoversResolvingAndLoadingDistinctly()
    {
        // 文案要区分「解析地址」与「加载媒体」两类阶段。
        var player = NewHost().Player;
        player.State = PlaybackState.Resolving;
        var resolving = player.LoadingText;

        player.State = PlaybackState.Loading;
        var loading = player.LoadingText;

        Assert.False(string.IsNullOrWhiteSpace(resolving));
        Assert.False(string.IsNullOrWhiteSpace(loading));
        Assert.NotEqual(resolving, loading);
        // 未开始播放直播时不应误报为直播。
        Assert.False(player.IsLive);
    }

    [AvaloniaFact]
    public async Task BufferingHintIsDelayedThenShownThenImmediatelyHidden()
    {
        // 中途缓冲要克制：刚卡住不闪提示，持续卡住才显示，恢复播放立即收起。
        var player = NewHost().Player;
        player.State = PlaybackState.Playing;
        Assert.False(player.BufferingHintVisible);

        player.State = PlaybackState.Buffering;
        Assert.True(player.IsBuffering);
        Assert.False(player.BufferingHintVisible); // 延迟未到，避免闪一下

        Assert.True(await Until(() => player.BufferingHintVisible),
            "持续缓冲后应显示缓冲提示");

        // 规格 §4：一旦显示至少停留 600ms，防止「出现 80ms 立刻消失」的闪烁；
        // 所以刚恢复播放时可能还在显示，最终必须收干净。
        player.State = PlaybackState.Playing;
        Assert.True(await Until(() => !player.BufferingHintVisible),
            "恢复播放后缓冲提示最终应收起");
        Assert.False(player.IsBuffering);
    }

    [AvaloniaFact]
    public async Task ShortBufferingJitterNeverShowsTheHint()
    {
        // 规格 §4：400ms 内的抖动人眼判定不了「卡住」，不该闪出提示。
        var player = NewHost().Player;
        player.State = PlaybackState.Buffering;
        await Task.Delay(120);
        player.State = PlaybackState.Playing;
        Assert.False(player.BufferingHintVisible);
        // 再确认延迟期间确实没有冒出来。
        await Task.Delay(400);
        Assert.False(player.BufferingHintVisible);
    }

    [AvaloniaFact]
    public void FailedStateIsExposedSeparatelyFromLoading()
    {
        var player = NewHost().Player;
        player.Error = "无法播放：连接超时";
        player.State = PlaybackState.Failed;
        Assert.True(player.IsFailed);
        Assert.False(player.IsLoading);
    }

    [AvaloniaFact]
    public async Task OverlayShowsLoadingLayerDuringResolvingThenHidesItAfterPlayback()
    {
        // 视图层验收：编译通过不代表绑定生效，这里真的渲染并观察 IsVisible 变化。
        var main = NewHost();
        main.Player.Visible = true;
        main.Player.State = PlaybackState.Resolving;

        var overlay = new PlayerOverlay { DataContext = main };
        var window = new Window { Width = 960, Height = 540, Content = overlay };
        window.Show();
        try
        {
            Pump(window);
            var layer = overlay.FindControl<StackPanel>("LoadingLayer")!;
            Assert.True(layer.IsEffectivelyVisible, "Resolving 时加载提示应可见");

            main.Player.State = PlaybackState.Playing;
            Assert.True(await Until(() => !layer.IsEffectivelyVisible),
                "出画后加载提示应消失（淡出收尾）");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LoadingHintLivesInTheMiddleRowAndNotInsideTheControlsLayer()
    {
        // 提示层必须独立于「由 Visible 驱动的控制层」，否则会遮住底部控制条、
        // 并随控制层一起消隐（加载期反而看不到任何东西）。
        var main = NewHost();
        main.Player.Visible = true;
        main.Player.State = PlaybackState.Loading;

        var overlay = new PlayerOverlay { DataContext = main };
        var window = new Window { Width = 960, Height = 540, Content = overlay };
        window.Show();
        try
        {
            Pump(window);
            var layer = overlay.FindControl<StackPanel>("LoadingLayer")!;
            Assert.Equal(1, Grid.GetRow(layer)); // 中间弹性行承载提示

            // 底部控制层的进度条与提示层不能是同一个容器。
            var status = overlay.FindControl<Grid>("StatusLayer")!;
            Assert.Contains(layer, status.GetVisualDescendants().OfType<StackPanel>());
            var controlsRoot = overlay.FindControl<Border>("BottomControls");
            Assert.NotNull(controlsRoot);
            Assert.DoesNotContain(layer, controlsRoot!.GetVisualDescendants().OfType<StackPanel>());
        }
        finally { window.Close(); }
    }
}
