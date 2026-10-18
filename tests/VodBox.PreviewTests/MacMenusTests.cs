using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>macOS 菜单栏：应用菜单放关于/检查更新/设置；窗口菜单为文件/显示/播放/帮助，菜单项真的会执行对应操作。</summary>
public sealed class MacMenusTests
{
    private static NativeMenuItem Find(NativeMenu menu, string header) =>
        menu.Items.OfType<NativeMenuItem>().Single(item => item is not NativeMenuItemSeparator && item.Header == header);

    [AvaloniaFact]
    public void ApplicationMenuHasAboutUpdateAndSettingsWithStandardShortcut()
    {
        var directory = TempDataDirectory.Create("mac-menu-app");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        try
        {
            var menu = MacMenus.CreateApplicationMenu(main);
            Assert.Equal(["关于 VodBox", "检查更新…", "—", "设置…"],
                menu.Items.Select(item => item is NativeMenuItemSeparator ? "—" : ((NativeMenuItem)item).Header ?? ""));
            Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Meta), Find(menu, "设置…").Gesture);
            // 「关于」打开设置窗口并停在关于分区。
            var opened = 0;
            main.SettingsRequested += () => opened++;
            Find(menu, "关于 VodBox").Command!.Execute(null);
            Assert.Equal(1, opened);
            Assert.Equal(MacMenus.AboutSection, main.Settings.Section);
        }
        finally
        {
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    /// <summary>
    /// 回归：Avalonia 启动早期已给 Application 挂了带「About Avalonia」的默认菜单并导出；
    /// 安装时必须原地替换那份菜单的条目（导出器只跟踪它的条目变化），不能换成另一个 NativeMenu 对象。
    /// </summary>
    [AvaloniaFact]
    public void InstallReplacesItemsOfTheAlreadyExportedDefaultMenuInPlace()
    {
        var app = Avalonia.Application.Current!;
        var previous = NativeMenu.GetMenu(app);
        var directory = TempDataDirectory.Create("mac-menu-install");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        try
        {
            var exported = new NativeMenu();
            exported.Items.Add(new NativeMenuItem("About Avalonia"));
            NativeMenu.SetMenu(app, exported);
            MacMenus.InstallApplicationMenu(app, main);
            Assert.Same(exported, NativeMenu.GetMenu(app));
            var headers = exported.Items.Select(item => item is NativeMenuItemSeparator ? "—" : ((NativeMenuItem)item).Header ?? "").ToArray();
            Assert.Equal(["关于 VodBox", "检查更新…", "—", "设置…"], headers);
            Assert.DoesNotContain("About Avalonia", headers);

            // 还没有默认菜单时直接挂上（导出器之后读取）。
            NativeMenu.SetMenu(app, null!);
            MacMenus.InstallApplicationMenu(app, main);
            Assert.Equal("关于 VodBox", ((NativeMenuItem)NativeMenu.GetMenu(app)!.Items[0]).Header);
        }
        finally
        {
            NativeMenu.SetMenu(app, previous!);
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }

    [AvaloniaFact]
    public void WindowMenusNavigateOpenFilesAndTrackPlaybackState()
    {
        var directory = TempDataDirectory.Create("mac-menu-window");
        using var services = new AppServices(directory);
        var main = new MainViewModel(services, true);
        main.AttachDispatcher(Dispatcher.UIThread);
        try
        {
            var menu = MacMenus.CreateWindowMenu(main, directory, out var detach);
            Assert.Equal(["文件", "显示", "播放", "视频", "音频", "字幕", "帮助"], menu.Items.OfType<NativeMenuItem>().Where(item => item is not NativeMenuItemSeparator).Select(item => item.Header ?? ""));
            var file = Find(menu, "文件").Menu!;
            var show = Find(menu, "显示").Menu!;
            var play = Find(menu, "播放").Menu!;
            Assert.Equal(new KeyGesture(Key.O, KeyModifiers.Meta), Find(file, "打开文件…").Gesture);

            var requests = 0;
            main.OpenFileRequested += _ => requests++;
            Find(file, "打开文件…").Command!.Execute(null);
            Assert.Equal(1, requests);

            Find(show, "直播").Command!.Execute(null);
            Assert.Equal(AppPage.Live, main.Page);
            Find(show, "历史").Command!.Execute(null);
            Assert.Equal(AppPage.History, main.Page);
            Assert.Equal(new KeyGesture(Key.D1, KeyModifiers.Meta), Find(show, "首页").Gesture);

            Assert.False(Find(play, "播放").IsEnabled);
            main.Player.Visible = true;
            var updated = MacMenus.CreateWindowMenu(main, directory, out var updatedDetach);
            var updatedPlay = Find(updated, "播放").Menu!;
            Assert.True(Find(updatedPlay, "播放").IsEnabled);
            Assert.True(Find(updatedPlay, "关闭播放").IsEnabled);
            Assert.Equal(new KeyGesture(Key.Left, KeyModifiers.Meta), Find(updatedPlay, "上一集").Gesture);
            Find(Find(updated, "音频").Menu!, "增加音量").Command!.Execute(null);
            var video = Find(updated, "视频").Menu!;
            Find(Find(video, "画面比例").Menu!, "16:9").Command!.Execute(null);
            Find(Find(updatedPlay, "播放速度").Menu!, "1.5×").Command!.Execute(null);
            Assert.Equal(1.5, main.Player.Rate);
            detach();
            updatedDetach();
        }
        finally
        {
            services.Dispose();
            TempDataDirectory.TryDelete(directory);
        }
    }
}
