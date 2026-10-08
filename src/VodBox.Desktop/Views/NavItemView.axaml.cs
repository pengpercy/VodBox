using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace VodBox.Desktop.Views;

/// <summary>导航项：图标 + 文字 + 选中态（圆角高亮 + 左侧指示条）。</summary>
public partial class NavItemView : UserControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<NavItemView, Geometry?>(nameof(Icon));
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<NavItemView, string>(nameof(Label));
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<NavItemView, bool>(nameof(IsSelected));
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<NavItemView, ICommand?>(nameof(Command));

    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    public NavItemView()
    {
        AvaloniaXamlLoader.Load(this);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IconProperty || e.Property == LabelProperty || e.Property == IsSelectedProperty)
                UpdateVisual();
        };
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Command?.CanExecute(null) == true)
            {
                Command.Execute(null);
                e.Handled = true;
            }
        };
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        if (this.FindControl<PathIcon>("IconPath") is not { } icon) return;
        icon.Data = Icon;
        Foreground = new SolidColorBrush(IsSelected ? Colors.White : Color.FromUInt32(0xC7FFFFFF));
        if (this.FindControl<TextBlock>("LabelText") is { } label)
            label.Text = Label;
        if (this.FindControl<Border>("Shell") is { } shell)
            shell.Background = new SolidColorBrush(IsSelected ? Color.FromUInt32(0x12FFFFFF) : Colors.Transparent);
        if (this.FindControl<Border>("Indicator") is { } indicator) indicator.IsVisible = IsSelected;
    }
}
