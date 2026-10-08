using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Xunit;

namespace VodBox.PreviewTests;

/// <summary>临时诊断：验证无头渲染管线的最小可捕获性（定位 CaptureRenderedFrame 为 null 的原因）。</summary>
public sealed class CaptureDiagnostics
{
    [AvaloniaFact]
    public void 空窗口可捕获渲染帧()
    {
        var window = new Window { Width = 300, Height = 200, Content = new Button { Content = "hi" } };
        window.Show();
        window.UpdateLayout();
        Bitmap? frame = null;
        for (var i = 0; i < 10 && frame is null; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
            frame = window.CaptureRenderedFrame();
        }
        Assert.NotNull(frame);
    }
}
