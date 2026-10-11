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
    public async Task OfferRequiresDownloadAndSeparateInstallConsent()
    {
        var model = new UpdateViewModel(Offer(), "0.2.6", _ => { });
        Assert.Equal("0.2.6", model.CurrentVersion);
        Assert.Equal("99.0.0", model.NewVersion);
        Assert.Equal("## 发行说明\n- 修复更新流程", model.ReleaseNotes);
        Assert.True(model.CanDownload);
        Assert.False(model.IsReady);
        Assert.False(model.HasProgress);
        Assert.False(await model.InstallAsync());
        await model.CloseAsync();
        Assert.False(model.CanDownload);
        Assert.False(await model.InstallAsync());
        Assert.Contains("未安装", model.StatusText);
        Assert.Contains("未提供发行说明", new UpdateViewModel(Offer(""), "0.2.6", _ => { }).ReleaseNotes);
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
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Text == Offer().ReleaseNotes);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible && Equals(button.Content, "确认安装并重启"));
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
