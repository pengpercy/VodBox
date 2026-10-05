using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using VodBox.Core;

namespace VodBox.Desktop.Views;

public sealed partial class PlaybackControlsView : UserControl
{
    private static readonly Avalonia.Media.Geometry ExpandIcon = Avalonia.Media.Geometry.Parse("M0,6 V0 H6 M12,0 H18 V6 M18,12 V18 H12 M6,18 H0 V12");
    private static readonly Avalonia.Media.Geometry CollapseIcon = Avalonia.Media.Geometry.Parse("M0,6 H6 V0 M12,0 V6 H18 M18,12 H12 V18 M6,18 V12 H0");
    internal MainWindow? OwnerWindow { get; set; }
    public void SetFullscreenPresentation(bool fullscreen)
    {
        FullscreenIcon.Data = fullscreen ? CollapseIcon : ExpandIcon;
    }
    private MainViewModel? Model => DataContext as MainViewModel;
    public PlaybackControlsView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateTransportLayout();
        SeekSlider.TemplateApplied += (_, _) =>
        {
            foreach (var thumb in SeekSlider.GetVisualDescendants().OfType<Thumb>())
            { thumb.Width = 16; thumb.Height = 16; thumb.MinWidth = 0; thumb.MinHeight = 0; }
        };
    }
    private void UpdateTransportLayout()
    {
        bool wide = Bounds.Width >= 900;
        Grid.SetColumn(PlaybackCluster, 0);
        Grid.SetColumnSpan(PlaybackCluster, 9);
        PlaybackCluster.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        TransportRow.ColumnSpacing = wide ? 6 : 4;
        MediaTitle.MaxWidth = Math.Max(0, (Bounds.Width - 24 - 250) / 2 - 12);
        AudioButton.IsVisible = wide; StopButton.IsVisible = wide;
        CompactMediaOptions.IsVisible = !wide;
        VolumePercent.IsVisible = Bounds.Width >= 680;
        RateButton.IsVisible = Bounds.Width >= 560;
        CompactRateOption.IsVisible = !RateButton.IsVisible;
        ToolTip.SetTip(SubtitleButton, wide ? "字幕" : "音轨 / 字幕");
    }
    private void CompactRateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    { SubtitleButton.Flyout?.Hide(); RateButton.Flyout?.ShowAt(SubtitleButton); }
    private void RatePresetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Model is not null && sender is Button { Tag: string value } && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rate))
        { Model.Rate = rate; RateButton.Flyout?.Hide(); }
    }
    private void MuteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args) => Model?.ToggleMute();
    private Task<string?> PickAsync(string title) => ViewFilePicker.PickAsync(this, title);
    private async void AddSubtitleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && await PickAsync("打开字幕") is { } path) await Model.AddSubtitleAsync(path); }
    private void FullscreenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if ((OwnerWindow ?? TopLevel.GetTopLevel(this) as MainWindow) is { } window) window.ToggleFullscreen(); }
    private void SeekPressed(object? sender, PointerPressedEventArgs e) { if (Model is not null) Model.IsScrubbing = true; }
    private async void SeekReleased(object? sender, PointerReleasedEventArgs e) { if (Model is not null) { await Model.SeekAsync(); Model.IsScrubbing = false; } }

}
