using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed record Catalog(IReadOnlyList<Category> Categories, IReadOnlyList<CatalogItem> Items);
public sealed record CatalogItem(string Id, string Title, string Description, string CategoryId,
    IReadOnlyList<CatalogEpisode> Episodes, string? Poster = null, string? Remarks = null);
public sealed record CatalogEpisode(string Id, string Title, string Uri);

/// <summary>A C# provider for the new VodBox catalog schema, over HTTP or local files.</summary>
public sealed class CatalogProvider : IContentProvider
{
    private readonly HttpClient _http;
    private readonly SourceDefinition _source;
    private readonly SemaphoreSlim _load = new(1, 1);
    private Catalog? _catalog;
    private bool _disposed;
    public string SourceId => _source.Id;
    public CatalogProvider(SourceDefinition source, HttpClient http) { _source = source; _http = http; }
    private async Task<Catalog> GetCatalogAsync(CancellationToken token)
    {
        await _load.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_catalog is not null) return _catalog;
            if (_source.Entry is null) throw new InvalidDataException("catalog 源缺少 entry。");
            var uri = new Uri(_source.Entry);
            var json = uri.IsFile ? await File.ReadAllTextAsync(uri.LocalPath, token) : await _http.GetStringAsync(uri, token);
            var catalog = JsonSerializer.Deserialize(json, CatalogJson.Default.Catalog) ?? throw new InvalidDataException("目录为空。");
            if (catalog.Items.Select(x => x.Id).Distinct().Count() != catalog.Items.Count)
                throw new InvalidDataException("目录媒体 id 重复。");
            _catalog = catalog;
            return catalog;
        }
        finally { _load.Release(); }
    }
    private static MediaItem Card(CatalogItem item) => new(item.Id, item.Title, item.Poster, item.Remarks);
    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken token) => (await GetCatalogAsync(token)).Categories;
    public async Task<MediaPage> GetItemsAsync(string? categoryId, string? cursor, CancellationToken token)
    {
        var catalog = await GetCatalogAsync(token);
        int offset = cursor is null ? 0 : int.Parse(cursor);
        if (offset < 0) throw new InvalidDataException("分页游标不能为负。");
        var items = catalog.Items.Where(x => categoryId is null || x.CategoryId == categoryId).ToList();
        return new(items.Skip(offset).Take(30).Select(Card).ToList(), offset + 30 < items.Count ? (offset + 30).ToString() : null);
    }
    public async Task<MediaPage> SearchAsync(string query, CancellationToken token) => new((await GetCatalogAsync(token)).Items
        .Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(Card).ToList());
    public async Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken token)
    {
        var item = (await GetCatalogAsync(token)).Items.First(x => x.Id == mediaId);
        return new(Card(item), item.Description, [new("main", "主线路", item.Episodes.Select(x => new Episode(x.Id, x.Title)).ToList())]);
    }
    public async Task<PlaybackRequest> ResolvePlaybackAsync(string mediaId, string episodeId, CancellationToken token)
    {
        var item = (await GetCatalogAsync(token)).Items.First(x => x.Id == mediaId);
        var episode = item.Episodes.First(x => x.Id == episodeId);
        return new() { Uri = new Uri(new Uri(_source.Entry!), episode.Uri).ToString(), Title = $"{item.Title} · {episode.Title}", SourceId = SourceId, MediaId = mediaId, EpisodeId = episodeId };
    }
    public async ValueTask DisposeAsync()
    {
        await _load.WaitAsync();
        try { _disposed = true; _catalog = null; }
        finally { _load.Release(); }
    }
}

public sealed class ProviderFactory(HttpClient http, string pluginHostPath, string assetsDirectory) : IProviderFactory
{
    public IContentProvider Create(SourceDefinition source) => source.Runtime switch
    {
        ProviderRuntime.Csharp when source.Provider == "catalog" => new CatalogProvider(source, http),
        ProviderRuntime.Csharp when source.Provider == "maccms-json" => new MacCmsProvider(source, http, false),
        ProviderRuntime.Csharp when source.Provider == "maccms-xml" => new MacCmsProvider(source, http, true),
        ProviderRuntime.Csharp => throw new NotSupportedException($"尚未注册 C# Provider：{source.Provider}"),
        _ => new ScriptProvider(source, pluginHostPath, assetsDirectory)
    };
}
