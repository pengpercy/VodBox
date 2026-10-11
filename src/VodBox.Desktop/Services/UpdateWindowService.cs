using Avalonia.Controls;
using VodBox.Desktop.Views;

namespace VodBox.Desktop.Services;

/// <summary>One update window at a time; automatic offers are deduplicated only within this session.</summary>
public sealed class UpdateWindowService
{
    private UpdateWindow? _currentWindow;
    private readonly HashSet<string> _automaticOffers = new(StringComparer.Ordinal);

    public bool ActivateExisting()
    {
        if (_currentWindow is not { } window) return false;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        return true;
    }

    public void Show(UpdateOffer offer, string currentVersion, Window owner, bool automatic, Action<string> report)
    {
        if (_currentWindow is not null)
        {
            if (!automatic) ActivateExisting();
            return;
        }
        if (automatic && _automaticOffers.Contains(offer.Version)) return;
        var window = new UpdateWindow(offer, currentVersion, report);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_currentWindow, window)) _currentWindow = null;
        };
        _currentWindow = window;
        try
        {
            window.Show(owner);
            _automaticOffers.Add(offer.Version);
        }
        catch { _currentWindow = null; throw; }
    }

    public async Task CloseAsync()
    {
        if (_currentWindow is { } window) await window.CloseForShutdownAsync();
    }
}
