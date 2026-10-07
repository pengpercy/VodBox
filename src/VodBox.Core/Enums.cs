using System.Text.Json.Serialization;

namespace VodBox.Core;

/// <summary>内容源运行时分类（对应 TVBox site.api 的 csp_/.js/.py 与 MacCMS 直连）。</summary>
public enum SourceRuntime
{
    /// <summary>苹果 CMS v10 JSON/XML 采集直连。</summary>
    MacCms,
    /// <summary>原生 C# 重写的 csp_ 爬虫（无 JVM/脚本，见 CspSpiders 注册表）。</summary>
    NativeSpider,
    /// <summary>drpy .js 脚本（QuickJS 宿主）。</summary>
    QuickJs,
    /// <summary>.py 脚本。</summary>
    Python,
    /// <summary>csp_ Java jar/其他无桌面实现，桌面端不支持。</summary>
    Node,
    /// <summary>本地媒体。</summary>
    Local,
}

/// <summary>播放地址解析方式：直连 / JSON 解析接口 / 网页嗅探。</summary>
public enum ResolutionKind { Direct, Json, Sniff }

/// <summary>播放器状态机。</summary>
public enum PlaybackState { Idle, Resolving, Loading, Playing, Paused, Buffering, Ended, Failed }

/// <summary>轨道类型。</summary>
public enum TrackKind { Audio, Subtitle, Video }

/// <summary>配置类型（对应 FongMi Config.type：0=点播 1=直播 2=壁纸）。</summary>
public enum ConfigKind { Vod = 0, Live = 1, Wall = 2 }

/// <summary>收藏类型（点播 / 直播频道）。</summary>
public enum FavoriteKind { Vod = 0, Live = 1 }
