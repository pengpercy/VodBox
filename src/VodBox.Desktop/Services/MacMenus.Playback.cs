using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VodBox.Core;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;

namespace VodBox.Desktop.Services;

internal static partial class MacMenus
{
    private static KeyGesture PlaybackKey(Key key, KeyModifiers modifiers = KeyModifiers.None) => new(key, KeyModifiers.Meta | modifiers);

    // Rebuild native menus before export so tracks, episodes and selected options follow the current session.
    internal static IEnumerable<NativeMenuItem> CreatePlaybackMenus(MainViewModel main, Func<PlayerWindow?>? playbackWindow = null)
    {
        foreach (var title in new[] { "播放", "视频", "音频", "字幕" })
        {
            var menu = new NativeMenu();
            void Refresh()
            {
                menu.Items.Clear();
                var vm = main.Player;
                var window = playbackWindow is null ? MainWindow?.PlaybackWindow : playbackWindow();
                var overlay = window?.Overlay;
                var session = vm.CurrentSessionId;
                NativeMenuItem Action(string label, Action action, bool enabled = true, KeyGesture? gesture = null)
                {
                    var item = Item(label, () => { if (enabled && vm.Visible && vm.CurrentSessionId == session) action(); }, gesture);
                    item.IsEnabled = vm.Visible && enabled;
                    return item;
                }
                NativeMenuItem Choice(string label, Action action, bool selected, KeyGesture? gesture = null)
                {
                    var item = Action(label, action, gesture: gesture);
                    item.ToggleType = MenuItemToggleType.CheckBox;
                    item.IsChecked = selected;
                    return item;
                }
                void Add(string label, Action action, bool enabled = true, KeyGesture? gesture = null) => menu.Add(Action(label, action, enabled, gesture));
                void Separator() => menu.Add(new NativeMenuItemSeparator());
                // Reuse the panel's settings and session guards, including its local subtitle picker.
                NativeMenuItem Convert(MenuItem source)
                {
                    var header = source.Header?.ToString() ?? "";
                    var selected = header.StartsWith("当前 · ");
                    var item = Action(selected ? header[5..] : header, () => source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)), source.IsEnabled);
                    var label = item.Header ?? "";
                    item.Gesture = label switch
                    {
                        _ when label.StartsWith("自动下一集") => PlaybackKey(Key.N, KeyModifiers.Shift),
                        _ when label.StartsWith("不记录") => PlaybackKey(Key.I, KeyModifiers.Shift),
                        _ when label.StartsWith("弹幕") => PlaybackKey(Key.D, KeyModifiers.Shift),
                        "加载本地字幕…" => PlaybackKey(Key.O, KeyModifiers.Shift),
                        "关闭字幕" => PlaybackKey(Key.S, KeyModifiers.Shift),
                        "字幕提前 0.5 秒" => PlaybackKey(Key.Left, KeyModifiers.Alt),
                        "字幕延后 0.5 秒" => PlaybackKey(Key.Right, KeyModifiers.Alt),
                        _ when label.StartsWith("恢复同步") => PlaybackKey(Key.D0, KeyModifiers.Alt),
                        _ => null,
                    };
                    if (selected) { item.ToggleType = MenuItemToggleType.CheckBox; item.IsChecked = true; }
                    var children = source.Items.OfType<MenuItem>().ToArray();
                    if (children.Length > 0)
                    {
                        item.Menu = new NativeMenu();
                        foreach (var child in children) item.Menu.Add(Convert(child));
                    }
                    return item;
                }
                var settings = overlay?.BuildSettingsMenu().Items.OfType<MenuItem>().ToArray() ?? [];
                void Settings(Func<string, bool> predicate)
                {
                    foreach (var item in settings.Where(item => predicate(item.Header?.ToString() ?? ""))) menu.Add(Convert(item));
                }
                void CycleTrack(TrackKind kind)
                {
                    var tracks = vm.GetTracks(kind).ToList();
                    if (kind == TrackKind.Subtitle) tracks.Insert(0, new MediaTrack("no", "关闭字幕", kind, tracks.All(track => !track.IsSelected)));
                    if (tracks.Count == 0) return;
                    var next = (tracks.FindIndex(track => track.IsSelected) + 1) % tracks.Count;
                    _ = vm.SelectTrackAsync(kind, tracks[next].Id);
                }
                if (title == "播放")
                {
                    Add(vm.IsPlaying ? "暂停" : "播放", vm.TogglePlayPause, gesture: Cmd(Key.P));
                    Add("上一集", () => vm.PreviousEpisodeCommand.Execute(null), vm.HasPreviousEpisode, Cmd(Key.Left));
                    Add("下一集", () => vm.NextEpisodeCommand.Execute(null), vm.HasNextEpisode, Cmd(Key.Right));
                    Separator();
                    Add("后退 10 秒", () => vm.SeekBy(-10), gesture: PlaybackKey(Key.Left, KeyModifiers.Shift));
                    Add("前进 10 秒", () => vm.SeekBy(10), gesture: PlaybackKey(Key.Right, KeyModifiers.Shift));
                    var episodes = Submenu("选集");
                    episodes.IsEnabled = vm.Visible && vm.Playlist.Count > 0;
                    for (var index = 0; index < vm.Playlist.Count; index++)
                    {
                        var target = index;
                        episodes.Menu!.Add(Choice(vm.Playlist[index].Title, () => _ = vm.PlayPlaylistIndexAsync(target), target == vm.PlaylistIndex, index < 9 ? PlaybackKey((Key)((int)Key.D1 + index), KeyModifiers.Alt) : null));
                    }
                    menu.Add(episodes);
                    Add("显示 / 隐藏播放列表", () => overlay?.OnTogglePlaylist(null, new RoutedEventArgs()), overlay is not null, Cmd(Key.L));
                    var rates = Submenu("播放速度");
                    var speeds = new[] { .5, .75, 1, 1.25, 1.5, 2 };
                    for (var index = 0; index < speeds.Length; index++)
                    {
                        var rate = speeds[index];
                        rates.Menu!.Add(Choice($"{rate:0.##}×", () => vm.Rate = rate, Math.Abs(vm.Rate - rate) < .001,
                            PlaybackKey((Key)((int)Key.D1 + index), KeyModifiers.Shift)));
                    }
                    rates.Menu!.Add(new NativeMenuItemSeparator());
                    rates.Menu.Add(Action("加快播放速度", () => vm.Rate = Math.Min(4, Math.Round(vm.Rate + .25, 2)), gesture: PlaybackKey(Key.Up, KeyModifiers.Shift)));
                    rates.Menu.Add(Action("减慢播放速度", () => vm.Rate = Math.Max(.25, Math.Round(vm.Rate - .25, 2)), gesture: PlaybackKey(Key.Down, KeyModifiers.Shift)));
                    rates.Menu.Add(Action("恢复正常速度", () => vm.Rate = 1, gesture: PlaybackKey(Key.D0, KeyModifiers.Shift)));
                    menu.Add(rates);
                    Separator();
                    Settings(label => label.StartsWith("自动下一集") || label.StartsWith("不记录") || label.StartsWith("跳过"));
                    Add("切换片头跳过时长", () =>
                    {
                        vm.OpeningSkipSeconds = new[] { 0, 30, 60, 90, 120 }.FirstOrDefault(seconds => seconds > vm.OpeningSkipSeconds);
                        vm.FlashToast(vm.OpeningSkipSeconds == 0 ? "已关闭跳过片头" : $"跳过片头：{vm.OpeningSkipSeconds} 秒");
                    }, gesture: PlaybackKey(Key.O, KeyModifiers.Alt));
                    Add("切换片尾跳过时长", () =>
                    {
                        vm.EndingSkipSeconds = new[] { 0, 30, 60, 90, 120 }.FirstOrDefault(seconds => seconds > vm.EndingSkipSeconds);
                        vm.FlashToast(vm.EndingSkipSeconds == 0 ? "已关闭跳过片尾" : $"跳过片尾：{vm.EndingSkipSeconds} 秒");
                    }, gesture: PlaybackKey(Key.E, KeyModifiers.Alt));
                    Separator();
                    Add("关闭播放", () => vm.CloseCommand.Execute(null), gesture: PlaybackKey(Key.W, KeyModifiers.Shift));
                }
                else if (title == "视频")
                {
                    menu.Add(Choice("全屏", () => { if (window is not null) window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; }, window?.WindowState == WindowState.FullScreen, PlaybackKey(Key.F, KeyModifiers.Control)));
                    menu.Add(Choice("画中画", () => { if (window is not null) overlay?.ToggleCompactWindow(window); }, vm.CompactMode, PlaybackKey(Key.P, KeyModifiers.Shift)));
                    menu.Add(Choice("始终置顶", () => { vm.AlwaysOnTop = !vm.AlwaysOnTop; if (window is not null) window.Topmost = vm.AlwaysOnTop; }, vm.AlwaysOnTop, PlaybackKey(Key.T, KeyModifiers.Control)));
                    Separator();
                    var ratios = Submenu("画面比例");
                    var ratioKey = (int)Key.D0;
                    foreach (var (label, ratio) in new (string, double?)[] { ("原始比例", null), ("16:9", 16d / 9), ("4:3", 4d / 3), ("1:1", 1d) })
                        ratios.Menu!.Add(Choice(label, () => _ = vm.SetAspectRatioAsync(ratio), vm.AspectRatio == ratio, PlaybackKey((Key)ratioKey++, KeyModifiers.Control)));
                    menu.Add(ratios);
                    Separator();
                    Settings(label => label.StartsWith("线路地址") || label.StartsWith("优先解析"));
                    Add("下一条播放线路", () => _ = vm.SelectAddressAsync((vm.AddressChoiceIndex + 1) % vm.AddressChoices.Count),
                        vm.AddressChoices.Count > 1, PlaybackKey(Key.L, KeyModifiers.Alt));
                    Add("下一条优先解析线路（下次播放生效）", () =>
                    {
                        var parsers = new[] { "" }.Concat(vm.AvailableParsers.Select(parser => parser.Url)).ToArray();
                        vm.PreferredParser = parsers[(Array.IndexOf(parsers, vm.PreferredParser) + 1) % parsers.Length];
                    }, vm.AvailableParsers.Count > 0, PlaybackKey(Key.P, KeyModifiers.Alt));
                }
                else if (title == "音频")
                {
                    menu.Add(Choice("静音", () => vm.ToggleMuteCommand.Execute(null), vm.IsMuted, PlaybackKey(Key.M, KeyModifiers.Shift)));
                    Add("增加音量", () => vm.Volume = Math.Clamp(vm.Volume + 5, 0, 100), gesture: Cmd(Key.Up));
                    Add("降低音量", () => vm.Volume = Math.Clamp(vm.Volume - 5, 0, 100), gesture: Cmd(Key.Down));
                    Separator();
                    Settings(label => label == "音轨");
                    Add("切换下一音轨", () => CycleTrack(TrackKind.Audio), vm.GetTracks(TrackKind.Audio).Count > 1, PlaybackKey(Key.A, KeyModifiers.Alt));
                }
                else
                {
                    var subtitles = settings.FirstOrDefault(item => item.Header?.ToString() == "字幕");
                    if (subtitles is not null)
                    {
                        var tracks = Submenu("字幕轨道");
                        var timing = Submenu("字幕同步");
                        var sizes = Submenu("字幕字号");
                        foreach (var item in subtitles.Items.OfType<MenuItem>())
                        {
                            var header = item.Header?.ToString() ?? "";
                            if (header.Contains("字幕字号")) sizes.Menu!.Add(Convert(item));
                            else if (header.StartsWith("字幕提前") || header.StartsWith("字幕延后") || header.StartsWith("恢复同步")) timing.Menu!.Add(Convert(item));
                            else if (header == "加载本地字幕…") menu.Add(Convert(item));
                            else tracks.Menu!.Add(Convert(item));
                        }
                        menu.Items.Insert(0, tracks);
                        menu.Add(timing);
                        menu.Add(sizes);
                    }
                    Add("切换下一字幕轨道", () => CycleTrack(TrackKind.Subtitle), vm.GetTracks(TrackKind.Subtitle).Count > 0, PlaybackKey(Key.S, KeyModifiers.Alt));
                    Add("增大字幕字号", () => vm.SubtitleFontSize += 2, gesture: PlaybackKey(Key.Up, KeyModifiers.Control));
                    Add("减小字幕字号", () => vm.SubtitleFontSize -= 2, gesture: PlaybackKey(Key.Down, KeyModifiers.Control));
                    Separator();
                    Settings(label => label.StartsWith("弹幕"));
                }
            }
            Refresh();
            menu.NeedsUpdate += (_, _) => Refresh();
            yield return new NativeMenuItem(title) { Menu = menu };
        }
    }
}
