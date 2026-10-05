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
    private bool _fullscreen;
    public void SetFullscreenPresentation(bool fullscreen)
    {
        _fullscreen = fullscreen;
        UpdatePlaybackLayout();
    }
    private void UpdatePlaybackLayout()
    {
        VideoFrame.CornerRadius = new(_fullscreen ? 0 : 10);
        PlaybackHeading.IsVisible = !_fullscreen;
        DetailRegion.IsVisible = !_fullscreen;
        EpisodeRegion.IsVisible = !_fullscreen;
        PlaybackLayout.RowDefinitions[0].Height = new(_fullscreen ? 1 : 2, GridUnitType.Star);
        PlaybackLayout.RowDefinitions[1].Height = _fullscreen ? new(0) : GridLength.Auto;
        PlaybackLayout.RowDefinitions[2].Height = _fullscreen ? new(0) : GridLength.Auto;
        PlaybackLayout.RowDefinitions[3].Height = _fullscreen ? new(0) : new(1, GridUnitType.Star);
        Grid.SetRowSpan(VideoFrame, _fullscreen ? 4 : 1);
        bool wide = !_fullscreen && Bounds.Width >= 900;
        PlaybackLayout.ColumnDefinitions[0].Width = new(wide ? 3 : 1, Avalonia.Controls.GridUnitType.Star);
        PlaybackLayout.ColumnDefinitions[1].Width = wide ? new(2, Avalonia.Controls.GridUnitType.Star) : new(0);
        PlaybackLayout.ColumnSpacing = wide ? 16 : 0;
        Grid.SetColumnSpan(VideoFrame, wide ? 1 : 2);
        Grid.SetRow(DetailRegion, wide ? 0 : 2);
        Grid.SetColumn(DetailRegion, wide ? 1 : 0);
        Grid.SetColumnSpan(DetailRegion, wide ? 1 : 2);
    }
    public EpisodeBrowserView EpisodeBrowser => EpisodesPanel;
    public Grid VideoContainer => VideoHost;
    public VlcVideoSurface VideoSurface => SurfaceControl;
    public DanmakuView DanmakuPreview => DanmakuControl;

}
