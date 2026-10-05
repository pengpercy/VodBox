using System.Text.Json;
using System.Text.Json.Serialization;

namespace VodBox.Core;

public enum ProviderRuntime { Csharp, Quickjs, Python, Node }
public enum ResolutionKind { Direct, Json, Browser }
public enum PlaybackState { Idle, Resolving, Loading, Playing, Paused, Buffering, Ended, Failed }
public enum TrackKind { Audio, Subtitle }

public sealed record SourceDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public ProviderRuntime Runtime { get; set; }
    public string Provider { get; set; } = "catalog";
    public string? Entry { get; set; }
    public Dictionary<string, JsonElement> Options { get; set; } = [];
    public string? ResolverId { get; set; }
}

public sealed record VodBoxConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "default";
    public List<SourceDefinition> Sources { get; set; } = [];
    public List<LiveSourceDefinition> LiveSources { get; set; } = [];
    public List<ResolverDefinition> Resolvers { get; set; } = [];
}

public sealed record Category(string Id, string Name);
public sealed record MediaItem(string Id, string Title, string? Poster = null, string? Remarks = null);
public sealed record Episode(string Id, string Title);
public sealed record PlaybackLine(string Id, string Name, IReadOnlyList<Episode> Episodes);
public sealed record MediaDetail(MediaItem Item, string Description, IReadOnlyList<PlaybackLine> PlaybackLines);
public sealed record MediaPage(IReadOnlyList<MediaItem> Items, string? NextCursor = null);
public sealed record SubtitleSource(string Uri, string Name, string? Language = null);
public sealed record MediaTrack(string Id, string Name, TrackKind Kind);

public sealed record PlaybackRequest
{
    public required string Uri { get; set; }
    public string? DanmakuUri { get; set; }
    public string Title { get; set; } = "媒体";
    public ResolutionKind ResolutionKind { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public List<SubtitleSource> Subtitles { get; set; } = [];
    public long StartPositionMs { get; set; }
    public string SourceId { get; set; } = "local";
    public string MediaId { get; set; } = "";
    public string EpisodeId { get; set; } = "";
    public bool IsLive { get; set; }
    public string? ResolverId { get; set; }
    public string? OriginalUri { get; set; }
}

public sealed record PlaybackSnapshot(PlaybackState State, TimeSpan Position, TimeSpan Duration,
    bool CanSeek, string? Error = null);
public sealed record PlaybackEvent(long SessionId, PlaybackSnapshot Snapshot);
public sealed record HistoryEntry(string ConfigId, string SourceId, string MediaId, string EpisodeId,
    string Title, string Uri, long PositionMs, DateTimeOffset UpdatedAt, ResolutionKind ResolutionKind = ResolutionKind.Direct, string? ResolverId = null);
public sealed record FavoriteEntry(string ConfigId, string SourceId, string MediaId, string Title);
public sealed record LiveSourceDefinition(string Id, string Name, string Uri, string? Epg = null, Dictionary<string, string>? EpgMap = null);
public sealed record LiveChannel(string Id, string Name, string Group, IReadOnlyList<string> Uris,
    string? Logo = null, string? TvgId = null, Dictionary<string, string>? Headers = null, string LiveSourceId = "");
public sealed record Programme(string ChannelId, string Title, DateTimeOffset Start, DateTimeOffset End);

public static class WireJson
{
    public static JsonSerializerOptions Options => VodBoxJson.Default.Options;
    public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> TypeInfo<T>() =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(VodBoxJson.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException($"JSON 类型未注册：{typeof(T).Name}"));
}

public sealed record ScriptParams(string? CategoryId = null, string? Cursor = null, string? Query = null, string? MediaId = null, string? EpisodeId = null, Dictionary<string, string>? Filters = null);
public sealed record RpcRequest(int ApiVersion, long RequestId, string SourceId, string Method, JsonElement Params);
public sealed record RpcResponse(int ApiVersion, long RequestId, JsonElement Result, string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = false)]
[JsonSerializable(typeof(VodBoxConfig))]
[JsonSerializable(typeof(MediaPage))]
[JsonSerializable(typeof(MediaDetail))]
[JsonSerializable(typeof(PlaybackRequest))]
[JsonSerializable(typeof(IReadOnlyList<Category>))]
[JsonSerializable(typeof(List<LiveChannel>))]
[JsonSerializable(typeof(ScriptParams))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(DanmakuDocument))]
[JsonSerializable(typeof(PortableBackup))]
[JsonSerializable(typeof(AppPreferences))]
[JsonSerializable(typeof(List<SavedConfiguration>))]
public partial class VodBoxJson : JsonSerializerContext;
