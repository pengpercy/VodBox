using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>视频画面的右键菜单：常用操作集中一处，毛玻璃外观（GlassMenu）。</summary>
public partial class PlayerOverlay
{
    private static readonly (string Label, double? Ratio)[] AspectRatios = [("原始比例", null), ("16:9", 16d / 9), ("4:3", 4d / 3), ("1:1", 1d)];

    internal ContextMenu OpenVideoMenu()
    {
        var menu = BuildVideoMenu();
        _activeMenu?.Close();
        _activeMenu = menu;
        _menuOpen = true;
        RevealControls();
        menu.Closed += (_, _) => { if (ReferenceEquals(_activeMenu, menu)) _activeMenu = null; _menuOpen = false; RevealControls(); };
        menu.Placement = PlacementMode.Pointer;
        menu.PlacementTarget = this;
        menu.Open(this);
        return menu;
    }

    internal ContextMenu BuildVideoMenu()
    {
        var menu = GlassMenu.Apply(new ContextMenu { MinWidth = 220 });
        if (VM is not { } vm) return menu;
        var window = TopLevel.GetTopLevel(this) as Window;
        var fullscreen = window?.WindowState == WindowState.FullScreen;
        var session = vm.CurrentSessionId;

        menu.Items.Add(Item(vm.State == Core.PlaybackState.Playing ? "暂停" : "播放", () => vm.TogglePlayPauseCommand.Execute(null), Key.Space));
        if (vm.Playlist.Count > 1)
        {
            menu.Items.Add(Item("上一集", () => vm.PreviousEpisodeCommand.Execute(null), enabled: vm.PlaylistIndex > 0));
            menu.Items.Add(Item("下一集", () => vm.NextEpisodeCommand.Execute(null), enabled: vm.PlaylistIndex >= 0 && vm.PlaylistIndex < vm.Playlist.Count - 1));
        }
        menu.Items.Add(new Separator());

        menu.Items.Add(Group("倍速", Rates.Select(rate => Check($"{rate:0.##}×", Math.Abs(vm.Rate - rate) < .001, () => vm.Rate = rate))));
        if (!vm.AudioOnly)
            menu.Items.Add(Group("画面比例", AspectRatios.Select(option => Check(option.Label, vm.AspectRatio == option.Ratio, async () =>
            {
                if (session == vm.CurrentSessionId) await vm.SetAspectRatioAsync(option.Ratio);
            }))));
        // 播放设置菜单原样挪进子菜单：音轨、字幕、跳过片头片尾、线路地址等。
        var settings = BuildSettingsMenu();
        var settingItems = settings.Items.Cast<object>().ToList();
        settings.Items.Clear();
        menu.Items.Add(Group("播放设置", settingItems));
        menu.Items.Add(new Separator());

        menu.Items.Add(Check("听视频", vm.AudioOnly, () => vm.AudioOnly = !vm.AudioOnly, Key.A));
        menu.Items.Add(Check("弹幕", vm.DanmakuEnabled, () => vm.DanmakuEnabled = !vm.DanmakuEnabled));
        if (!fullscreen && window is not null)
        {
            menu.Items.Add(Check("窗口置顶", window.Topmost, () => OnToggleTopmost(this, new Avalonia.Interactivity.RoutedEventArgs())));
            menu.Items.Add(Check("画中画", vm.CompactMode, () => ToggleCompactWindow(window)));
        }
        if (window is not null)
            menu.Items.Add(Item(fullscreen ? "退出全屏" : "全屏", () => OnToggleFullscreen(this, new Avalonia.Interactivity.RoutedEventArgs()), Key.F));
        menu.Items.Add(new Separator());

        menu.Items.Add(Item("截图", () => _ = vm.SaveScreenshotAsync(), Key.S, enabled: !vm.AudioOnly && vm.State is not (Core.PlaybackState.Idle or Core.PlaybackState.Resolving or Core.PlaybackState.Loading or Core.PlaybackState.Failed)));
        menu.Items.Add(Item("复制视频地址", async () =>
        {
            if (vm.MediaUri is not { } uri || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            try { await clipboard.SetTextAsync(uri); vm.FlashToast("已复制视频地址"); }
            catch (Exception error) { vm.FlashToast($"复制失败：{error.Message}"); }
        }, enabled: !string.IsNullOrEmpty(vm.MediaUri)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Check("统计信息", vm.StatsVisible, () => vm.StatsVisible = !vm.StatsVisible, Key.I));
        return menu;
    }

    private static MenuItem Item(string header, Action click, Key? key = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (key is { } gesture) item.InputGesture = new KeyGesture(gesture);
        item.Click += (_, _) => click();
        return item;
    }

    private static MenuItem Check(string header, bool isChecked, Action click, Key? key = null)
    {
        var item = Item(header, click, key);
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = isChecked;
        return item;
    }

    private static MenuItem Group(string header, IEnumerable<object> items) => new() { Header = header, ItemsSource = items.ToList() };
}
