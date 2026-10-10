using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop;

public class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime validationLifetime&&validationLifetime.Args?.Contains("--ui-smoke")==true&&string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VODBOX_DATA_DIR")))
            throw new InvalidOperationException("UI smoke requires an isolated VODBOX_DATA_DIR.");
        Services = new AppServices();
        VodBox.Core.VodBoxLog.Initialize(Services.DataDir);
        VodBox.Core.VodBoxLog.Info("app", $"VodBox {typeof(App).Assembly.GetName().Version} 启动 | data={Services.DataDir}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            VodBox.Core.VodBoxLog.Error("app", "未处理的异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            VodBox.Core.VodBoxLog.Error("app", "未观察的任务异常", e.Exception);
            e.SetObserved();
        };
        RequestedThemeVariant = Services.Prefs.GetInt("ui.theme", 0) switch
        {
            1 => Avalonia.Styling.ThemeVariant.Light, 2 => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default,
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main=new MainViewModel(Services);
            var window=new MainWindow{DataContext=main};
            desktop.MainWindow=window;
            if (desktop.Args?.Contains("--ui-smoke") != true)
            {
                window.Opened += async (_, _) =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    if (window.IsVisible && main.Settings.AutoCheckUpdates)
                        await main.Settings.CheckUpdateAsync(); // Only updates the settings label; never interrupts playback.
                };
            }
            if(desktop.Args?.Contains("--ui-smoke")==true)
            {
                if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VODBOX_DATA_DIR")))throw new InvalidOperationException("UI smoke requires an isolated VODBOX_DATA_DIR.");
                window.Opened+=async(_,_)=>
                {
                    try
                    {
                        foreach(var page in new[]{AppPage.Home,AppPage.Vod,AppPage.Live,AppPage.Search,AppPage.Favorites,AppPage.History,AppPage.Files})
                        {
                            main.Navigate(page);window.UpdateLayout();await Task.Delay(100);
                        }
                        // 设置已改为独立窗口：逐个分区在真实窗口里渲染一遍，确认没有空分区或绑定失败。
                        var settingsWindow=window.SettingsWindowForSmoke??throw new InvalidOperationException("设置窗口未创建。");
                        for(var section=0;section<9;section++)
                        {
                            main.Settings.Section=section;
                            settingsWindow.SyncSelectionFrom(main.Settings);
                            settingsWindow.UpdateLayout();await Task.Delay(50);
                        }
                        main.Settings.RefreshLog();
                        if(string.IsNullOrWhiteSpace(main.Settings.LogTail))throw new InvalidOperationException("诊断区没有读到日志内容。");
                        window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Light;window.UpdateLayout();await Task.Delay(100);
                        window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;
                        // 直播播放验证：--live=<m3u 路径> 用与界面完全相同的路径选频道播放并报告结果，
                        // 便于在没有界面的情况下确认“能解析但播不出来”是应用问题还是源本身的问题。
                        var liveArg=desktop.Args.FirstOrDefault(argument=>argument.StartsWith("--live=",StringComparison.Ordinal));
                        if(liveArg is not null)
                        {
                            var playlistPath=liveArg[7..];
                            var loaded=await main.Live.ApplyConfigurationAsync(playlistPath);
                            var channels=main.Live.Groups.SelectMany(group=>group.Channels).ToArray();
                            var channel=channels.FirstOrDefault()??throw new InvalidOperationException("直播播放列表没有频道。");
                            Console.WriteLine($"LIVE LOADED: ok={loaded} groups={main.Live.Groups.Count} channels={channels.Length}");
                            main.Live.PlayChannel(channel);
                            var deadline=DateTime.UtcNow.AddSeconds(30);
                            while(main.Player.State is VodBox.Core.PlaybackState.Resolving or VodBox.Core.PlaybackState.Loading&&DateTime.UtcNow<deadline)
                                await Task.Delay(100);
                            await Task.Delay(4000);
                            var overlay3=window.PlaybackWindow?.Overlay;
                            Console.WriteLine($"LIVE PLAY: channel={channel.Name} state={main.Player.State} error={main.Player.Error??"<none>"} "+
                                $"position={main.Player.Position.TotalSeconds:F2}s frames={overlay3?.RenderedFrames??0} "+
                                $"videoRatio={main.Player.VideoAspectRatio?.ToString("F3")??"<none>"} visible={main.Player.Visible}");
                            await main.ShutdownAsync();window.Close();return;
                        }
                        // 网络流验证：--url=<http(s) 地址> 走与直播频道完全相同的播放链路
                        // （真实窗口 + OpenGL 渲染面），用于排查“能解析但播不出来”的问题。
                        var network=desktop.Args.FirstOrDefault(argument=>argument.StartsWith("--url=",StringComparison.Ordinal));
                        if(network is not null)
                        {
                            var address=network[6..];
                            main.Player.Play(new VodBox.Core.PlaybackRequest
                            {
                                Uri=address,Title="网络流验证",SourceKey="smoke",SourceName="验收",
                                MediaId=address,IsLive=true,
                            });
                            var deadline=DateTime.UtcNow.AddSeconds(30);
                            while(main.Player.State is VodBox.Core.PlaybackState.Resolving or VodBox.Core.PlaybackState.Loading&&DateTime.UtcNow<deadline)
                                await Task.Delay(100);
                            await Task.Delay(3000);
                            var window2=window;
                            var overlay2=window2.PlaybackWindow?.Overlay;
                            Console.WriteLine($"NETWORK STREAM: state={main.Player.State} error={main.Player.Error??"<none>"} "+
                                $"position={main.Player.Position.TotalSeconds:F2}s duration={main.Player.Duration.TotalSeconds:F2}s "+
                                $"frames={overlay2?.RenderedFrames??0} videoRatio={main.Player.VideoAspectRatio?.ToString("F3")??"<none>"} "+
                                $"visible={main.Player.Visible}");
                            await main.ShutdownAsync();window.Close();return;
                        }
                        var media=desktop.Args.FirstOrDefault(argument=>argument.StartsWith("--media=",StringComparison.Ordinal));
                        if(media is not null)
                        {
                            var path=Path.GetFullPath(media[8..]);if(!File.Exists(path))throw new FileNotFoundException("UI smoke media missing.",path);
                            var entries=new[]{"ep1","ep2"}.Select(id=>new PlaylistEntry(id,id,_=>Task.FromResult(new VodBox.Core.PlaybackRequest
                            {Uri=new Uri(path).AbsoluteUri,Title="Native UI media",SourceKey="smoke",SourceName="验收",MediaId="series",LineId="line",EpisodeId=id}))).ToArray();
                            main.Player.AutoNext=true;
                            var opening=main.Player.PlayResolvedAsync(entries[0].Resolve,entries,0);
                            await opening.WaitAsync(TimeSpan.FromSeconds(20));
                            async Task Wait(Func<bool> condition)
                            {
                                var deadline=DateTime.UtcNow.AddSeconds(10);
                                while(!condition()){if(DateTime.UtcNow>deadline)throw new TimeoutException("native player UI");await Task.Delay(50);}
                            }
                            var playerWindow=window.PlaybackWindow??throw new InvalidOperationException("Independent player window missing.");
                            var overlay=playerWindow.Overlay;
                            var shellWidth=window.Width;var shellHeight=window.Height;
                            try{await Wait(()=>overlay.RenderedFrames>10&&main.Player.Position.TotalSeconds>1);}
                            catch{Console.Error.WriteLine($"player diagnostics: frames={overlay.RenderedFrames}, state={main.Player.State}, position={main.Player.Position}, visible={overlay.IsVisible}, bounds={overlay.Bounds}, error={main.Player.Error}");throw;}
                            if(desktop.Args.Contains("--ratio-smoke"))
                            {
                                await Wait(()=>main.Player.VideoAspectRatio is >0);
                                var ratio=main.Player.VideoAspectRatio!.Value;
                                if(Math.Abs(playerWindow.Width/playerWindow.Height-ratio)>.005)throw new InvalidOperationException($"window/video ratio mismatch: {playerWindow.Width}x{playerWindow.Height}, video={ratio}");
                                var screen=playerWindow.Screens.ScreenFromWindow(playerWindow)??playerWindow.Screens.Primary;
                                if(screen is not null&&(playerWindow.Width*screen.Scaling>screen.WorkingArea.Width||playerWindow.Height*screen.Scaling>screen.WorkingArea.Height))throw new InvalidOperationException("player window exceeds screen");
                                var fittedWidth=playerWindow.Width;var fittedHeight=playerWindow.Height;
                                overlay.ToggleCompactWindow(window);await Task.Delay(150);
                                if(Math.Abs(playerWindow.Width/playerWindow.Height-ratio)>.005)throw new InvalidOperationException("compact window ratio mismatch");
                                overlay.RestoreWindow();
                                if(Math.Abs(playerWindow.Width-fittedWidth)>.5||Math.Abs(playerWindow.Height-fittedHeight)>.5)throw new InvalidOperationException("compact size restore mismatch");
                                await main.Player.CloseCommand.ExecuteAsync(null);
                                if(window.MinWidth!=960||window.MinHeight!=600)throw new InvalidOperationException("shell constraints not restored");
                                Console.WriteLine($"Native player ratio: OK | video={ratio:F6}, window={fittedWidth:F1}x{fittedHeight:F1}, compact/bounds/restore");
                                await main.ShutdownAsync();window.Close();return;
                            }
                            await Services.Player.PauseAsync();await Wait(()=>main.Player.State==VodBox.Core.PlaybackState.Paused);
                            main.Player.Seek(TimeSpan.FromSeconds(3));await Wait(()=>Math.Abs(main.Player.Position.TotalSeconds-3)<.2);
                            playerWindow.WindowState=Avalonia.Controls.WindowState.FullScreen;
                            await Wait(()=>playerWindow.WindowState==Avalonia.Controls.WindowState.FullScreen);await Task.Delay(1200);
                            playerWindow.WindowState=Avalonia.Controls.WindowState.Normal;
                            await Wait(()=>playerWindow.WindowState==Avalonia.Controls.WindowState.Normal);await Task.Delay(1200);
                            var width=playerWindow.Width;overlay.ToggleCompactWindow(window);await Task.Delay(200);overlay.RestoreWindow();
                            if(playerWindow.Width!=width||main.Player.CompactMode)throw new InvalidOperationException("compact restore mismatch");
                            await Services.Player.PlayAsync();await Wait(()=>main.Player.State==VodBox.Core.PlaybackState.Playing);
                            var firstSession=main.Player.CurrentSessionId;
                            main.Player.Seek(main.Player.Duration);
                            try{await Wait(()=>main.Player.PlaylistIndex==1&&main.Player.CurrentSessionId!=firstSession&&main.Player.State==VodBox.Core.PlaybackState.Playing);}
                            catch{Console.Error.WriteLine($"auto-next diagnostics: index={main.Player.PlaylistIndex},session={main.Player.CurrentSessionId},old={firstSession},state={main.Player.State},pos={main.Player.Position},dur={main.Player.Duration},error={main.Player.Error}");throw;}
                            await Wait(()=>main.Player.Position.TotalSeconds>.2);
                            if (!ReferenceEquals(window.PlaybackWindow,playerWindow)) throw new InvalidOperationException("Episode switch created another player window.");
                            if (window.Width!=shellWidth||window.Height!=shellHeight) throw new InvalidOperationException("Player resized the main window.");
                            var renderedFrames=overlay.RenderedFrames;
                            await main.Player.CloseCommand.ExecuteAsync(null);
                            if(window.PlaybackWindow is not null)throw new InvalidOperationException("Player did not close independently.");
                            var history=await Services.Store.FindHistoryAsync("smoke","series");
                            if(history?.EpisodeId!="ep2")throw new InvalidOperationException("automatic next history episode mismatch");
                            Console.WriteLine($"Native main player: OK | frames={renderedFrames}, independent single-window/main bounds/pause/seek/fullscreen/compact restore/resume/automatic-next/history");
                        }
                        await main.ShutdownAsync();
                        Console.WriteLine("Native desktop UI: OK | 8 pages, 9 settings sections, light/dark, graceful shutdown");
                        window.Close();
                    }
                    catch(Exception error){Console.Error.WriteLine(error);desktop.Shutdown(1);}
                };
            }
        }
        VodBox.Core.VodBoxLog.Info("app", "界面初始化完成");
        base.OnFrameworkInitializationCompleted();
    }
}
