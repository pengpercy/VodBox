using CommunityToolkit.Mvvm.Input;
using VodBox.Core;
using VodBox.Infrastructure;

namespace VodBox.Desktop;

public partial class MainViewModel
{
    private readonly SemaphoreSlim _sourceGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _queryCancellation, _detailCancellation, _configCancellation, _aggregateCancellation;
    private Task _sourceTask = Task.CompletedTask;
    private bool _settingDetail, _disposed;

    public int ConfigurationLoadVersion { get; private set; }

    [RelayCommand] private Task LoadConfigAsync() => RunAsync(async () =>
    {
        _configCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _configCancellation = cancellation;
        try
        {
            string location = ConfigLocation;
            var config = await new ConfigLoader(_http).LoadAsync(location, cancellation.Token);
            await _configurations.SaveAsync(config, location, cancellation.Token);
            await ApplyConfigAsync(config, cancellation.Token);
            await RefreshSavedConfigurationsAsync();
            await NavigateAsync("首页");
            ConfigurationLoadVersion++;
            SchedulePreferencesSave();
        }
        finally { if (ReferenceEquals(_configCancellation, cancellation)) _configCancellation = null; }
    });
    private async Task RefreshSavedConfigurationsAsync()
    {
        SavedConfigurations.Clear(); foreach (var saved in await _configurations.ListAsync(_lifetime.Token)) SavedConfigurations.Add(saved);
        SelectedSavedConfiguration = SavedConfigurations.FirstOrDefault(x => x.Id == _config.Id);
    }
    [RelayCommand] private Task LoadSavedConfigurationAsync(SavedConfiguration? saved) => RunAsync(async () =>
    {
        if (saved is null) return;
        _configCancellation?.Cancel();
        ConfigLocation = saved.Location;
        await ApplyConfigAsync(await _configurations.LoadAsync(saved.Id, _lifetime.Token), _lifetime.Token);
        await NavigateAsync("首页");
        SchedulePreferencesSave();
    });
    [RelayCommand] private Task RemoveSavedConfigurationAsync(SavedConfiguration? saved) => RunAsync(async () =>
    {
        if (saved is null) return;
        await _configurations.RemoveAsync(saved.Id, _lifetime.Token); await RefreshSavedConfigurationsAsync();
    });
    private async Task ApplyConfigAsync(VodBoxConfig config, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _aggregateCancellation?.Cancel(); _playlist.Clear(); _playingChannel = null; await _coordinator.StopAsync();
        _config = config; _resolution.Configure(config); NotifyHomeConfiguration();
        Resolvers.Clear(); Resolvers.Add(new() { Id = "_direct", Name = "直接播放", Kind = ResolutionKind.Direct });
        Resolvers.Add(new() { Id = "_browser", Name = "网页嗅探", Kind = ResolutionKind.Browser });
        foreach (var resolver in config.Resolvers ?? []) Resolvers.Add(resolver); SelectedResolver = Resolvers[0]; SelectedSource = null; await _sourceTask;
        Sources.Clear(); foreach (var source in config.Sources) Sources.Add(source);
        SelectedSource = Sources.FirstOrDefault(x => x.Id == _preferredSourceId) ?? Sources.FirstOrDefault();
        await _sourceTask;
        _allChannels.Clear(); Channels.Clear(); LiveGroups.Clear(); LiveGroups.Add("全部"); Programmes.Clear();
        var failures = new List<string>();
        foreach (var live in config.LiveSources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var uri = new Uri(live.Uri);
                await using var stream = uri.IsFile ? File.OpenRead(uri.LocalPath) : await _http.GetStreamAsync(uri, token);
                var text = TextEncoding.Decode(await BoundedLiveAsync(stream, token));
                foreach (var channel in LiveParser.Parse(text))
                    _allChannels.Add(channel with { LiveSourceId = live.Id, Uris = channel.Uris.Select(x => new Uri(uri, x).AbsoluteUri).ToList() });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { failures.Add($"{live.Name}: {ex.Message}"); }
        }
        foreach (var group in _allChannels.Select(x => x.Group).Distinct()) LiveGroups.Add(group);
        SelectedLiveGroup = "全部"; await RefreshLiveFavoritesAsync();
        Status = $"已加载 {Sources.Count} 个内容源、{_allChannels.Count} 个直播频道。" + (failures.Count > 0 ? " 未加载：" + string.Join("；", failures) : "");
    }
    private static async Task<byte[]> BoundedLiveAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        { if (output.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("直播列表超过 8 MiB。"); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    partial void OnSelectedSourceChanged(SourceDefinition? value) => _sourceTask = RunAsync(() => SwitchSourceAsync(value));
    private async Task SwitchSourceAsync(SourceDefinition? source)
    {
        _browse?.Cancel(); _browse?.Dispose(); _queryCancellation?.Cancel(); _detailCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _browse = cancellation;
        await _sourceGate.WaitAsync(cancellation.Token);
        try
        {
            var previous = _provider; _provider = null;
            if (previous is not null) await previous.DisposeAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(source, SelectedSource)) return;
            SelectedCategory = null; SelectedItem = null; _detail = null; _nextCursor = null;
            SetHomeRecommendations([]); HomeRecommendationStatus = "请在设置中配置点播播放源。";
            Categories.Clear(); Items.Clear(); Lines.Clear(); Episodes.Clear(); Description = "选择内容查看详情和播放线路。";
            ConfigureLibraryFilters(source);
            if (source is null) return;
            var provider = _factory.Create(source); _provider = provider;
            var categories = await provider.GetCategoriesAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            await LoadHomeRecommendationsAsync(provider, cancellation.Token);
            Categories.Add(new("", "全部")); foreach (var category in categories) Categories.Add(category);
            SelectedCategory = Categories.First(); SchedulePreferencesSave();
        }
        finally { _sourceGate.Release(); }
    }
    private async Task SelectSourceAsync(SourceDefinition source)
    {
        SelectedSource = Sources.FirstOrDefault(x => x.Id == source.Id) ?? throw new InvalidDataException("该内容源已经被移除。");
        await _sourceTask;
        if (_provider is null || _provider.SourceId != source.Id) throw new InvalidOperationException("内容源未能初始化。");
    }
    private string? _activeSearchQuery;
    private int _queryEpoch;
    private IReadOnlyDictionary<string, string> _activeBrowseFilters = new Dictionary<string, string>();
    partial void OnSelectedCategoryChanged(Category? value) { if (value is not null && ShowLibrary && !ShowHome) _ = BrowseAsync(); }
    [RelayCommand] private Task BrowseAsync() => QueryAsync(false);
    [RelayCommand] private Task SearchAsync() => QueryAsync(true);
    private Task QueryAsync(bool search) => RunAsync(async () =>
    {
        var provider = _provider; if (provider is null) return;
        int epoch = ++_queryEpoch; _nextCursor = null;
        var filters = CurrentBrowseFilters();
        _queryCancellation?.Cancel(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_browse?.Token ?? _lifetime.Token);
        _queryCancellation = cancellation; AggregateResultsVisible = false;
        try
        {
            string query = SearchText;
            var page = search ? await provider.SearchPageAsync(query, null, cancellation.Token)
                : await provider.GetItemsFilteredAsync(string.IsNullOrEmpty(SelectedCategory?.Id) ? null : SelectedCategory.Id, null, filters, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (!ReferenceEquals(provider, _provider) || epoch != _queryEpoch) return;
            Items.Clear(); foreach (var item in page.Items) Items.Add(item);
            _activeBrowseFilters = filters; _activeSearchQuery = search ? query : null; _nextCursor = page.NextCursor;
        }
        finally { if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null; }
    });
    [RelayCommand] private Task LoadMoreAsync() => RunAsync(async () =>
    {
        var provider = _provider; string? cursor = _nextCursor; if (provider is null || cursor is null) return;
        int epoch = _queryEpoch; var token = _browse?.Token ?? _lifetime.Token;
        var page = _activeSearchQuery is { } query ? await provider.SearchPageAsync(query, cursor, token)
            : await provider.GetItemsFilteredAsync(string.IsNullOrEmpty(SelectedCategory?.Id) ? null : SelectedCategory.Id, cursor, _activeBrowseFilters, token);
        token.ThrowIfCancellationRequested(); if (!ReferenceEquals(provider, _provider) || cursor != _nextCursor || epoch != _queryEpoch) return;
        foreach (var item in page.Items) Items.Add(item); _nextCursor = page.NextCursor;
    });
    [RelayCommand] private Task SearchAllAsync() => RunAsync(async () =>
    {
        _aggregateCancellation?.Cancel(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _aggregateCancellation = cancellation;
        try
        {
            SearchResults.Clear(); SearchErrors.Clear(); AggregateResultsVisible = true; int completed = 0;
            await foreach (var batch in _aggregateSearch.SearchAsync(_config.Sources, SearchText, cancellation.Token))
            {
                foreach (var hit in batch.Hits) SearchResults.Add(hit);
                if (batch.Error is not null) SearchErrors.Add($"{batch.Source.Name}: {batch.Error}");
                Status = $"已搜索 {++completed}/{_config.Sources.Count} 个内容源，{SearchResults.Count} 条结果，{SearchErrors.Count} 个失败。";
            }
        }
        finally { if (ReferenceEquals(_aggregateCancellation, cancellation)) _aggregateCancellation = null; }
    });
    [RelayCommand] private void CancelSearch() => _aggregateCancellation?.Cancel();
    [RelayCommand] private Task OpenSearchResultAsync(SearchHit? hit) => RunAsync(async () =>
    {
        if (hit is null) return; _aggregateCancellation?.Cancel();
        await SelectSourceAsync(hit.Source); AggregateResultsVisible = false; await DetailAsync(hit.Item);
    });
    partial void OnSelectedItemChanged(MediaItem? value) { if (!_settingDetail && value is not null) { ShowPlaybackPage = true; _ = RunAsync(() => DetailAsync(value)); } }
    private async Task DetailAsync(MediaItem item)
    {
        var provider = _provider; if (provider is null) return;
        _detailCancellation?.Cancel(); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_browse?.Token ?? _lifetime.Token);
        _detailCancellation = cancellation;
        try
        {
            var detail = await provider.GetDetailAsync(item.Id, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (!ReferenceEquals(provider, _provider)) return;
            ShowPlaybackPage = true;
            _detail = detail; _settingDetail = true;
            try { SelectedItem = detail.Item; } finally { _settingDetail = false; }
            Description = detail.Description; Lines.Clear(); foreach (var line in detail.PlaybackLines) Lines.Add(line); SelectedLine = Lines.FirstOrDefault();
        }
        finally { if (ReferenceEquals(_detailCancellation, cancellation)) _detailCancellation = null; }
    }
    partial void OnSelectedLineChanged(PlaybackLine? value)
    { Episodes.Clear(); if (value is not null) foreach (var episode in value.Episodes) Episodes.Add(episode); }
    private async Task OpenStoredItemAsync(string configId, string sourceId, string mediaId)
    {
        await EnsureStoredConfigurationAsync(configId);
        var source = _config.Sources.FirstOrDefault(x => x.Id == sourceId) ?? throw new InvalidDataException("记录所属内容源已被移除。");
        await SelectSourceAsync(source); await DetailAsync(new(mediaId, ""));
        await NavigateAsync("发现"); ShowPlaybackPage = true; SchedulePreferencesSave();
    }
    private async Task EnsureStoredConfigurationAsync(string configId)
    {
        if (_config.Id != configId)
        {
            var saved = (await _configurations.ListAsync(_lifetime.Token)).FirstOrDefault(x => x.Id == configId)
                ?? throw new InvalidDataException("记录所属配置已被删除，请重新导入。");
            ConfigLocation = saved.Location; await ApplyConfigAsync(await _configurations.LoadAsync(configId, _lifetime.Token), _lifetime.Token);
        }
    }
    [RelayCommand] private Task OpenFavoriteAsync(FavoriteEntry? favorite) => RunAsync(async () =>
    {
        if (favorite is null) return;
        if (favorite.SourceId.StartsWith("live/", StringComparison.Ordinal)) await OpenStoredLiveAsync(favorite.ConfigId, favorite.SourceId[5..], favorite.MediaId, null);
        else await OpenStoredItemAsync(favorite.ConfigId, favorite.SourceId, favorite.MediaId);
    });
}
