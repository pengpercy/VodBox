using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VodBox.Core;

/// <summary>应用统一 JSON 选项：AOT 源码生成、骆驼命名、中文化不转义。</summary>
public static class Json
{
    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = AppJsonContext.Default,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
}

[JsonSerializable(typeof(RemotePlaybackStatus))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(LibraryBackup))]
[JsonSerializable(typeof(TvBoxConfig))]
[JsonSerializable(typeof(TvBoxSite))]
[JsonSerializable(typeof(TvBoxLive))]
[JsonSerializable(typeof(TvBoxParse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(MacCmsCategoryResponse))]
[JsonSerializable(typeof(MacCmsListResponse))]
[JsonSerializable(typeof(MacCmsDetailResponse))]
[JsonSerializable(typeof(MacCmsSearchResponse))]
[JsonSerializable(typeof(MacCmsVod))]
[JsonSerializable(typeof(MediaPage))]
[JsonSerializable(typeof(MediaDetail))]
[JsonSerializable(typeof(MediaItem))]
[JsonSerializable(typeof(Episode))]
[JsonSerializable(typeof(PlaybackLine))]
[JsonSerializable(typeof(Category))]
[JsonSerializable(typeof(PlaybackRequest))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSerializable(typeof(FavoriteEntry))]
[JsonSerializable(typeof(LiveChannel))]
[JsonSerializable(typeof(LiveGroup))]
[JsonSerializable(typeof(LiveSource))]
[JsonSerializable(typeof(ConfigSubscription))]
public sealed partial class AppJsonContext : JsonSerializerContext;
