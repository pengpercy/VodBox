using System.ComponentModel;
using Avalonia.Controls;

namespace VodBox.Desktop;

public sealed partial class MainWindow
{
    public SettingsWindow? SettingsWindow { get; private set; }
    private void InitializeSettingsWindow(MainViewModel model)
    {
        void Open(object? sender, EventArgs args)
        {
            if (SettingsWindow is { } existing)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate(); return;
            }
            var window = new SettingsWindow(model);
            SettingsWindow = window;
            window.Closed += (_, _) =>
            {
                SettingsWindow = null;
                model.ShowSettings = false;
            };
            window.Show(this);
        }
        void StateChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.ShowSettings) && !model.ShowSettings) SettingsWindow?.Close();
        }
        model.SettingsRequested += Open;
        model.PropertyChanged += StateChanged;
        Closed += (_, _) =>
        {
            model.SettingsRequested -= Open;
            model.PropertyChanged -= StateChanged;
            SettingsWindow?.Close();
        };
    }
}
