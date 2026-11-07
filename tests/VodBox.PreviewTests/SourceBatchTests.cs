using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Core;
using VodBox.Infrastructure;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views.Settings;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>设置 › 采集源：模糊搜索、批量启用/禁用/删除、按勾选或筛选范围检测连通性。</summary>
public sealed class SourceBatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vodbox-source-batch-{Guid.NewGuid():N}");
    private readonly AppServices _services;
    private readonly SettingsViewModel _settings;

    public SourceBatchTests()
    {
        _services = new AppServices(_directory);
        var main = new MainViewModel(_services, designTime: true);
        main.AttachDispatcher(Dispatcher.UIThread);
        _settings = main.Settings;
        _services.SourceConfig.ApplyConfigFile("""
            {"api_site":{
              "lzzy":{"name":"量子资源","api":"https://cj.lziapi.com/api.php/provide/vod"},
              "ffzy":{"name":"非凡影视","api":"https://cj.ffzyapi.com/api.php/provide/vod"},
              "bfzy":{"name":"暴风资源","api":"https://bfzyapi.com/api.php/provide/vod"},
              "dead":{"name":"失效站","api":"http://127.0.0.1:1/api.php/provide/vod"}
            }}
            """);
        _settings.RefreshSites();
    }

    public void Dispose()
    {
        Dispatcher.UIThread.RunJobs();
        _services.Dispose();
        TempDataDirectory.TryDelete(_directory);
    }

    private string[] Visible() => _settings.FilteredSites.Select(row => row.Key).ToArray();

    [AvaloniaTheory]
    [InlineData("量子", "lzzy")]
    [InlineData("lz", "lzzy")]        // 键的字符顺序匹配
    [InlineData("量资", "lzzy")]       // 名称的字符顺序匹配
    [InlineData("ffzyapi", "ffzy")]   // 地址包含
    [InlineData("资源 暴风", "bfzy")]   // 多词都要命中
    public void FuzzyFilterMatchesNameKeyOrApi(string query, string expected)
    {
        _settings.SiteFilter = query;
        Assert.Equal([expected], Visible());
        Assert.True(_settings.IsSiteFilterActive);
        Assert.Equal("匹配 1 / 4", _settings.SiteFilterSummary);
    }

    [AvaloniaFact]
    public void FilterIgnoresSubsequenceAcrossLongUrls()
    {
        // 「apivod」在每个 URL 里都按顺序出现，但不应匹配所有源。
        _settings.SiteFilter = "apivod";
        Assert.Empty(Visible());
        _settings.ClearSiteFilterCommand.Execute(null);
        Assert.Equal(4, _settings.FilteredSites.Count);
    }

    [AvaloniaFact]
    public void SelectAllOnlyTouchesFilteredRowsAndBatchDisables()
    {
        _settings.SiteFilter = "资源";
        Assert.Equal(["lzzy", "bfzy"], Visible());
        _settings.AllVisibleSelected = true;
        Assert.Equal(2, _settings.SelectedSiteCount);
        Assert.True(_settings.AllVisibleSelected);

        _settings.DisableSelectedSitesCommand.Execute(null);
        Assert.True(_services.SourceConfig.Find("lzzy")!.Disabled);
        Assert.True(_services.SourceConfig.Find("bfzy")!.Disabled);
        Assert.False(_services.SourceConfig.Find("ffzy")!.Disabled);
        // 刷新后勾选保留，可以接着批量启用。
        Assert.Equal(2, _settings.SelectedSiteCount);
        _settings.EnableSelectedSitesCommand.Execute(null);
        Assert.All(_services.SourceConfig.Sources, site => Assert.False(site.Disabled));

        _settings.FilteredSites[0].IsSelected = false;
        Assert.Null(_settings.AllVisibleSelected); // 部分选中
        _settings.AllVisibleSelected = true;
        _settings.AllVisibleSelected = false;
        Assert.Equal(0, _settings.SelectedSiteCount);
    }

    [AvaloniaFact]
    public void StatusFilterShowsDisabledOnly()
    {
        _services.SourceConfig.SetSourceDisabled("ffzy", true);
        _settings.SiteStatusFilterIndex = 2;
        Assert.Equal(["ffzy"], Visible());
        _settings.SiteStatusFilterIndex = 1;
        Assert.DoesNotContain("ffzy", Visible());
    }

    [AvaloniaFact]
    public void BatchDeleteNeedsConfirmationAndCanBeRestored()
    {
        _settings.Sites.First(row => row.Key == "dead").IsSelected = true;
        _settings.Sites.First(row => row.Key == "bfzy").IsSelected = true;
        _settings.DeleteSelectedSitesCommand.Execute(null);
        Assert.True(_settings.DeleteDialogOpen);
        Assert.Equal("确定删除选中的 2 个采集源吗？", _settings.DeleteDialogMessage);
        Assert.Contains("暴风资源", _settings.DeleteDialogNames);
        Assert.Contains("恢复已删除", _settings.DeleteDialogNote);
        Assert.Equal(4, _services.SourceConfig.Sources.Count); // 确认前不删

        _settings.ConfirmDeleteSitesCommand.Execute(null);
        Assert.False(_settings.DeleteDialogOpen);
        Assert.Equal(["lzzy", "ffzy"], _services.SourceConfig.Sources.Select(site => site.Key));
        Assert.True(_settings.HasDeletedSources);
        Assert.Equal(0, _settings.SelectedSiteCount);

        _settings.RestoreDeletedSitesCommand.Execute(null);
        Assert.Equal(4, _services.SourceConfig.Sources.Count);
        Assert.False(_settings.HasDeletedSources);
    }

    [AvaloniaFact]
    public void RowDeleteAlsoAsksAndCancelKeepsSource()
    {
        var row = _settings.Sites.First(site => site.Key == "ffzy");
        _settings.DeleteSiteCommand.Execute(row);
        Assert.True(_settings.DeleteDialogOpen);
        Assert.Equal("确定删除采集源「非凡影视」吗？", _settings.DeleteDialogMessage);
        _settings.CancelDeleteSitesCommand.Execute(null);
        Assert.False(_settings.DeleteDialogOpen);
        Assert.NotNull(_services.SourceConfig.Find("ffzy"));
        // 取消后再触发确认不会误删上一次的目标。
        _settings.ConfirmDeleteSitesCommand.Execute(null);
        Assert.Equal(4, _services.SourceConfig.Sources.Count);
    }

    [AvaloniaFact]
    public void SettingsWindowShowsDeleteDialogAndEscCancels()
    {
        var window = new VodBox.Desktop.Views.SettingsWindow(_settings) { Width = 1000, Height = 720 };
        window.Show();
        try
        {
            void Pump() { for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
            T Named<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
            Pump();
            Assert.False(Named<Panel>("DeleteSitesDialog").IsVisible);
            _settings.DeleteSiteCommand.Execute(_settings.Sites.First(site => site.Key == "dead"));
            Pump();
            Assert.True(Named<Panel>("DeleteSitesDialog").IsEffectivelyVisible);

            window.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            Pump();
            Assert.False(_settings.DeleteDialogOpen);
            Assert.True(window.IsVisible); // Esc 只关弹窗，不关设置窗口
            Assert.NotNull(_services.SourceConfig.Find("dead"));

            _settings.DeleteSiteCommand.Execute(_settings.Sites.First(site => site.Key == "dead"));
            Pump();
            var confirm = Named<Button>("ConfirmDeleteSitesButton");
            confirm.Command!.Execute(confirm.CommandParameter);
            Pump();
            Assert.False(Named<Panel>("DeleteSitesDialog").IsVisible);
            Assert.Null(_services.SourceConfig.Find("dead"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ValidationOnlyChecksSelectedSources()
    {
        var dead = _settings.Sites.First(row => row.Key == "dead");
        dead.IsSelected = true;
        Assert.Equal("检测已选 1 个", _settings.ValidateButtonLabel);
        await _settings.ValidateSitesCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SourceValidity.Invalid, dead.Status);
        Assert.All(_settings.Sites.Where(row => row.Key != "dead"), row => Assert.Null(row.Status));
        Assert.StartsWith("1 / 1", _settings.ValidationSummary);

        // 检测后按状态筛出无效的，全选后批量禁用。
        dead.IsSelected = false;
        _settings.SiteStatusFilterIndex = 5;
        Assert.Equal(["dead"], Visible());
        Assert.Equal("检测匹配的 1 个", _settings.ValidateButtonLabel);
    }

    [AvaloniaFact]
    public void ViewBindsFilterAndBatchControls()
    {
        var view = new SourcesSettingsView { DataContext = _settings };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        window.Show();
        try
        {
            void Pump() { for (var i = 0; i < 3; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); } }
            Pump();
            T Named<T>(string name) where T : Control => view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
            Named<TextBox>("SiteFilterBox").Text = "非凡";
            Pump();
            var rows = Named<ItemsControl>("SiteRows");
            Assert.Equal(1, rows.ItemCount);
            Named<CheckBox>("SelectAllSites").IsChecked = true;
            Pump();
            Assert.True(Assert.Single(_settings.Sites, row => row.IsSelected).Key == "ffzy");
            var rowBox = rows.GetVisualDescendants().OfType<CheckBox>().Single();
            Assert.True(rowBox.IsChecked);
        }
        finally { window.Close(); }
    }
}
