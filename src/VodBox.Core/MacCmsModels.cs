using System.Text.Json.Serialization;

// TVBox 生态接口字段类型不稳定（type_id/vod_id 时而数字时而字符串），与 Gson 宽容行为对齐
#pragma warning disable CS1591

namespace VodBox.Core;

/// <summary>苹果 CMS v10 采集接口响应（ac=list）。</summary>
public sealed class MacCmsCategoryResponse
{
    [JsonPropertyName("class")] public List<MacCmsClass> Classes { get; set; } = [];
}
public sealed class MacCmsClass
{
    [JsonPropertyName("type_id")] [JsonConverter(typeof(LenientStringConverter))] public string TypeId { get; set; } = "";
    [JsonPropertyName("type_name")] [JsonConverter(typeof(LenientStringConverter))] public string TypeName { get; set; } = "";
}

/// <summary>苹果 CMS v10 视频条目。</summary>
public sealed class MacCmsVod
{
    [JsonPropertyName("vod_id")] [JsonConverter(typeof(LenientStringConverter))] public string VodId { get; set; } = "";
    [JsonPropertyName("vod_name")] [JsonConverter(typeof(LenientStringConverter))] public string VodName { get; set; } = "";
    [JsonPropertyName("vod_pic")] [JsonConverter(typeof(LenientStringConverter))] public string? VodPic { get; set; }
    [JsonPropertyName("vod_remarks")] [JsonConverter(typeof(LenientStringConverter))] public string? VodRemarks { get; set; }
    [JsonPropertyName("vod_year")] [JsonConverter(typeof(LenientStringConverter))] public string? VodYear { get; set; }
    [JsonPropertyName("vod_area")] [JsonConverter(typeof(LenientStringConverter))] public string? VodArea { get; set; }
    [JsonPropertyName("type_name")] [JsonConverter(typeof(LenientStringConverter))] public string? TypeName { get; set; }
    [JsonPropertyName("vod_content")] [JsonConverter(typeof(LenientStringConverter))] public string? VodContent { get; set; }
    [JsonPropertyName("vod_director")] [JsonConverter(typeof(LenientStringConverter))] public string? VodDirector { get; set; }
    [JsonPropertyName("vod_actor")] [JsonConverter(typeof(LenientStringConverter))] public string? VodActor { get; set; }
    [JsonPropertyName("vod_play_from")] [JsonConverter(typeof(LenientStringConverter))] public string? VodPlayFrom { get; set; }
    [JsonPropertyName("vod_play_url")] [JsonConverter(typeof(LenientStringConverter))] public string? VodPlayUrl { get; set; }
}

public sealed class MacCmsListResponse
{
    public int Page { get; set; } = 1;
    [JsonPropertyName("pagecount")] public int PageCount { get; set; } = 1;
    public int Total { get; set; }
    public List<MacCmsVod> List { get; set; } = [];
    [JsonPropertyName("class")] public List<MacCmsClass> Classes { get; set; } = [];
}

public sealed class MacCmsDetailResponse
{
    public List<MacCmsVod> List { get; set; } = [];
}

public sealed class MacCmsSearchResponse
{
    public List<MacCmsVod> List { get; set; } = [];
}

