using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VodBox.Desktop.Views.Settings;

/// <summary>配置订阅 · 点播与豆瓣 · 直播与节目单 分区。</summary>
public partial class SourcesSettingsView : UserControl
{
    /// <summary>本文件承载的设置分区：窗口为三个分区各挂一个实例，用 Section 区分；缺省 All 用于兼容独立挂载。</summary>
    public enum SourcesSettingsViewSection { All, Subscription, Vod, Live }

    public static readonly StyledProperty<SourcesSettingsViewSection> SectionProperty =
        AvaloniaProperty.Register<SourcesSettingsView, SourcesSettingsViewSection>(nameof(Section), SourcesSettingsViewSection.All);

    public SourcesSettingsViewSection Section { get => GetValue(SectionProperty); set => SetValue(SectionProperty, value); }

    /// <summary>本实例是否承载「配置订阅」分区。</summary>
    public bool IsSubscription => Section is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Subscription;

    /// <summary>本实例是否承载「点播与豆瓣」分区。</summary>
    public bool IsVod => Section is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Vod;

    /// <summary>本实例是否承载「直播与节目单」分区。</summary>
    public bool IsLive => Section is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Live;

    private static readonly DirectProperty<SourcesSettingsView, bool> IsSubscriptionProperty =
        AvaloniaProperty.RegisterDirect<SourcesSettingsView, bool>(nameof(IsSubscription), o => o.IsSubscription);

    private static readonly DirectProperty<SourcesSettingsView, bool> IsVodProperty =
        AvaloniaProperty.RegisterDirect<SourcesSettingsView, bool>(nameof(IsVod), o => o.IsVod);

    private static readonly DirectProperty<SourcesSettingsView, bool> IsLiveProperty =
        AvaloniaProperty.RegisterDirect<SourcesSettingsView, bool>(nameof(IsLive), o => o.IsLive);

    public SourcesSettingsView() => AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SectionProperty) return;
        var before = change.OldValue is SourcesSettingsViewSection old ? old : SourcesSettingsViewSection.All;
        var now = change.NewValue is SourcesSettingsViewSection current ? current : SourcesSettingsViewSection.All;
        RaisePropertyChanged(IsSubscriptionProperty,
            before is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Subscription,
            now is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Subscription);
        RaisePropertyChanged(IsVodProperty,
            before is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Vod,
            now is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Vod);
        RaisePropertyChanged(IsLiveProperty,
            before is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Live,
            now is SourcesSettingsViewSection.All or SourcesSettingsViewSection.Live);
    }
}
