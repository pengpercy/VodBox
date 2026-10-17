using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Explains the fixed config's orphan XPathGuard reminder; does not claim a missing Guard implementation is XPath.</summary>
public sealed class XPathGuardNoticeSource(SourceInfo site) : IInformationalContentSource
{
    public string Key=>site.Key;
    public string Name=>site.Name;
    public string Notice=>""+site.Name+"。此配置入口是提醒项；固定插件中未找到 XPathGuard 实现，也没有抓取规则，不提供视频内容。";
    internal static bool Supports(string? ext)=>string.IsNullOrWhiteSpace(ext)||ext=="null";
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<Category>>([]);}
    public Task<MediaPage> GetHomeAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],1,1));}
    public Task<MediaPage> GetItemsAsync(string categoryId,int page,IReadOnlyDictionary<string,string>? filters,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],1,1));}
    public Task<MediaPage> SearchAsync(string query,int page,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(new MediaPage([],1,1));}
    public Task<MediaDetail> GetDetailAsync(string mediaId,CancellationToken ct=default){ct.ThrowIfCancellationRequested();throw new NotSupportedException(Notice);}
}
