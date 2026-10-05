using Avalonia.Controls;

namespace VodBox.Desktop.Views;

public sealed partial class PlaybackView : UserControl
{
    public PlaybackView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdatePlaybackLayout();
        if (Design.IsDesignMode)
        {
            DataContext = Design.GetDataContext(this) as MainViewModel ?? new DesignMainViewModel();
            ConfigureDesignPreview((MainViewModel)DataContext);
            DetachedFromVisualTree += (_, _) => DanmakuPreview.Dispose();
        }
    }
    public void ConfigureDesignPreview(MainViewModel model)
    {
        VideoSurface.IsVisible = false;
        DanmakuPreview.IsVisible = true;
        DanmakuPreview.Attach(model, preview: true);
    }
    private void UpdatePlaybackLayout()
    {
        bool wide = Bounds.Width >= 900;
        PlaybackLayout.ColumnDefinitions[0].Width = new(wide ? 3 : 1, Avalonia.Controls.GridUnitType.Star);
        PlaybackLayout.ColumnDefinitions[1].Width = wide ? new(2, Avalonia.Controls.GridUnitType.Star) : new(0);
        PlaybackLayout.ColumnSpacing = wide ? 16 : 0;
        Grid.SetColumnSpan(VideoFrame, wide ? 1 : 2);
        Grid.SetRow(DetailSummary, wide ? 0 : 2);
        Grid.SetColumn(DetailSummary, wide ? 1 : 0);
        Grid.SetColumnSpan(DetailSummary, wide ? 1 : 2);
    }
    public EpisodeBrowserView EpisodeBrowser => EpisodesPanel;
    public Grid VideoContainer => VideoHost;
    public VlcVideoSurface VideoSurface => SurfaceControl;
    public DanmakuView DanmakuPreview => DanmakuControl;

}
