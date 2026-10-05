using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsActivelyPlaying))] private PlaybackState _playerState;
    public bool IsActivelyPlaying => PlayerState is PlaybackState.Playing or PlaybackState.Buffering;
    [ObservableProperty] private bool _danmakuEnabled = true;
    [ObservableProperty] private double _danmakuFontSize = 24;
    [ObservableProperty] private double _danmakuOpacity = .85;
    [ObservableProperty] private double _danmakuCoverage = .5;
    [ObservableProperty] private int _danmakuDelayMs;
    [ObservableProperty] private string _danmakuLocation = "";
    [ObservableProperty] private string _danmakuStatus = "可加载 XML 或 VodBox 弹幕 JSON。";
    [ObservableProperty] private IReadOnlyList<DanmakuComment> _danmakuComments = [];
    private CancellationTokenSource? _danmakuCancellation;
    private readonly List<Task> _danmakuTasks = [];
    partial void OnDanmakuEnabledChanged(bool value) => SchedulePreferencesSave();
    partial void OnDanmakuFontSizeChanged(double value) => SchedulePreferencesSave();
    partial void OnDanmakuOpacityChanged(double value) => SchedulePreferencesSave();
    partial void OnDanmakuCoverageChanged(double value) => SchedulePreferencesSave();
    partial void OnDanmakuDelayMsChanged(int value) => SchedulePreferencesSave();
    public void ClearDanmaku()
    { _danmakuCancellation?.Cancel(); DanmakuComments = []; DanmakuLocation = ""; DanmakuStatus = "可加载 XML 或 VodBox 弹幕 JSON。"; }
    [RelayCommand] private Task LoadDanmakuUrlAsync() => LoadDanmakuAsync(DanmakuLocation);
    public Task LoadDanmakuAsync(string location)
    {
        _danmakuCancellation?.Cancel(); _danmakuTasks.RemoveAll(x => x.IsCompleted);
        Task task = RunAsync(async () =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _danmakuCancellation = cancellation;
            DanmakuComments = []; DanmakuLocation = location; DanmakuStatus = "正在加载弹幕…";
            try
            {
                var comments = await new DanmakuLoader(_http).LoadAsync(location, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                DanmakuComments = comments; DanmakuStatus = $"已加载 {comments.Count} 条弹幕。";
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { if (ReferenceEquals(_danmakuCancellation, cancellation)) DanmakuStatus = "弹幕下载或解析超过 20 秒，请检查地址。"; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested && ReferenceEquals(_danmakuCancellation, cancellation)) DanmakuStatus = "弹幕加载失败：" + ex.Message; }
            finally { if (ReferenceEquals(_danmakuCancellation, cancellation)) _danmakuCancellation = null; }
        }, busy: false);
        _danmakuTasks.Add(task); return task;
    }
}
