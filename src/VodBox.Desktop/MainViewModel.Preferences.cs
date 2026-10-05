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
        Volume = Math.Clamp(preferences.Volume, 0, 100); Rate = Math.Clamp(preferences.Rate, .25, 4);
        AutoNext = preferences.AutoNext; ResumePlayback = preferences.ResumePlayback;
        SkipIntroSeconds = Math.Clamp(preferences.SkipIntroSeconds, 0, 600); SkipOutroSeconds = Math.Clamp(preferences.SkipOutroSeconds, 0, 600);
        AudioDelayMs = Math.Clamp(preferences.AudioDelayMs, -10000, 10000); SubtitleDelayMs = Math.Clamp(preferences.SubtitleDelayMs, -10000, 10000);
        Theme = preferences.Theme; ApplyTheme(); _preferredSourceId = preferences.LastSourceId;
        _lastLiveConfigId = preferences.LastLiveConfigId; _lastLiveSourceId = preferences.LastLiveSourceId; _lastLiveChannelId = preferences.LastLiveChannelId;
        AutoLiveFallback = preferences.AutoLiveFallback; ResumeLiveOnStartup = preferences.ResumeLiveOnStartup;
        _initialized = true; await RefreshSavedConfigurationsAsync();
        var saved = SavedConfigurations.FirstOrDefault(x => x.Id == preferences.LastConfigId);
        if (saved is not null) { await LoadSavedConfigurationAsync(saved); if (ResumeLiveOnStartup) await ResumeLastLiveAsync(); return; }
        var legacySnapshot = Path.Combine(Infrastructure.AppPaths.DataDirectory, "config.json");
        ConfigLocation = File.Exists(legacySnapshot) ? legacySnapshot : Path.Combine(Core.AppLayout.AssetsDirectory, "examples", "vodbox.json");
        if (File.Exists(ConfigLocation)) await LoadConfigAsync();
    }
    private AppPreferences CapturePreferences() => new()
    {
        LastConfigId = _config.Id, LastSourceId = SelectedSource?.Id, Volume = Volume, Rate = Rate,
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
