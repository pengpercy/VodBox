using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VodBox.Desktop.Services;
using VodBox.Desktop.ViewModels;
using VodBox.Desktop.Views;
using Xunit;

namespace VodBox.PreviewTests;

public sealed class UpdateWindowTests
{
    private static UpdateOffer Offer(string notes = "## 发行说明\n- 修复更新流程") =>
        new("99.0.0", "VodBox_99.0.0.win-x64.zip", new Uri("https://github.com/pengpercy/VodBox/releases/download/v99.0.0/VodBox_99.0.0.win-x64.zip"),
            new string('a', 64), "https://github.com/pengpercy/VodBox/releases/tag/v99.0.0", notes, 1024);

    [AvaloniaFact]
    public async Task OfferShowsSkipAndClosingWithoutInstallOnQuitEndsTheSession()
    {
        var model = new UpdateViewModel(Offer(), "0.2.6", _ => { });
        Assert.Equal("0.2.6", model.CurrentVersion);
        Assert.Equal("99.0.0", model.NewVersion);
        Assert.Equal("## 发行说明\n- 修复更新流程", model.ReleaseNotes);
        Assert.True(model.CanSkip);
        Assert.True(model.IsOffer);
        Assert.False(model.IsReady);
        Assert.False(model.IsInProgress);
        Assert.False(model.InstallOnQuit);
        Assert.Equal("软件更新", model.WindowTitle);
        Assert.Contains("VodBox 99.0.0 可供下载，您现在的版本是 0.2.6。", model.OfferText);
        // 测试进程不是正式安装包：只能打开下载页面，不能原地安装。
        Assert.True(model.UsesExternalDownload);
        Assert.False(model.CanInstall);
        Assert.False(model.CanInstallNow);
        await model.InstallAsync();
        Assert.Equal(UpdateState.Available, model.State);
        Assert.False(await model.InstallNowAsync());
        await model.WindowClosedAsync();
        Assert.True(model.IsFinished);
        Assert.False(model.CanSkip);
        Assert.Contains("未安装", model.StatusText);
        Assert.Contains("未提供发行说明", new UpdateViewModel(Offer(""), "0.2.6", _ => { }).ReleaseNotes);
    }

    [AvaloniaFact]
    public async Task SkipRemembersTheVersionAndAutoInstallWritesThePreference()
    {
        var preferences = new MemoryUpdatePreferences();
        var model = new UpdateViewModel(Offer(), "0.2.6", _ => { }, preferences);
        model.AutoInstall = true;
        Assert.True(preferences.AutoInstallUpdates);
        await model.SkipAsync();
        Assert.Equal("99.0.0", preferences.SkippedUpdateVersion);
        Assert.True(model.IsFinished);
        Assert.Contains("已跳过版本 99.0.0", model.StatusText);
    }

    [AvaloniaFact]
    public void WindowOffersSkipRemindLaterAndInstall()
    {
        var window = new UpdateWindow(new UpdateViewModel(Offer(), "0.2.6", _ => { }));
        window.Show();
        try
        {
            window.UpdateLayout();
            var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button.Name is not null).ToDictionary(button => button.Name!, button => button);
            Assert.Equal("跳过这个版本", buttons["SkipVersion"].Content);
            Assert.Equal("稍后提示我", buttons["RemindLater"].Content);
            Assert.Equal("安装更新", buttons["Install"].Content);
            Assert.True(buttons["SkipVersion"].IsEffectivelyVisible);
            Assert.True(buttons["RemindLater"].IsEffectivelyVisible);
            // 进度面板在发现新版本阶段隐藏。
            Assert.False(buttons["InstallNow"].IsEffectivelyVisible);
            Assert.False(buttons["Cancel"].IsEffectivelyVisible);
            var autoInstall = window.GetVisualDescendants().OfType<CheckBox>().Single(box => box.Name == "AutoInstall");
            Assert.Equal("以后自动下载并安装更新", autoInstall.Content);
            // 开发/测试进程不能原地更新：安装按钮与自动安装勾选隐藏，改为打开下载页面。
            Assert.False(buttons["Install"].IsEffectivelyVisible);
            Assert.False(autoInstall.IsEffectivelyVisible);
            Assert.True(buttons["OpenDownloadPage"].IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReleaseNotesRenderHeadingsBulletsAndBold()
    {
        var notes = Assert.IsType<StackPanel>(UpdateWindow.RenderNotes("## Fixed\n\n- **崩溃修复.** 卡片网格\n- 第二项 [链接](https://example.com)"));
        var heading = Assert.IsType<SelectableTextBlock>(notes.Children[0]);
        Assert.Equal("Fixed", Assert.IsType<Avalonia.Controls.Documents.Run>(Assert.Single(heading.Inlines!)).Text);
        var first = Assert.IsType<Grid>(notes.Children[1]);
        Assert.Equal("•", Assert.IsType<TextBlock>(first.Children[0]).Text);
        var runs = Assert.IsType<SelectableTextBlock>(first.Children[1]).Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
        Assert.Equal(("崩溃修复.", Avalonia.Media.FontWeight.Bold), (runs[0].Text, runs[0].FontWeight));
        Assert.Equal(" 卡片网格", runs[1].Text);
        var second = Assert.IsType<SelectableTextBlock>(Assert.IsType<Grid>(notes.Children[2]).Children[1]);
        Assert.Equal("第二项 链接", string.Concat(second.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text)));
    }

    [AvaloniaFact]
    public async Task AutomaticOffersDeduplicateAndManualCheckReopens()
    {
        var service = new UpdateWindowService();
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        try
        {
            service.Show(Offer(), "0.2.6", owner, automatic: true, _ => { });
            Dispatcher.UIThread.RunJobs();
            var window = Assert.Single(owner.OwnedWindows.OfType<UpdateWindow>());
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Any(run => run.Text == "修复更新流程") == true);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && Equals(button.Content, "安装并重启应用"));
            service.Show(Offer(), "0.2.6", owner, automatic: false, _ => { });
            Assert.Same(window, Assert.Single(owner.OwnedWindows.OfType<UpdateWindow>()));
            await service.CloseAsync();
            Assert.Empty(owner.OwnedWindows.OfType<UpdateWindow>());
            service.Show(Offer(), "0.2.6", owner, automatic: true, _ => { });
            Assert.Empty(owner.OwnedWindows.OfType<UpdateWindow>());
            service.Show(Offer(), "0.2.6", owner, automatic: false, _ => { });
            Assert.Single(owner.OwnedWindows.OfType<UpdateWindow>());
        }
        finally { await service.CloseAsync(); owner.Close(); }
    }
}
