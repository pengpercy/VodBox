using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Desktop.Services;

namespace VodBox.Desktop.ViewModels;

/// <summary>详情：元信息 + 线路 + 选集 + 播放入口。</summary>
public sealed partial class DetailViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ObservableCollection<PlaybackLine> Lines { get; } = [];

    [ObservableProperty] private MediaDetail? _detail;
    [ObservableProperty] private PlaybackLine? _selectedLine;
    [ObservableProperty] private Episode? _selectedEpisode;
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private bool _isFavorite;
    private string _sourceKey = "";
    private string _sourceName = "";
    // 续播上下文：上次看的是哪条线路的哪一集、看到哪；只有选集对得上才复用位置
    private string _resumeLineId = "";
    private string _resumeEpisodeId = "";
    private long _resumePositionMs;

    public DetailViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    partial void OnSelectedLineChanged(PlaybackLine? value)
    {
        SelectedEpisode = value?.Episodes.FirstOrDefault();
    }

    /// <summary>从卡片进入详情。</summary>
    public async void Open(string sourceKey, MediaItem item)
    {
        _sourceKey = sourceKey;
        var source = _services.Registry.Get(sourceKey);
        if (source is null) return;
        _sourceName = source.Name;
        Loading = true;
        _main.Navigate(AppPage.Detail);
        try
        {
            Detail = await source.GetDetailAsync(item.Id);
            Lines.Clear();
            foreach (var line in Detail.Lines) Lines.Add(line);
            SelectedLine = Lines.FirstOrDefault();
            IsFavorite = await _services.Store.IsFavoriteAsync(sourceKey, item.Id);
            // 历史有记录时连线路/选集一起还原，否则续播位置会套到第一集上
            ApplyResume(await _services.Store.FindHistoryAsync(sourceKey, item.Id));
        }
        catch (Exception error)
        {
            _main.StatusMessage = $"详情加载失败：{error.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>从历史记录续播。</summary>
    public async void Resume(HistoryEntry entry)
    {
        var source = _services.Registry.Get(entry.SourceKey);
        if (source is null)
        {
            _main.StatusMessage = $"站点 {entry.SourceName} 在当前配置中不可用";
            return;
        }
        _sourceKey = entry.SourceKey;
        _sourceName = entry.SourceName;
        Loading = true;
        _main.Navigate(AppPage.Detail);
        try
        {
            Detail = await source.GetDetailAsync(entry.MediaId);
            Lines.Clear();
            foreach (var line in Detail.Lines) Lines.Add(line);
            ApplyResume(entry);
            IsFavorite = await _services.Store.IsFavoriteAsync(entry.SourceKey, entry.MediaId);
        }
        catch (Exception error)
        {
            _main.StatusMessage = $"详情加载失败：{error.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>还原续播上下文（线路 + 选集 + 位置），并记下集号供 <see cref="Play"/> 判断位置能否复用。</summary>
    private void ApplyResume(HistoryEntry? history)
    {
        _resumeLineId = history?.LineId ?? "";
        _resumeEpisodeId = history?.EpisodeId ?? "";
        _resumePositionMs = history?.PositionMs ?? 0;
        if (history is null || Detail is null) return;
        SelectedLine = Detail.FindLine(history.LineId);
        SelectedEpisode = Detail.FindEpisode(SelectedLine, history.EpisodeId);
    }

    [RelayCommand]
    private void Play()
    {
        if (Detail is null || SelectedLine is null || SelectedEpisode is null || SelectedEpisode.Uri is null)
        {
            _main.StatusMessage = "没有可播放的选集";
            return;
        }
        var resumeMs = MediaDetail.ResumePositionApplies(_resumeLineId, _resumeEpisodeId, SelectedLine.Id, SelectedEpisode.Id)
            ? _resumePositionMs
            : 0;
        _main.Player.Play(new PlaybackRequest
        {
            Uri = SelectedEpisode.Uri,
            Title = Detail.Item.Title,
            StartPositionMs = resumeMs,
            SourceKey = _sourceKey,
            SourceName = _sourceName,
            MediaId = Detail.Item.Id,
            LineId = SelectedLine.Id,
            EpisodeId = SelectedEpisode.Id,
            Poster = Detail.Item.Poster,
            Remarks = Detail.Item.Remarks,
        });
    }

    [RelayCommand]
    private async Task ToggleFavorite()
    {
        if (Detail is null) return;
        var entry = new FavoriteEntry
        {
            Kind = FavoriteKind.Vod,
            SourceKey = _sourceKey,
            SourceName = _sourceName,
            MediaId = Detail.Item.Id,
            Title = Detail.Item.Title,
            Poster = Detail.Item.Poster,
            Remarks = Detail.Item.Remarks,
        };
        IsFavorite = !IsFavorite;
        await _services.Store.SetFavoriteAsync(entry, IsFavorite);
    }
}
