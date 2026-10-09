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
    private readonly Avalonia.Threading.DispatcherTimer _programmeTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    public LiveView()
    {
        InitializeComponent();
        _programmeTimer.Tick += (_, _) =>
        {
            if (IsVisible && DataContext is MainViewModel main) main.Live.RefreshProgrammeClock(DateTimeOffset.Now);
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (Avalonia.Application.Current is VodBox.Desktop.App) _programmeTimer.Start();
        };
        DetachedFromVisualTree += (_, _) => _programmeTimer.Stop();
    }
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private LiveViewModel VM => ((MainViewModel)DataContext!).Live;

    private void OnCardViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel main)
            main.Live.SetCardViewportWidth(Math.Max(1, e.NewSize.Width - 18));
    }

    private void OnPlayCard(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: LiveChannel channel }) VM.PlayChannelCommand.Execute(channel);
    }

    private void OnGroupHeader(object? sender,PointerPressedEventArgs e)
    {
        if(sender is Control{Tag:LiveViewModel.LiveGroupHeader header}&&header.Locked)VM.RequestGroupUnlock(header.Name);
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

    private void OnTabGroups(object? sender, RoutedEventArgs e) => SelectTab(0);
    private void OnTabFavorites(object? sender, RoutedEventArgs e) => SelectTab(1);
    private void OnTabHistory(object? sender, RoutedEventArgs e) => SelectTab(2);

    /// <summary>Tab 视觉切换（底部蓝线 + 文字对比度）；数据切换 S4 接收藏/历史频道。</summary>
    private async void SelectTab(int index)
    {
        try { await VM.RefreshLibraryTabsAsync(); VM.ChannelTab = index; }
        catch (Exception error) { if (DataContext is MainViewModel main) main.StatusMessage = $"频道列表加载失败：{error.Message}"; }
        if (this.FindControl<Button>("TabGroups") is not { } groups) return;
        var tabs = new[] { groups, this.FindControl<Button>("TabFavorites"), this.FindControl<Button>("TabHistory")};
        for (var i = 0; i < tabs.Length; i++)
        {
            tabs[i]!.Classes.Set("selected", i == index);
        }
    }
}
