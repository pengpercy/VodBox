using Avalonia;
using Avalonia.Controls;

namespace VodBox.Desktop.Views.Settings;

public partial class RemoteDiagnosticsSettingsView : UserControl
{
    /// <summary>本文件承载的设置分区：窗口为每个分区各挂一个实例，用 Section 区分。</summary>
    public enum RemoteDiagnosticsSettingsViewSection { Remote, Diagnostics }

    public static readonly StyledProperty<RemoteDiagnosticsSettingsViewSection> SectionProperty =
        AvaloniaProperty.Register<RemoteDiagnosticsSettingsView, RemoteDiagnosticsSettingsViewSection>(nameof(Section));

    public RemoteDiagnosticsSettingsViewSection Section { get => GetValue(SectionProperty); set => SetValue(SectionProperty, value); }

    /// <summary>本实例是否承载「推送与遥控」分区（XAML 用 $parent 绑定做可见性开关）。</summary>
    public bool IsRemote => Section == RemoteDiagnosticsSettingsViewSection.Remote;

    /// <summary>本实例是否承载「诊断」分区。</summary>
    public bool IsDiagnostics => Section == RemoteDiagnosticsSettingsViewSection.Diagnostics;

    private static readonly DirectProperty<RemoteDiagnosticsSettingsView, bool> IsRemoteProperty =
        AvaloniaProperty.RegisterDirect<RemoteDiagnosticsSettingsView, bool>(nameof(IsRemote), o => o.IsRemote);

    private static readonly DirectProperty<RemoteDiagnosticsSettingsView, bool> IsDiagnosticsProperty =
        AvaloniaProperty.RegisterDirect<RemoteDiagnosticsSettingsView, bool>(nameof(IsDiagnostics), o => o.IsDiagnostics);

    public RemoteDiagnosticsSettingsView() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SectionProperty) return;
        // Section 变化时手工通知两个只读派生属性，否则 XAML 的 IsVisible 不会跟着切。
        // 这里不能新声明 PropertyChanged 事件：那会遮蔽 AvaloniaObject.PropertyChanged（CS0108）。
        var before = change.OldValue is RemoteDiagnosticsSettingsViewSection old ? old : RemoteDiagnosticsSettingsViewSection.Remote;
        var now = change.NewValue is RemoteDiagnosticsSettingsViewSection current ? current : RemoteDiagnosticsSettingsViewSection.Remote;
        RaisePropertyChanged(IsRemoteProperty, before == RemoteDiagnosticsSettingsViewSection.Remote, now == RemoteDiagnosticsSettingsViewSection.Remote);
        RaisePropertyChanged(IsDiagnosticsProperty, before == RemoteDiagnosticsSettingsViewSection.Diagnostics, now == RemoteDiagnosticsSettingsViewSection.Diagnostics);
    }
}
