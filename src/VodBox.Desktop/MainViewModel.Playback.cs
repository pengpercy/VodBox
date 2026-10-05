using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using VodBox.Application;
using VodBox.Core;

namespace VodBox.Desktop;

public sealed record PlaybackEngineChoice(string Name, PlaybackEngineMode Mode);

public partial class MainViewModel
{
    public IReadOnlyList<PlaybackEngineChoice> PlaybackEngineChoices { get; } =
    [new("自动选择", PlaybackEngineMode.Automatic), new("libmpv", PlaybackEngineMode.Mpv), new("LibVLC", PlaybackEngineMode.LibVlc)];
    [ObservableProperty] private PlaybackEngineChoice _selectedPlaybackEngine = new("自动选择", PlaybackEngineMode.Automatic);
    [ObservableProperty] private string _activeEngineText = "播放时自动选择内核";
    private IPlaybackEngine? _tracksEngine;
    private long _tracksRefreshAt;

    partial void OnSelectedPlaybackEngineChanged(PlaybackEngineChoice value) => _ = ChangePlaybackEngineAsync(value);
    private async Task ChangePlaybackEngineAsync(PlaybackEngineChoice value)
    {
        try
        {
            await Engine.ChangeModeAsync(value.Mode, _lifetime.Token);
            if (SelectedPlaybackEngine == value) SchedulePreferencesSave();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (SelectedPlaybackEngine != value) return;
            Status = error.Message;
            // Rejected selections must not persist a mode the router did not accept.
            SelectedPlaybackEngine = PlaybackEngineChoices.First(x => x.Mode == Engine.Mode);
            SchedulePreferencesSave();
        }
    }
    private void OnActiveEngineChanged(object? sender, ActivePlaybackEngine choice) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || !ReferenceEquals(Engine.ActiveEngine, choice.Engine)) return;
        ActiveEngineText = $"{(choice.Kind == PlaybackEngineKind.Mpv ? "libmpv" : "LibVLC")} · {choice.Reason}";
        _tracksEngine = null;
        SelectedAudio = null; SelectedSubtitle = null; AudioTracks.Clear(); SubtitleTracks.Clear();
    });
    private void RefreshTracks()
    {
        var engine = Engine.ActiveEngine;
        if (ReferenceEquals(engine, _tracksEngine) && System.Diagnostics.Stopwatch.GetElapsedTime(_tracksRefreshAt).TotalSeconds < 1) return;
        _tracksEngine = engine; _tracksRefreshAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Synchronize(AudioTracks, Engine.GetTracks(TrackKind.Audio));
        Synchronize(SubtitleTracks, Engine.GetTracks(TrackKind.Subtitle));
        static void Synchronize(ObservableCollection<MediaTrack> target, IReadOnlyList<MediaTrack> tracks)
        {
            if (target.SequenceEqual(tracks)) return;
            target.Clear(); foreach (var track in tracks) target.Add(track);
        }
    }
}
