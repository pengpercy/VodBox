using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using VodBox.Desktop.Services;
using VodBox.Infrastructure;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop;

public partial class MainWindow : Window
{
    private PlayerViewModel? _observedPlayer;
    internal Views.PlayerWindow? PlaybackWindow { get; private set; }
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => StopObservingPlayer();
        Closing+=OnClosing;
        // 拖放：m3u/m3u8 作为直播源打开，媒体文件直接播放。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>拖入内容不可用时把 DragEffects 置为 None，光标会显示“禁止”。</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var (intent, _) = DroppedFiles.Classify(DroppedPaths(e));
        e.DragEffects = intent == DroppedIntent.None ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainViewModel main) return;
        var (intent, path) = DroppedFiles.Classify(DroppedPaths(e));
        switch (intent)
        {
            case DroppedIntent.PlayLive:
                await main.OpenLivePlaylistAsync(path);
                break;
            case DroppedIntent.PlayMedia:
                main.PlayLocalFile(path);
                break;
            default:
                main.StatusMessage = "请拖入 .m3u/.m3u8 直播播放列表或媒体文件";
                break;
        }
    }

    /// <summary>
    /// 取出拖入的本地路径。Avalonia 12 用 IDataTransfer（不是旧的 IDataObject）：
    /// 逐项按 DataFormat.File 取值，再解析成本地路径。
    /// </summary>
    private static string?[] DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.GetItems(DataFormat.File)
            .Select(item => item.TryGetValue(DataFormat.File))
            .OfType<IStorageItem>()
            .Select(storage => storage.TryGetLocalPath())
            .Where(path => !string.IsNullOrEmpty(path))
            .ToArray();

    private async void OnClosing(object? sender,WindowClosingEventArgs e)
    {
        if(_shutdownComplete||Avalonia.Application.Current is not App||DataContext is not MainViewModel main)return;
        e.Cancel=true;
        if(_shutdownStarted)return;
        _shutdownStarted=true;
        try { await main.ShutdownAsync(); }
        catch(Exception error) { System.Diagnostics.Debug.WriteLine($"[shutdown] {error.Message}"); }
        finally
        {
            try { App.Services.Dispose(); }
            catch(Exception error){System.Diagnostics.Debug.WriteLine($"[shutdown] release failed: {error.Message}");}
            _shutdownComplete=true;Close();
        }
    }

    private MainViewModel VM => (MainViewModel)DataContext!;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        StopObservingPlayer();
        base.OnDataContextChanged(e);
        if (DataContext is MainViewModel playerOwner)
        {
            _observedPlayer = playerOwner.Player;
            _observedPlayer.PropertyChanged += OnPlayerVisibilityChanged;
            SyncPlayerWindow();
        }
        if (DataContext is MainViewModel vm && Avalonia.Application.Current is App)
        {
            vm.AttachDispatcher(Avalonia.Threading.Dispatcher.UIThread);
            // 启动时若有历史配置则自动加载
            Loaded += async (_, _) =>
            {
                if (App.Services.CurrentVodConfig is { Length: > 0 } url)
                {
                    try
                    {
                        await App.Services.Registry.LoadConfigAsync(url);
                        vm.UpdateSourceName();
                        await vm.Home.LoadAsync();
                    }
                    catch
                    {
                        vm.StatusMessage = "上次配置加载失败，请到设置中重新配置";
                    }
                }
                if (App.Services.CurrentLiveConfig is { Length: > 0 })
                {
                    await vm.Live.LoadAsync();
                }
            };
        }
    }

    private void StopObservingPlayer()
    {
        if (_observedPlayer is not null) _observedPlayer.PropertyChanged -= OnPlayerVisibilityChanged;
        _observedPlayer = null;
        PlaybackWindow?.CloseAfterPlayback();
        PlaybackWindow = null;
    }

    private void OnPlayerVisibilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.Visible)) SyncPlayerWindow();
    }

    private void SyncPlayerWindow()
    {
        if (DataContext is not MainViewModel main) return;
        if (!main.Player.Visible) { PlaybackWindow?.CloseAfterPlayback(); return; }
        if (PlaybackWindow is not null) return;
        var player = new Views.PlayerWindow { DataContext = main, Topmost = main.Player.AlwaysOnTop };
        PlaybackWindow = player;
        player.Closed += (_, _) => { if (ReferenceEquals(PlaybackWindow, player)) PlaybackWindow = null; };
        // Non-modal and unowned: the main/detail window remains independently interactive.
        player.Show();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) VM.SubmitSearchCommand.Execute(null);
    }
}
