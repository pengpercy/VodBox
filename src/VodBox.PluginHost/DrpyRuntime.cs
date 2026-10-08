using System.Text.Json;

namespace VodBox.PluginHost;

/// <summary>
/// drpy2 运行时：装载 drpy2.min.js + 依赖库，注入宿主函数（req/log/getItem/setItem），
/// 把 TVBox 的 home/category/detail/search/play 调用翻译到 drpy2 的同名导出。
///
/// 模块名解析（drpy2 import 实测形态）：
/// - "assets://js/lib/X" → 包内 Assets/js/lib/X
/// - "./X" → 包内 Assets/js/X（drpy2 与依赖同目录）
/// - 站点脚本不入模块表：init(extUrl) 时 drpy2 自己 request() 拉取并 eval。
/// </summary>
public sealed class DrpyRuntime : IDrpyRuntime
{
    private readonly QuickJsEngine _engine;

    public DrpyRuntime(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _engine = new QuickJsEngine();
        try
        {
            InstallModules();
            InstallHost(ct);
            LoadDrpy2(ct);
        }
        catch { _engine.Dispose(); throw; }
    }

    // ---------- 模块与宿主注入 ----------

    private void InstallModules()
    {
        var root = FindAssetsRoot();
        if (root is null) throw new InvalidOperationException("找不到 drpy 依赖库目录（Assets/js）。");
        foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            // drpy2 的相对名（./X）与 assets 全名（assets://js/lib/X）都注册
            _engine.AddModule(rel, File.ReadAllText(file));
            _engine.AddModule($"assets://js/{rel}", File.ReadAllText(file));
        }
    }

    private static string? FindAssetsRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Assets", "js"),
            Path.Combine(baseDir, "..", "..", "..", "Assets", "js"), // dev 布局（bin/Debug/net10.0 → 项目根）
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "VodBox.PluginHost", "Assets", "js"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    private void InstallHost(CancellationToken ct)
    {
        // drpy2 期望的宿主全局：req(url, obj) → {content, headers}；log；getItem/setItem；
        // getProxy/joinUrl 少数站点用（先给最小实现）
        _engine.EvaluateAsync("""
            globalThis.log = (...a) => __hostapi.__host('log', a.map(x => typeof x === 'object' ? JSON.stringify(x) : String(x)).join(' '));
            globalThis.print = globalThis.log;
            globalThis.console = { log: globalThis.log, error: globalThis.log, warn: globalThis.log };
            // 站点存储（drpy setItem/getItem）：JS 侧对象即可，重启丢失符合 TVBox 语义
            globalThis.__storage = {};
            globalThis.getItem = (k, d) => (k in __storage) ? __storage[k] : (d ?? '');
            globalThis.setItem = (k, v) => { __storage[k] = String(v ?? ''); };
            globalThis.clearStorage = () => { __storage = {}; };
            globalThis.joinUrl = (base, rel) => new URL(rel, base).href;
            globalThis.getProxy = () => ({ proxy: '', reverse: '' });
            // req：drpy2 的 request() 是【同步契约】（返回 html 字符串）——
            // 走同步宿主桥（引擎线程阻塞等 HTTP，无锁环：HTTP 不回引擎命令队列）
            globalThis.req = (url, obj) => {
                const res = __hostapi.__host('req', JSON.stringify({ url: String(url), obj: obj || {} }));
                return JSON.parse(res);
            };
            """, ct).GetAwaiter().GetResult();
    }

    private void LoadDrpy2(CancellationToken ct)
    {
        var root = FindAssetsRoot() ?? throw new InvalidOperationException("Assets/js 缺失。");
        var path = Path.Combine(root, "drpy2.min.js");
        if (!File.Exists(path)) throw new FileNotFoundException("drpy2.min.js 缺失。", path);
        _engine.AddModule("drpy2", File.ReadAllText(path));
        // drpy2 模块顶层引用全局 pdfh/pdfa/pd（TVBox 宿主约定）。
        // 顺序：prelude 普通脚本（定义 pdfh 系）→ cheerio 模块 import 挂全局 → drpy2。
        _engine.EvaluateAsync(PreludeScript, ct).GetAwaiter().GetResult();
        var bootstrap = """
            import('assets://js/lib/cheerio.min.js')
              .then(m => { globalThis.cheerioLib = m.default && typeof m.default.load === 'function' ? m.default : m; })
              .then(() => import('drpy2'))
              .then(m => { globalThis.__drpyDefault = m.default; globalThis.__drpy = m.default || m; globalThis.__drpyReady = true; })
              .catch(e => { globalThis.__drpyError = String(e); });
            'booting'
            """;
        _engine.EvaluateAsync(bootstrap, ct).GetAwaiter().GetResult();
        var state = _engine.EvaluateAsync("globalThis.__drpyReady === true ? 'ok' : ('error:' + (globalThis.__drpyError || 'timeout'))", ct).GetAwaiter().GetResult();
        if (state != "ok") throw new InvalidOperationException("drpy2.min.js 装载失败：" + state);
    }

    /// <summary>
    /// drpy2 前置：pdfh/pdfa/pd 全局（TVBox JS 宿主约定签名）。
    /// pdfh(html, "selector&&attr")：&&分隔最后一段是属性（Text/src/href…）；
    /// pdfa(html, "selector") → 元素数组；pd(html, parse, uri) → joinUrl 兜底。
    /// 与 drpy2 的 parseTags.jsp 语义一致（pdfh2 在 defaultParser.pdfh 外再包一层）。
    /// </summary>
    internal const string PreludeScript = """
        // TVBox 宿主约定全局：pdfh/pdfa/pd（drpy2 模块顶层即引用）。
        // cheerio 延迟获取（bootstrap 先 import 挂 globalThis.cheerioLib）。
        function __pdfh_impl(html, parse) {
            if (!html || !parse) return '';
            const parts = String(parse).split('&&');
            const attr = parts.pop().trim();
            const sel = parts.join(' ');
            const cheerio = globalThis.cheerioLib;
            // hipy 语义：元素带 .cheerio（父 $ 上下文）—— $ 取 html.cheerio；
            // 元素自身不是函数，不能直接当 $ 用
            // cheerio selection 自带 .cheerio="[cheerio object]" 字符串标识（truthy 陷阱）——
            // 只有真正的 $ 上下文（function）才复用；selection/字符串都走重新 load
            let $ = (typeof html === 'object' && typeof html.cheerio === 'function') ? html.cheerio : cheerio.load(String(html));
            let node = sel ? $(sel).first() : $.root();
            if (node.length === 0) return '';
            if (!attr || attr === 'Text' || attr === 'text') return node.text().trim();
            if (attr === 'Html' || attr === 'html') return node.html() || '';
            const val = node.attr(attr);
            return val !== undefined ? val : '';
        }
        function __pdfa_impl(html, parse) {
            if (!html || !parse) return [];
            const parts = String(parse).split('&&');
            const sel = parts.join(' ').trim();
            if (!sel) return [];
            const cheerio = globalThis.cheerioLib;
            // cheerio selection 自带 .cheerio="[cheerio object]" 字符串标识（truthy 陷阱）——
            // 只有真正的 $ 上下文（function）才复用；selection/字符串都走重新 load
            let $ = (typeof html === 'object' && typeof html.cheerio === 'function') ? html.cheerio : cheerio.load(String(html));
            let out = [];
            $(sel).each((i, el) => { out.push($(el)); });
            return out;
        }
        function __pd_impl(html, parse, uri) {
            let ret = __pdfh_impl(html, parse);
            if (ret && uri && ret.indexOf('http') !== 0) {
                try { ret = new URL(ret, uri).href; } catch (e) { }
            }
            return ret;
        }
        globalThis.pdfh = __pdfh_impl;
        globalThis.pdfa = __pdfa_impl;
        globalThis.pd = __pd_impl;
        // TVBox 宿主 local 存储（drpy2 setItem/getItem 包装它）
        globalThis.local = {
            set: (k, key, v) => { globalThis['__ls_' + k + '_' + key] = String(v); return true; },
            get: (k, key) => { const v = globalThis['__ls_' + k + '_' + key]; return v === undefined ? null : v; },
            delete: (k, key) => { delete globalThis['__ls_' + k + '_' + key]; return true; },
        };
        // URL 兜底（QuickJS 无 Node url 模块；drpy2 的 getCategory 用 new URL）
        if (typeof globalThis.URL !== 'function' && typeof globalThis.__URLShim !== 'function') {
            globalThis.__URLShim = class URL {
                constructor(input, base) {
                    // 简化 URL 解析：够 drpy 用（protocol/host/pathname/search）
                    const abs = base ? __joinUrlAbs(String(base), String(input)) : String(input);
                    const m = /^([a-z]+:)\/\/([^\/?#]*)([^?#]*)(\?[^#]*)?/.exec(abs) || ['', '', '', abs, ''];
                    this.protocol = m[1]; this.host = m[2]; this.pathname = m[3] || '/';
                    this.search = m[4] || ''; this.href = abs;
                    this.origin = this.protocol + '//' + this.host;
                }
                toString() { return this.href }
            };
            globalThis.URL = globalThis.__URLShim;
        }
        function __joinUrlAbs(base, rel) {
            return __hostapi.__host('joinUrl', JSON.stringify({base:String(base), rel:String(rel)}));
        }
        'prelude-ok'
        """;

    // ---------- drpy API ----------

    public async Task InitAsync(string ext, CancellationToken ct = default)
    {
        // JSON rule 是对象，不是 eval 的字符串；URL/内联 JS 均以字符串传入。
        var arg = ext.TrimStart().StartsWith('{') ? JsonDocument.Parse(ext) : null;
        using (arg)
            await _engine.EvaluateAsync("__drpy.init(" + (arg is null ? J(ext) : "JSON.parse(" + J(arg.RootElement.GetRawText()) + ")") + ")", ct).ConfigureAwait(false);
    }

    public Task<string> HomeAsync(CancellationToken ct = default) => _engine.EvaluateAsync("__drpy.home(true)", ct);
    public Task<string> HomeVodAsync(CancellationToken ct = default) => _engine.EvaluateAsync("__drpy.homeVod({})", ct);
    public Task<string> CategoryAsync(string tid, int page, IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default) =>
        _engine.EvaluateAsync($"__drpy.category({J(tid)}, {page.ToString(System.Globalization.CultureInfo.InvariantCulture)}, true, JSON.parse({J(ObjectJson(filters))}))", ct);
    public Task<string> DetailAsync(string mediaId, CancellationToken ct = default) => _engine.EvaluateAsync($"__drpy.detail({J(mediaId)})", ct);
    public Task<string> SearchAsync(string query, int page, CancellationToken ct = default) =>
        _engine.EvaluateAsync($"__drpy.search({J(query)}, false, {page.ToString(System.Globalization.CultureInfo.InvariantCulture)})", ct);
    public Task<string> PlayAsync(string flag, string id, IReadOnlyList<string> flags, CancellationToken ct = default) =>
        _engine.EvaluateAsync($"__drpy.play({J(flag)}, {J(id)}, {ArrayJson(flags)})", ct);

    private static string J(string value) => WriteJson(w => w.WriteStringValue(value));
    private static string ObjectJson(IReadOnlyDictionary<string, string>? values) => WriteJson(w =>
    {
        w.WriteStartObject();
        if (values is not null) foreach (var pair in values) w.WriteString(pair.Key, pair.Value);
        w.WriteEndObject();
    });
    private static string ArrayJson(IReadOnlyList<string> values) => WriteJson(w =>
    {
        w.WriteStartArray();
        foreach (var value in values) w.WriteStringValue(value);
        w.WriteEndArray();
    });
    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose() => _engine.Dispose();
}
