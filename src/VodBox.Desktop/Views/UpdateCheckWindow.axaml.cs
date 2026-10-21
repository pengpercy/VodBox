using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace VodBox.Desktop.Views;

public partial class UpdateCheckWindow : Window
{
    private bool _finished;

    /// <summary>检查中点「取消」或关闭窗口时触发。</summary>
    public event Action? CancelRequested;

    public UpdateCheckWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += (_, _) => { if (!_finished) CancelRequested?.Invoke(); };
    }

    /// <summary>检查结束：收起进度条，按钮改为「好」。</summary>
    public void ShowResult(string headline, string detail)
    {
        _finished = true;
        this.FindControl<TextBlock>("Headline")!.Text = headline;
        this.FindControl<ProgressBar>("Progress")!.IsVisible = false;
        var text = this.FindControl<TextBlock>("Detail")!;
        text.Text = detail;
        text.IsVisible = detail.Length > 0;
        var action = this.FindControl<Button>("Action")!;
        action.Content = "好";
        action.IsDefault = true;
    }

    /// <summary>检查结束后直接关闭（例如由更新窗口接手）。</summary>
    public void Dismiss()
    {
        _finished = true;
        Close();
    }

    private void OnAction(object? sender, RoutedEventArgs e) => Close();
}
