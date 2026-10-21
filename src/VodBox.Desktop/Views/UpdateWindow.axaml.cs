using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateViewModel _viewModel;
    private bool _closing;
    private bool _closeComplete;

    // Parameterless constructor keeps the compiled XAML reachable for previews/runtime loading.
    public UpdateWindow() : this(new UpdateViewModel(new UpdateOffer("未知", "", new Uri("https://github.com/pengpercy/VodBox/releases"), "", "https://github.com/pengpercy/VodBox/releases"), "未知", _ => { }))
    {
    }

    public UpdateWindow(UpdateViewModel viewModel)
    {
        _viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = _viewModel;
        this.FindControl<ContentControl>("Notes")!.Content = RenderNotes(_viewModel.ReleaseNotes);
        Closing += OnClosing;
        _viewModel.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelChanged;
        ApplyLayout();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateViewModel.IsOffer)) ApplyLayout();
    }

    /// <summary>发现新版本：可缩放的发行说明窗口；更新进行中：固定宽度、高度随内容的进度面板。</summary>
    private void ApplyLayout()
    {
        if (_viewModel.IsOffer)
        {
            SizeToContent = SizeToContent.Manual;
            CanResize = true;
            MinWidth = 520; MinHeight = 360;
            Width = 640; Height = 420;
        }
        else
        {
            CanResize = false;
            MinWidth = 0; MinHeight = 0;
            Width = 440;
            SizeToContent = SizeToContent.Height;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => _viewModel.CancelDownload();
    private void OnOpenDownloadPage(object? sender, RoutedEventArgs e) => _viewModel.OpenDownloadPage();
    private void OnRemindLater(object? sender, RoutedEventArgs e) => Close();
    private async void OnInstall(object? sender, RoutedEventArgs e) => await _viewModel.InstallAsync();

    private async void OnInstallNow(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.InstallNowAsync()) App.ShutdownForUpdate();
    }

    private async void OnSkip(object? sender, RoutedEventArgs e)
    {
        await _viewModel.SkipAsync();
        Close();
    }

    private void OnReleasePage(object? sender, RoutedEventArgs e)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo { FileName = _viewModel.ReleaseUrl, UseShellExecute = true }) ?? throw new InvalidOperationException("无法打开浏览器。");
        }
        catch (Exception error) { _viewModel.ErrorText = $"打开发行页面失败：{error.Message}"; }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeComplete) return;
        if (!_viewModel.CanClose) { e.Cancel = true; return; }
        // 自动更新的会话继续后台准备；已结束的会话无需清理。
        if (_viewModel.InstallOnQuit || _viewModel.IsFinished) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        await _viewModel.WindowClosedAsync();
        _closeComplete = true;
        Close();
    }

    /// <summary>应用退出：直接收起，会话的安装或清理由 <see cref="UpdateWindowService.CloseAsync"/> 负责。</summary>
    internal void CloseForShutdown()
    {
        _closeComplete = true;
        Close();
    }

    private static readonly Regex InlineToken = new(@"\*\*(.+?)\*\*|__(.+?)__|`([^`]+)`|\[([^\]]+)\]\([^)]+\)", RegexOptions.CultureInvariant);
    private static readonly Regex Bullet = new(@"^(\s*)(?:[-*+]|\d+[.)])\s+(.*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// GitHub 发行说明的轻量 Markdown：标题、列表、粗体、行内代码与链接文字。不加载远程图片，未识别的语法按原文显示。
    /// </summary>
    internal static Control RenderNotes(string markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            panel.Children.Add(Text(string.Join(" ", paragraph), 13, FontWeight.Normal));
            paragraph.Clear();
        }
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { FlushParagraph(); continue; }
            if (line.TrimStart().StartsWith('#'))
            {
                FlushParagraph();
                var trimmed = line.TrimStart();
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var heading = Text(trimmed[level..].Trim(), level <= 1 ? 20 : level == 2 ? 17 : 15, FontWeight.Bold);
                heading.Margin = new Avalonia.Thickness(0, panel.Children.Count == 0 ? 0 : 8, 0, 2);
                panel.Children.Add(heading);
                continue;
            }
            if (Bullet.Match(line) is { Success: true } bullet)
            {
                FlushParagraph();
                var indent = bullet.Groups[1].Value.Replace("\t", "  ").Length / 2;
                var ordered = char.IsDigit(line.TrimStart()[0]);
                var marker = ordered ? line.TrimStart()[..(line.TrimStart().IndexOf(' '))] : "•";
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Avalonia.Thickness(16 + indent * 18, 0, 0, 0) };
                row.Children.Add(new TextBlock { Text = marker, FontSize = 13, Margin = new Avalonia.Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top });
                var content = Text(bullet.Groups[2].Value, 13, FontWeight.Normal);
                Grid.SetColumn(content, 1);
                row.Children.Add(content);
                panel.Children.Add(row);
                continue;
            }
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        return panel;
    }

    private static SelectableTextBlock Text(string markdown, double size, FontWeight weight)
    {
        var block = new SelectableTextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection() };
        var position = 0;
        foreach (Match token in InlineToken.Matches(markdown))
        {
            if (token.Index > position) block.Inlines.Add(new Run(markdown[position..token.Index]));
            if (token.Groups[1].Success || token.Groups[2].Success)
                block.Inlines.Add(new Run(token.Groups[1].Success ? token.Groups[1].Value : token.Groups[2].Value) { FontWeight = FontWeight.Bold });
            else if (token.Groups[3].Success)
                block.Inlines.Add(new Run(token.Groups[3].Value) { FontFamily = new FontFamily("Menlo, Consolas, monospace") });
            else
                block.Inlines.Add(new Run(token.Groups[4].Value));
            position = token.Index + token.Length;
        }
        if (position < markdown.Length) block.Inlines.Add(new Run(markdown[position..]));
        return block;
    }
}
