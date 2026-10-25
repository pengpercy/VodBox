using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Path = System.IO.Path;

namespace VodBox.PreviewTests;

/// <summary>输入框聚焦态：中性描边、四边 1px，不再是系统强调色加 2px 底边；且仍比静止态更醒目。</summary>
public sealed class TextBoxFocusTests
{
    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void FocusedTextBoxUsesNeutralUniformBorder(string theme)
    {
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var focused = new TextBox { Text = "已聚焦的输入框", Width = 260 };
        var idle = new TextBox { PlaceholderText = "未聚焦的输入框", Width = 260 };
        var window = new Window
        {
            Width = 320, Height = 140, RequestedThemeVariant = variant,
            Background = (IBrush)Application.Current!.FindResource(variant, "PageBackgroundBrush")!,
            Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children = { focused, idle } },
        };
        window.Show();
        try
        {
            focused.Focus();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var border = focused.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
            var expected = (ISolidColorBrush)Application.Current!.FindResource(variant, "TextControlBorderBrushFocused")!;
            var accent = (Color)Application.Current!.FindResource(variant, "SystemAccentColor")!;
            var actual = Assert.IsAssignableFrom<ISolidColorBrush>(border.BorderBrush);
            Assert.Equal(expected.Color, actual.Color);
            Assert.NotEqual(accent, actual.Color);
            Assert.Equal(new Thickness(1), border.BorderThickness);
            // 聚焦档必须比静止档更醒目（同色相，比较不透明度），否则键盘焦点看不出来。
            var rest = (ISolidColorBrush)Application.Current!.FindResource(variant, "TextControlBorderBrush")!;
            Assert.True(actual.Color.A > rest.Color.A);
            // 浅色主题的聚焦底色不能回落到深色主题的半透明白。
            var background = (ISolidColorBrush)Application.Current!.FindResource(variant, "TextControlBackgroundFocused")!;
            if (variant == ThemeVariant.Light) Assert.Equal(Colors.White, background.Color);

            Avalonia.Media.Imaging.Bitmap? frame = null;
            for (var i = 0; i < 8 && frame is null; i++)
            {
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
                Dispatcher.UIThread.RunJobs();
                frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
            }
            if (frame is not null)
            {
                var dir = Path.Combine(Environment.CurrentDirectory, "preview-shots");
                Directory.CreateDirectory(dir);
                frame.Save(Path.Combine(dir, $"TextBoxFocus.{theme}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally
        {
            window.Close();
        }
    }
}
