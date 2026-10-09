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
        RequestedThemeVariant = Services.Prefs.GetInt("ui.theme", 0) switch
        {
            1 => Avalonia.Styling.ThemeVariant.Light, 2 => Avalonia.Styling.ThemeVariant.Dark, _ => Avalonia.Styling.ThemeVariant.Default,
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main=new MainViewModel(Services);
            var window=new MainWindow{DataContext=main};
            desktop.MainWindow=window;
            if(desktop.Args?.Contains("--ui-smoke")==true)
            {
                if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VODBOX_DATA_DIR")))throw new InvalidOperationException("UI smoke requires an isolated VODBOX_DATA_DIR.");
                window.Opened+=async(_,_)=>
                {
                    try
                    {
                        foreach(var page in new[]{AppPage.Home,AppPage.Vod,AppPage.Live,AppPage.Search,AppPage.Favorites,AppPage.History,AppPage.Files,AppPage.Settings})
                        {
                            main.Navigate(page);window.UpdateLayout();await Task.Delay(100);
                        }
                        for(var section=0;section<8;section++){main.Settings.Section=section;window.UpdateLayout();await Task.Delay(50);}
                        window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Light;window.UpdateLayout();await Task.Delay(100);
                        window.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;
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
                        Console.WriteLine("Native desktop UI: OK | 8 pages, 8 settings sections, light/dark, graceful shutdown");
                        window.Close();
                    }
                    catch(Exception error){Console.Error.WriteLine(error);desktop.Shutdown(1);}
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
