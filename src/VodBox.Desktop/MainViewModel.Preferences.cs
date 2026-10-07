using Avalonia.Styling;
using VodBox.Core;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    public IReadOnlyList<string> ThemeChoices { get; } = ["Dark", "Light", "System"];
    private string? _preferredSourceId;
    public async Task InitializeAsync()
    {
        var preferences = await _preferencesStore.LoadAsync(_lifetime.Token);
        var engineChoice = PlaybackEngineChoices.FirstOrDefault(x => x.Mode == preferences.PlaybackEngineMode) ?? PlaybackEngineChoices[0];
        await Engine.ChangeModeAsync(engineChoice.Mode, _lifetime.Token);
        SelectedPlaybackEngine = engineChoice;
        Volume = Math.Clamp(preferences.Volume, 0, 100); Rate = Math.Clamp(preferences.Rate, .25, 4);
        AutoNext = preferences.AutoNext; ResumePlayback = preferences.ResumePlayback;
        SkipIntroSeconds = Math.Clamp(preferences.SkipIntroSeconds, 0, 600); SkipOutroSeconds = Math.Clamp(preferences.SkipOutroSeconds, 0, 600);
        AudioDelayMs = Math.Clamp(preferences.AudioDelayMs, -10000, 10000); SubtitleDelayMs = Math.Clamp(preferences.SubtitleDelayMs, -10000, 10000);
        DanmakuEnabled = preferences.DanmakuEnabled; DanmakuFontSize = Math.Clamp(preferences.DanmakuFontSize, 16, 48); DanmakuOpacity = Math.Clamp(preferences.DanmakuOpacity, .1, 1); DanmakuCoverage = Math.Clamp(preferences.DanmakuCoverage, .25, 1); DanmakuDelayMs = Math.Clamp(preferences.DanmakuDelayMs, -60000, 60000);
        Theme = preferences.Theme; ApplyTheme(); _preferredSourceId = preferences.LastSourceId;
        _lastLiveConfigId = preferences.LastLiveConfigId; _lastLiveSourceId = preferences.LastLiveSourceId; _lastLiveChannelId = preferences.LastLiveChannelId;
        AutoLiveFallback = preferences.AutoLiveFallback; ResumeLiveOnStartup = preferences.ResumeLiveOnStartup;
        SearchHistory.Clear(); foreach (var term in preferences.SearchHistory.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(20)) SearchHistory.Add(term);
        _initialized = true; await RefreshSavedConfigurationsAsync(); await RefreshRecentHistoryAsync();
        var saved = SavedConfigurations.FirstOrDefault(x => x.Id == preferences.LastConfigId);
        if (saved is not null) { await LoadSavedConfigurationAsync(saved); if (ResumeLiveOnStartup) await ResumeLastLiveAsync(); return; }
        // Only restore a configuration explicitly selected in saved preferences.
        // Development snapshots must not become a first-launch playback source.
        ConfigLocation = ""; HomeRecommendationStatus = "请在设置中配置点播播放源。";
    }
    private AppPreferences CapturePreferences() => new()
    {
        DanmakuEnabled = DanmakuEnabled, DanmakuFontSize = DanmakuFontSize, DanmakuOpacity = DanmakuOpacity, DanmakuCoverage = DanmakuCoverage, DanmakuDelayMs = DanmakuDelayMs,
        SearchHistory = SearchHistory.ToList(),
        LastConfigId = _config.Id, LastSourceId = SelectedSource?.Id, Volume = Volume, Rate = Rate,
        PlaybackEngineMode = SelectedPlaybackEngine.Mode,
        AutoNext = AutoNext, ResumePlayback = ResumePlayback, SkipIntroSeconds = SkipIntroSeconds, SkipOutroSeconds = SkipOutroSeconds, Theme = Theme, LastLiveConfigId = _lastLiveConfigId, LastLiveSourceId = _lastLiveSourceId, LastLiveChannelId = _lastLiveChannelId, AutoLiveFallback = AutoLiveFallback, ResumeLiveOnStartup = ResumeLiveOnStartup, AudioDelayMs = AudioDelayMs, SubtitleDelayMs = SubtitleDelayMs
    };
    private void SchedulePreferencesSave()
    {
        if (!_initialized || _designMode || _disposed) return;
        _preferencesSave?.Cancel(); _preferencesSave?.Dispose(); _preferencesSave = new();
        var token = _preferencesSave.Token;
        _preferencesSaveTask = RunAsync(async () => { await Task.Delay(500, token); await _preferencesStore.SaveAsync(CapturePreferences(), token); }, false);
    }
    partial void OnAutoNextChanged(bool value) => SchedulePreferencesSave();
    partial void OnResumePlaybackChanged(bool value) => SchedulePreferencesSave();
    partial void OnSkipIntroSecondsChanged(int value) => SchedulePreferencesSave();
    partial void OnSkipOutroSecondsChanged(int value) => SchedulePreferencesSave();
    partial void OnThemeChanged(string value) { ApplyTheme(); SchedulePreferencesSave(); }
    private void ApplyTheme()
    {
        if (_designMode || Avalonia.Application.Current is not { } application) return;
        application.RequestedThemeVariant = Theme switch { "Light" => ThemeVariant.Light, "System" => ThemeVariant.Default, _ => ThemeVariant.Dark };
    }
}
