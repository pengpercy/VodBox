using Avalonia;
using Avalonia.Controls;

namespace VodBox.Desktop.Views.Settings;

public partial class PlaybackSettingsView : UserControl
{
    /// <summary>本文件承载的设置分区：窗口为每个分区各挂一个实例，用 Section 区分。</summary>
    public enum PlaybackSettingsViewSection { Playback, Danmaku, Subtitles }

    public static readonly StyledProperty<PlaybackSettingsViewSection> SectionProperty =
        AvaloniaProperty.Register<PlaybackSettingsView, PlaybackSettingsViewSection>(nameof(Section));

    public PlaybackSettingsViewSection Section { get => GetValue(SectionProperty); set => SetValue(SectionProperty, value); }

    /// <summary>本实例是否承载「播放」分区（XAML 用 $parent 绑定做可见性开关）。</summary>
    public bool IsPlayback => Section == PlaybackSettingsViewSection.Playback;

    /// <summary>本实例是否承载「弹幕」分区。</summary>
    public bool IsDanmaku => Section == PlaybackSettingsViewSection.Danmaku;

    /// <summary>本实例是否承载「字幕」分区。</summary>
    public bool IsSubtitles => Section == PlaybackSettingsViewSection.Subtitles;

    private static readonly DirectProperty<PlaybackSettingsView, bool> IsPlaybackProperty =
        AvaloniaProperty.RegisterDirect<PlaybackSettingsView, bool>(nameof(IsPlayback), o => o.IsPlayback);

    private static readonly DirectProperty<PlaybackSettingsView, bool> IsDanmakuProperty =
        AvaloniaProperty.RegisterDirect<PlaybackSettingsView, bool>(nameof(IsDanmaku), o => o.IsDanmaku);

    private static readonly DirectProperty<PlaybackSettingsView, bool> IsSubtitlesProperty =
        AvaloniaProperty.RegisterDirect<PlaybackSettingsView, bool>(nameof(IsSubtitles), o => o.IsSubtitles);

    public PlaybackSettingsView() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SectionProperty) return;
        // Section 变化时手工通知三个只读派生属性，否则 XAML 的 IsVisible 不会跟着切。
        var before = change.OldValue is PlaybackSettingsViewSection old ? old : PlaybackSettingsViewSection.Playback;
        var now = change.NewValue is PlaybackSettingsViewSection current ? current : PlaybackSettingsViewSection.Playback;
        RaisePropertyChanged(IsPlaybackProperty, before == PlaybackSettingsViewSection.Playback, now == PlaybackSettingsViewSection.Playback);
        RaisePropertyChanged(IsDanmakuProperty, before == PlaybackSettingsViewSection.Danmaku, now == PlaybackSettingsViewSection.Danmaku);
        RaisePropertyChanged(IsSubtitlesProperty, before == PlaybackSettingsViewSection.Subtitles, now == PlaybackSettingsViewSection.Subtitles);
    }
}
