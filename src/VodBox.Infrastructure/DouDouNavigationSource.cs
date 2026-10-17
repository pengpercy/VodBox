using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>DouDou's Java implementation is a Douban discovery catalogue, with no detail or playback implementation.</summary>
public sealed class DouDouNavigationSource(SourceInfo site) : IInformationalContentSource
{
    public string Key=>site.Key;
    public string Name=>site.Name;
    public string Notice=>"豆豆是豆瓣热播与片单导航，不提供视频地址。请切换点播源，或在全局搜索中输入片名查找可播放资源。";
    internal static bool Supports(string? ext)
    {
        if(string.IsNullOrWhiteSpace(ext))return true;
        try {using var json=JsonDocument.Parse(FtyExtCodec.JsonConfig(ext));return json.RootElement.ValueKind==JsonValueKind.Object&&!json.RootElement.EnumerateObject().Any();}
        catch(Exception e) when(e is JsonException or InvalidDataException or FormatException){return false;}
    }
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>([]);}
    public Task<MediaPage> GetHomeAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],1,1));}
    public Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],Math.Max(1,page),Math.Max(1,page)));}
    public Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],Math.Max(1,page),Math.Max(1,page)));}
    public Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();throw new NotSupportedException(Notice);}
}
