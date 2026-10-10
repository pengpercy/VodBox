using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>仅适配无 ext 规则的 csp_XPath 导航/提醒配置；不是通用 XPath 抓取或 XPathGuard。</summary>
public sealed class XPathNoticeSource(SourceInfo site) : IInformationalContentSource
{
    public string Key => site.Key;
    public string Name => site.Name;
    public string Notice => "此入口为导航／提醒，不提供点播内容：" + site.Name;
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<Category>>([]); }
    public Task<MediaPage> GetHomeAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], 1, 1)); }
    public Task<MediaPage> GetItemsAsync(string categoryId, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], 1, 1)); }
    public Task<MediaPage> SearchAsync(string query, int page, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaPage([], 1, 1)); }
    public Task<MediaDetail> GetDetailAsync(string mediaId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); throw new NotSupportedException("导航／提醒入口没有视频详情。"); }
}
