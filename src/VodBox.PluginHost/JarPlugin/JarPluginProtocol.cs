using System.Text.Json;
using System.Text.Json.Serialization;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>
/// 外部 JAR 插件宿主的 JSON IPC 协议常量与数据契约。
///
/// 这是**独立实现**的协议契约（仅依据可观测的进程交互事实，不复制任何上游源码）：
/// 宿主以 <c>--serve &lt;request.json&gt;</c> 启动，就绪后写 <c>&lt;cache&gt;/ready.json</c>；
/// 之后宿主从 stdin 逐行读取命令 <c>{"id","params"}</c>，把每一条响应**原子写**到
/// <c>&lt;cache&gt;/responses/&lt;id&gt;.json</c>（信封见 <see cref="JarPluginEnvelope"/>）。
/// 把响应写成文件而不是走 stdout，是为了让插件自身向 stdout 的打印不会污染协议通道。
/// </summary>
public static class JarPluginProtocol
{
    /// <summary>宿主进程启动参数：指向 request.json 的路径。</summary>
    public const string ServeFlag = "--serve";

    /// <summary>独立转换子命令：{@code Host --convert <request.json>}，把 DEX 归档转为 JVM JAR。</summary>
    public const string ConvertFlag = "--convert";

    /// <summary>就绪/启动失败标记文件（相对 cache 目录）。</summary>
    public const string ReadyFile = "ready.json";

    /// <summary>命令响应目录（相对 cache 目录）。</summary>
    public const string ResponsesDirectory = "responses";

    /// <summary>请求编号必须在双方一致地限制字符集，避免它被拼进文件路径。</summary>
    public static bool IsValidIdentifier(string id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 80 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    /// <summary>params 里的动作参数名。存在性决定动作，与宿主分支一一对应。</summary>
    public static class Parameter
    {
        public const string Ids = "ids";       // detailContent
        public const string Key = "wd";        // searchContent
        public const string Category = "t";    // categoryContent
        public const string Play = "play";     // playerContent
        public const string Flag = "flag";     // playerContent 的线路标识
        public const string DetailId = "detailID"; // 播放前若与当前详情不一致，宿主会先取详情
        public const string Page = "pg";
        public const string Quick = "quick";
        /// <summary>categoryContent 的 extend 筛选（JSON 对象字符串）。</summary>
        public const string Extend = "extend";
    }
}

/// <summary>
/// 启动文档（request.json）。字段名与宿主读取逻辑一致；这些键承载 JAR 插件所需的全部上下文：
/// 订阅最终 origin、全局 spider、站点 jar、站点 key/api/ext 与 raw 记录。
/// </summary>
public sealed class JarPluginLaunchRequest
{
    [JsonPropertyName("jar")] public required string Jar { get; init; }
    [JsonPropertyName("cache")] public required string Cache { get; init; }
    [JsonPropertyName("profile")] public required string Profile { get; init; }
    /// <summary>站点 api 名（如 <c>csp_AppRJ</c>）；宿主据此定位 spider 类。</summary>
    [JsonPropertyName("api")] public required string Api { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("ext")] public string Ext { get; init; } = "";
    /// <summary>spider 全类名（<c>com.github.catvod.spider.&lt;Name&gt;</c>），便于宿主显式加载。</summary>
    [JsonPropertyName("spider")] public string Spider { get; init; } = "";
    /// <summary>全局 spider JAR 的原始声明（含 <c>;md5;</c>），仅作上下文记录，不在此校验。</summary>
    [JsonPropertyName("globalSpider")] public string GlobalSpider { get; init; } = "";
    /// <summary>订阅最终 origin（跟随重定向后的地址）。</summary>
    [JsonPropertyName("origin")] public string Origin { get; init; } = "";
    /// <summary>站点原始 JSON（raw 上下文）。</summary>
    [JsonPropertyName("site")] public string Site { get; init; } = "";
}

/// <summary>一条命令：id 用于匹配响应文件，params 决定动作。</summary>
public sealed class JarPluginCommand
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("params")] public required Dictionary<string, string> Params { get; init; }
}

/// <summary>
/// {@code --convert} 子命令的请求文档：把 <see cref="Input"/> 处的 DEX 归档转换为
/// <see cref="Output"/> 处的 JVM JAR，并把结果信封写到 <see cref="Result"/>。
/// 转换本身无状态；缓存、锁与原子提交由调用方（桌面端）在进程外负责。
/// </summary>
public sealed class JarPluginConvertRequest
{
    [JsonPropertyName("input")] public required string Input { get; init; }
    [JsonPropertyName("output")] public required string Output { get; init; }
    [JsonPropertyName("result")] public required string Result { get; init; }
}

/// <summary>一次成功转换的落盘标记：与 jar 同键，缺它即视为未提交（原子提交的可见性信号）。</summary>
public sealed class JarPluginConversionMarker
{
    [JsonPropertyName("contentHash")] public required string ContentHash { get; init; }
    [JsonPropertyName("hostVersion")] public required string HostVersion { get; init; }
    [JsonPropertyName("dexFileCount")] public int DexFileCount { get; init; }
    [JsonPropertyName("dexVersion")] public string DexVersion { get; init; } = "";
    [JsonPropertyName("classCount")] public int ClassCount { get; init; }
}

/// <summary>
/// 宿主响应信封。成功时 <see cref="Result"/> 是被调用方法的原始 JSON 字符串；
/// 失败时 <see cref="Error"/> / <see cref="ErrorCode"/> 给出分类，见 <see cref="JarPluginErrors"/>。
/// </summary>
public sealed class JarPluginEnvelope
{
    [JsonPropertyName("result")] public string? Result { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
    [JsonPropertyName("ready")] public bool? Ready { get; init; }
    [JsonPropertyName("host")] public string? Host { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }
    [JsonPropertyName("library")] public string? Library { get; init; }
    [JsonPropertyName("missingClass")] public string? MissingClass { get; init; }
    [JsonPropertyName("trace")] public string? Trace { get; init; }
    // ---- --convert 结果字段（DEX→JVM）----
    [JsonPropertyName("ok")] public bool? Ok { get; init; }
    [JsonPropertyName("dexFiles")] public int? DexFiles { get; init; }
    [JsonPropertyName("dexVersion")] public string? DexVersion { get; init; }
    [JsonPropertyName("classCount")] public int? ClassCount { get; init; }

    public bool Failed => !string.IsNullOrEmpty(Error) || !string.IsNullOrEmpty(ErrorCode);
}

/// <summary>PluginHost 自己的 JSON 源码生成上下文；避免依赖 Core 的程序集级上下文。</summary>
[JsonSerializable(typeof(JarPluginLaunchRequest))]
[JsonSerializable(typeof(JarPluginCommand))]
[JsonSerializable(typeof(JarPluginEnvelope))]
[JsonSerializable(typeof(JarPluginConvertRequest))]
[JsonSerializable(typeof(JarPluginConversionMarker))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class JarPluginJsonContext : JsonSerializerContext;

public static class JarPluginJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = JarPluginJsonContext.Default,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));
}
