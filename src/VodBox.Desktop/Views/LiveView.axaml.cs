using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class LiveView : UserControl
{
    private readonly Avalonia.Threading.DispatcherTimer _scrollIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly Avalonia.Threading.DispatcherTimer _programmeTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    public LiveView()
    {
        InitializeComponent();
        _scrollIdleTimer.Tick += (_, _) =>
        {
            _scrollIdleTimer.Stop();
            if (DataContext is MainViewModel main) main.Live.IsChannelScrolling = false;
        };
        AddHandler(ScrollViewer.ScrollChangedEvent, OnChannelScrollChanged, RoutingStrategies.Bubble, handledEventsToo: true);
        // 在滚轮事件进入布局前就暂停台标，让随后的虚拟行创建直接走缓存/占位路径。
        AddHandler(PointerWheelChangedEvent, (_, _) =>
        {
            if (this.FindControl<ListBox>("ChannelCardGrid")?.IsPointerOver == true) MarkChannelScrolling();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _programmeTimer.Tick += (_, _) =>
        {
            if (IsVisible && DataContext is MainViewModel main) main.Live.RefreshProgrammeClock(DateTimeOffset.Now);
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (Avalonia.Application.Current is VodBox.Desktop.App) _programmeTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _programmeTimer.Stop();
            _scrollIdleTimer.Stop();
            if (DataContext is MainViewModel main) main.Live.IsChannelScrolling = false;
        };
    }
    private void OnChannelScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta == default) return;
        MarkChannelScrolling();
    }

    private void MarkChannelScrolling()
    {
        if (DataContext is not MainViewModel main) return;
        main.Live.IsChannelScrolling = true;
        _scrollIdleTimer.Stop();
        _scrollIdleTimer.Start();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private LiveViewModel VM => ((MainViewModel)DataContext!).Live;

    private void OnCardViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel main)
            main.Live.SetCardViewportWidth(Math.Max(1, e.NewSize.Width - 52));
    }

    private void OnPlayCard(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: LiveChannel channel }) VM.PlayChannelCommand.Execute(channel);
    }

    private void OnGroupHeader(object? sender,PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { Tag: LiveViewModel.LiveGroupHeader header })
        { VM.ToggleGroup(header.Name); e.Handled = true; }
    }

    private void OnPlayChannel(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is LiveChannel channel) VM.PlayChannelCommand.Execute(channel);
    }

    private void OnEpgSeek(object? sender, PointerPressedEventArgs e)
    {
        // S4 前仅演示：点 EPG 卡片提示回看能力
        if ((sender as Control)?.Tag is LiveEpgCard card && card.IsPast)
            VM.PlayCatchup(card);
    }

    private void OnToggleLive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main && main.Player.Visible) main.Player.TogglePlayPauseCommand.Execute(null);
    }

    private async void OnImportEpg(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider.CanOpen != true) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入 XMLTV 节目表", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("XMLTV") { Patterns = ["*.xml", "*.xmltv", "*.xml.gz", "*.xmltv.gz"] }],
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            await VM.ImportXmlTvAsync(stream, files[0].Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error)
        {
            if (DataContext is MainViewModel main) main.StatusMessage = $"节目表导入失败：{error.Message}";
        }
    }

    private void OnPrevChannel(object? sender, RoutedEventArgs e) => VM.PrevChannelCommand.Execute(null);

    private void OnNextChannel(object? sender, RoutedEventArgs e) => VM.NextChannelCommand.Execute(null);

    private void OnSwitchLine(object? sender, RoutedEventArgs e) => VM.SwitchLineCommand.Execute(null);

    private void OnCatchup(object? sender, RoutedEventArgs e) => VM.CatchupCommand.Execute(null);

}
