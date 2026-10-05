using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using VodBox.Core;
using VodBox.Desktop;
using VodBox.Playback.LibVlc;
using VodBox.Playback.Mpv;

internal static class DesktopProbe
{
    public static Window Create(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var model = new MainViewModel(designMode: true, engineFactory: kind =>
        {
            if (kind == PlaybackEngineKind.Mpv) return new MpvEngine(factory: () => new MpvClient(new Dictionary<string, string>
            { ["vo"] = "libmpv", ["ao"] = "null", ["hwdec"] = "no" }), waitForVideoSurface: true);
            var vlc = new LibVlcEngine(waitForVideoSurface: true); vlc.Initialized += (_, _) => vlc.Player!.Volume = 0; return vlc;
        }) { Volume = 0, AutoNext = false };
        var window = new MainWindow(model, initialize: false);
        window.Opened += async (_, _) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await model.OpenFileAsync(Program.FixturePath);
                Console.WriteLine($"MainWindow opened media: kind={model.Engine.ActiveKind}, state={model.Engine.Snapshot.State}, status={model.Status}");
                if (Program.FallbackMode)
                {
                    await Until(() => model.Engine.ActiveKind == PlaybackEngineKind.LibVlc && model.Engine.Snapshot.State == PlaybackState.Playing && model.Engine.Snapshot.Position.TotalSeconds > 1);
                    var fallback = (LibVlcEngine)model.Engine.ActiveEngine!;
                    using var media = fallback.Player!.Media!;
                    await Until(() => fallback.Player.VideoTrack >= 0 && media.Statistics.DecodedVideo > 0);
                    var fallbackSurface = window.FindControl<VlcVideoSurface>("VideoSurface")!;
                    if (!fallbackSurface.IsVisible || !ReferenceEquals(fallbackSurface.MediaPlayer, fallback.Player)) throw new InvalidOperationException("Fallback VLC surface not attached.");
                    model.DanmakuComments = [new(0, "Fallback danmaku", DanmakuMode.Top)]; await Until(() => window.ActiveDanmakuCount > 0);
                    Console.WriteLine($"MainWindow native fallback: OK | unavailable mpv -> LibVLC, decodedVideo={media.Statistics.DecodedVideo}, native surface/danmaku, settings stay automatic");
                    window.Close(); return;
                }
                await Until(() => window.MpvRenderedFrames >= 10 && model.Engine.Snapshot.Position.TotalSeconds > 1);
                if (model.Engine.ActiveKind != PlaybackEngineKind.Mpv) throw new InvalidOperationException("Main window failed to select/render mpv: " + model.Status);
                model.DanmakuComments = [new(0, "MainWindow danmaku probe", DanmakuMode.Top)];
                await Until(() => window.ActiveDanmakuCount > 0);
                await model.TogglePauseCommand.ExecuteAsync(null); await Until(() => model.Engine.Snapshot.State == PlaybackState.Paused);
                await model.Engine.SeekAsync(TimeSpan.FromSeconds(2.5), timeout.Token);
                await Until(() => Math.Abs(model.Engine.Snapshot.Position.TotalSeconds - 2.5) < .2);
                model.SelectedPlaybackEngine = model.PlaybackEngineChoices.Single(x => x.Mode == PlaybackEngineMode.LibVlc);
                await Until(() => model.Engine.ActiveKind == PlaybackEngineKind.LibVlc && model.Engine.Snapshot.State == PlaybackState.Paused);
                var vlc = (LibVlcEngine)model.Engine.ActiveEngine!;
                await Until(() => vlc.Player!.VideoTrack >= 0 && Math.Abs(model.Engine.Snapshot.Position.TotalSeconds - 2.5) < .3);
                using (var media = vlc.Player!.Media!) await Until(() => vlc.Player.VoutCount > 0 && media.Statistics.DecodedVideo > 0);
                var surface = window.FindControl<VlcVideoSurface>("VideoSurface")!;
                if (!surface.IsVisible || !ReferenceEquals(surface.MediaPlayer, vlc.Player)) throw new InvalidOperationException("VLC surface not attached.");
                await Until(() => vlc.Player!.Volume == 0);
                await Until(() => window.ActiveDanmakuCount > 0);
                long previous = window.MpvRenderedFrames;
                model.SelectedPlaybackEngine = model.PlaybackEngineChoices.Single(x => x.Mode == PlaybackEngineMode.Mpv);
                await Until(() => model.Engine.ActiveKind == PlaybackEngineKind.Mpv && model.Engine.Snapshot.State == PlaybackState.Paused && window.MpvRenderedFrames > previous);
                if (surface.IsVisible || surface.MediaPlayer is not null) throw new InvalidOperationException("VLC native view not detached.");
                await Until(() => window.ActiveDanmakuCount > 0);
                window.Width = 900;
                await Until(() => !window.FindControl<Grid>("BrowsePane")!.IsVisible && window.FindControl<Grid>("PlaybackPane")!.IsVisible);
                var renderSurface = window.FindControl<Grid>("VideoContainer")!.Children.OfType<MpvVideoSurface>().Single();
                model.ShowPlaybackPage = false;
                await Until(() => window.FindControl<Grid>("BrowsePane")!.IsVisible && !window.FindControl<Grid>("PlaybackPane")!.IsVisible);
                model.ShowPlaybackPage = true; window.Width = 1280;
                await Until(() => window.FindControl<Grid>("BrowsePane")!.IsVisible && window.FindControl<Grid>("PlaybackPane")!.IsVisible);
                if (!ReferenceEquals(renderSurface, window.FindControl<Grid>("VideoContainer")!.Children.OfType<MpvVideoSurface>().Single())) throw new InvalidOperationException("Resize recreated the native renderer.");
                await model.TogglePauseCommand.ExecuteAsync(null);
                await Until(() => window.MpvRenderedFrames > previous + 5 && model.Engine.Snapshot.Position.TotalSeconds > 2.7);
                Console.WriteLine($"MainWindow dual engines: OK | mpv/VLC/mpv, frames={window.MpvRenderedFrames}, pause/position, device mute, surface reuse, settings binding, inline/native-overlay danmaku, narrow/wide layout");
                window.Close();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error); desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.Closed += (_, _) => desktop.Shutdown(1); window.Close();
            }
            async Task Until(Func<bool> condition)
            {
                while (!condition())
                {
                    if (model.Engine.Snapshot.State == PlaybackState.Failed) throw new InvalidOperationException(model.Engine.Snapshot.Error);
                    try { await Task.Delay(50, timeout.Token); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        var container = window.FindControl<Grid>("VideoContainer")!;
                        var native = (model.Engine.ActiveEngine as LibVlcEngine)?.Player;
                        throw new TimeoutException($"Desktop stage timed out: kind={model.Engine.ActiveKind}, state={model.Engine.Snapshot.State}, position={model.Engine.Snapshot.Position.TotalSeconds:F2}, frames={window.MpvRenderedFrames}, vlcVolume={native?.Volume}, vlcDrawable={native?.NsObject}, status={model.Status}, surfaces={string.Join(';', container.Children.Select(x => $"{x.GetType().Name}:{x.Bounds}:{x.IsVisible}"))}");
                    }
                }
            }
        };
        return window;
    }
}
