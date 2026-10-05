using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace VodBox.Desktop.Themes;

public sealed partial class DesktopTheme : Styles
{
    public DesktopTheme() => AvaloniaXamlLoader.Load(this);
}
