using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using VodBox.Desktop.Views.Settings;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>
/// 设置分区迁移的整合验收：每个分区在真窗口里渲染，并验证 $parent 绑定真的解析到命令。
/// 这类绑定编译期不报错、运行期静默失效，必须实际渲染并执行命令才能确认。
/// </summary>
public sealed class SettingsSectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-sections-{Guid.NewGuid():N}");
    private readonly AppServices _services;
    private readonly SettingsViewModel _settings;

    public SettingsSectionTests()
    {
        _services = new AppServices(_directory);
        var main = new MainViewModel(_services);
        main.AttachDispatcher(Dispatcher.UIThread);
        _settings = main.Settings;
    }

    public void Dispose()
    {
        // 先释放数据库连接，再删目录：Windows 不允许删除仍被占用的文件。
        _services.Dispose();
        TempDataDirectory.TryDelete(_directory);
    }

    /// <summary>注入一个可见站点；空站点列表会让站点行绑定测不出东西。</summary>
    private void SeedSite()
    {
        _settings.Sites.Clear();
        _settings.Sites.Add(new SourceInfo { Key = "seed", Name = "测试站点", Runtime = SourceRuntime.MacCms, Api = "csp_Seed" });
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
        window.UpdateLayout();
    }

    /// <summary>把分区控件放进真窗口，DataContext 指向 SettingsViewModel。</summary>
    private static (Window Window, TView View) Host<TView>(SettingsViewModel settings, Action<TView>? configure = null)
        where TView : Control, new()
    {
        var view = new TView { DataContext = settings };
        configure?.Invoke(view);
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        Pump(window);
        return (window, view);
    }

    [AvaloniaFact]
    public void SourcesSectionBindsSiteCommandsThroughParentWithoutSilentFailure()
    {
        // 站点行按钮是全项目最容易静默失效的绑定；先确保有站点数据，否则测试无意义。
        SeedSite();
        var site = _settings.Sites.FirstOrDefault();
        Assert.NotNull(site);

        var (window, view) = Host<SourcesSettingsView>(_settings);
        try
        {
            var buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Command is not null && b.CommandParameter is SourceInfo).ToArray();
            Assert.True(buttons.Length >= 5, $"站点行应至少 5 个带命令的按钮，实际 {buttons.Length}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SiteRowCommandsReallyExecuteInsteadOfDroppingSilently()
    {
        SeedSite();
        var site = _settings.Sites.FirstOrDefault();
        Assert.NotNull(site);
        var before = _services.Registry.IsHidden(site!.Key);

        var (window, view) = Host<SourcesSettingsView>(_settings);
        try
        {
            // $parent 绑定若失效，命令会是 null 或点了没反应；这里真的执行一次并观察状态。
            var button = view.GetVisualDescendants().OfType<Button>().First(b =>
                ReferenceEquals(b.CommandParameter, site) && b.Content?.ToString() == "显示/隐藏");
            Assert.NotNull(button.Command);
            button.Command!.Execute(button.CommandParameter);
            Pump(window);
            Assert.NotEqual(before, _services.Registry.IsHidden(site.Key));
            _services.Registry.SetHidden(site.Key, before);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PlaybackDanmakuSubtitleSectionsSwitchOnSectionProperty()
    {
        var (window, view) = Host<PlaybackSettingsView>(_settings,
            v => v.Section = PlaybackSettingsView.PlaybackSettingsViewSection.Playback);
        try
        {
            // 用各区独有文案判断，而不是「有没有滑杆」——弹幕区也有滑杆。
            string[] VisibleTitles() => view.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("rowTitle") && t.IsEffectivelyVisible)
                .Select(t => t.Text ?? "").ToArray();

            Assert.True(view.IsPlayback);
            Assert.False(view.IsDanmaku);
            Assert.False(view.IsSubtitles);
            Assert.Contains("音量", VisibleTitles());
            Assert.DoesNotContain("透明度", VisibleTitles());

            view.Section = PlaybackSettingsView.PlaybackSettingsViewSection.Danmaku;
            Pump(window);
            Assert.False(view.IsPlayback);
            Assert.True(view.IsDanmaku);
            Assert.Contains("透明度", VisibleTitles());
            Assert.DoesNotContain("音量", VisibleTitles());

            view.Section = PlaybackSettingsView.PlaybackSettingsViewSection.Subtitles;
            Pump(window);
            Assert.True(view.IsSubtitles);
            Assert.Contains("ASSRT 服务凭据", VisibleTitles());
            Assert.DoesNotContain("透明度", VisibleTitles());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SubtitleListRowCommandResolvesThroughParent()
    {
        // 字幕列表的行内按钮是清单点名的静默失效高风险点。
        _settings.OnlineSubtitles.Add(new OnlineSubtitle("id-1", "测试字幕"));
        var (window, view) = Host<PlaybackSettingsView>(_settings,
            v => v.Section = PlaybackSettingsView.PlaybackSettingsViewSection.Subtitles);
        try
        {
            var button = view.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Content?.ToString() == "测试字幕");
            Assert.NotNull(button.Command); // 解析失败时这里是 null
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InterfaceDataSectionSwitchesAndKeepsOptionOrder()
    {
        var (window, view) = Host<InterfaceDataSettingsView>(_settings,
            v => v.Section = InterfaceDataSettingsView.InterfaceDataSettingsViewSection.Interface);
        try
        {
            Assert.True(view.IsInterface);
            Assert.False(view.IsData);
            var combos = view.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible).ToArray();
            Assert.Equal(2, combos.Length);
            // 选项顺序不能变：索引语义与偏好存储绑定。
            Assert.Equal(new[] { "跟随系统", "浅色", "深色" },
                combos[0].Items.OfType<ComboBoxItem>().Select(i => i.Content?.ToString()).ToArray());
            Assert.Equal(new[] { "宽松", "标准", "紧凑" },
                combos[1].Items.OfType<ComboBoxItem>().Select(i => i.Content?.ToString()).ToArray());
            Assert.Contains("清除壁纸", view.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()));

            view.Section = InterfaceDataSettingsView.InterfaceDataSettingsViewSection.Data;
            Pump(window);
            Assert.True(view.IsData);
            Assert.False(view.IsInterface);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible && b.Content?.ToString() == "清除壁纸");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RemoteDiagnosticsSectionKeepsQrCodeAndLogPreview()
    {
        var (window, view) = Host<RemoteDiagnosticsSettingsView>(_settings,
            v => v.Section = RemoteDiagnosticsSettingsView.RemoteDiagnosticsSettingsViewSection.Remote);
        try
        {
            Assert.True(view.IsRemote);
            Assert.False(view.IsDiagnostics);
            Assert.NotNull(view.GetVisualDescendants().OfType<Image>().FirstOrDefault(i => i.Width == 220));

            view.Section = RemoteDiagnosticsSettingsView.RemoteDiagnosticsSettingsViewSection.Diagnostics;
            Pump(window);
            Assert.True(view.IsDiagnostics);
            // 日志预览：只读、等宽、保留最小高度。
            var preview = view.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.IsReadOnly && t.MinHeight >= 200);
            Assert.NotNull(preview);
            var commands = view.GetVisualDescendants().OfType<Button>()
                .Count(b => b.Command is not null && b.IsEffectivelyVisible);
            Assert.True(commands >= 4, $"诊断区应至少 4 个命令按钮，实际 {commands}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AboutSectionWiresUpdateButtonsAndVersion()
    {
        var (window, view) = Host<AboutSettingsView>(_settings);
        try
        {
            var labels = view.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToArray();
            Assert.Contains("检查更新", labels);
            Assert.Contains("获取更新", labels);
            Assert.Contains("取消下载", labels);
            Assert.NotNull(view.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Text?.StartsWith("VodBox ", StringComparison.Ordinal) == true));
            Assert.True(view.GetVisualDescendants().OfType<CheckBox>().First().IsChecked);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EverySectionRendersRealContentInsideTheSettingsWindow()
    {
        // 整合验收：窗口接入真实 SettingsViewModel 后，逐个分区都要渲染出可见内容。
        var window = new SettingsWindow { Width = 940, Height = 680 };
        window.Show();
        try
        {
            Pump(window);
            var vm = (SettingsWindowViewModel)window.DataContext!;
            vm.Content = _settings;
            var items = new[]
            {
                vm.Sources, vm.Playback, vm.Danmaku, vm.Subtitles, vm.Interface,
                vm.Data, vm.Remote, vm.Diagnostics, vm.About,
            };
            foreach (var item in items)
            {
                item.SelectCommand.Execute(null);
                Pump(window);
                Assert.True(item.IsSelected);
                Assert.Equal(item.Title, vm.CurrentTitle);
                var sections = window.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.GetType().Namespace == "VodBox.Desktop.Views.Settings" && c.IsEffectivelyVisible)
                    .ToArray();
                Assert.True(sections.Length >= 1, $"分区「{item.Title}」没有渲染出任何内容");
                Assert.Contains(sections, c => c.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)));
            }
        }
        finally { window.Close(); }
    }
}
