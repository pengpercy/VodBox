using Avalonia.Controls;
using Avalonia.Input;
using VodBox.Core;

namespace VodBox.Desktop.Views;

public sealed partial class PlaybackControlsView : UserControl
{
    private MainViewModel? Model => DataContext as MainViewModel;
    public PlaybackControlsView() => InitializeComponent();
    private Task<string?> PickAsync(string title) => ViewFilePicker.PickAsync(this, title);
    private async void AddSubtitleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    { if (Model is not null && await PickAsync("打开字幕") is { } path) await Model.AddSubtitleAsync(path); }
    private void SeekPressed(object? sender, PointerPressedEventArgs e) { if (Model is not null) Model.IsScrubbing = true; }
    private async void SeekReleased(object? sender, PointerReleasedEventArgs e) { if (Model is not null) { await Model.SeekAsync(); Model.IsScrubbing = false; } }

}
