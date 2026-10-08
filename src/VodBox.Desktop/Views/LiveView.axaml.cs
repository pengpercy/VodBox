using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class LiveView : UserControl
{
    public LiveView() => InitializeComponent();
    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private LiveViewModel VM => ((MainViewModel)DataContext!).Live;

    private void OnPlayChannel(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.Tag is LiveChannel channel) VM.PlayChannelCommand.Execute(channel);
    }

    private void OnEpgSeek(object? sender, PointerPressedEventArgs e)
    {
        // S4 前仅演示：点 EPG 卡片提示回看能力
        if ((sender as Control)?.Tag is LiveEpgCard card && card.IsPast)
            VM.CatchupCommand.Execute(null);
    }

    private void OnToggleLive(object? sender, RoutedEventArgs e) { }

    private void OnPrevChannel(object? sender, RoutedEventArgs e) => VM.PrevChannelCommand.Execute(null);

    private void OnNextChannel(object? sender, RoutedEventArgs e) => VM.NextChannelCommand.Execute(null);

    private void OnSwitchLine(object? sender, RoutedEventArgs e) => VM.SwitchLineCommand.Execute(null);

    private void OnCatchup(object? sender, RoutedEventArgs e) => VM.CatchupCommand.Execute(null);

    private void OnTabGroups(object? sender, RoutedEventArgs e) => SelectTab(0);
    private void OnTabFavorites(object? sender, RoutedEventArgs e) => SelectTab(1);
    private void OnTabHistory(object? sender, RoutedEventArgs e) => SelectTab(2);

    /// <summary>Tab 视觉切换（底部蓝线 + 文字对比度）；数据切换 S4 接收藏/历史频道。</summary>
    private void SelectTab(int index)
    {
        if (this.FindControl<Button>("TabGroups") is not { } groups) return;
        var tabs = new[] { groups, this.FindControl<Button>("TabFavorites"), this.FindControl<Button>("TabHistory")};
        for (var i = 0; i < tabs.Length; i++)
        {
            tabs[i]!.BorderBrush = i == index ? Avalonia.Media.Brushes.Transparent : Avalonia.Media.Brushes.Transparent;
            tabs[i]!.BorderThickness = new Avalonia.Thickness(0, 0, 0, i == index ? 2 : 0);
            if (i == index) tabs[i]!.BorderBrush = Avalonia.Media.Brush.Parse("#4CC2FF");
            if (tabs[i]!.Content is TextBlock text)
                text.Foreground = i == index
                    ? Avalonia.Media.Brushes.White
                    : Avalonia.Media.Brush.Parse("#85FFFFFF");
        }
    }
}
