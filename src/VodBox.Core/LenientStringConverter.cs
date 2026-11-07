using System.Text.Json;
using System.Text.Json.Serialization;

namespace VodBox.Core;

/// <summary>数字 token → string（TVBox 圈接口 type_id 有时是数字有时是字符串，Gson 宽容处理）。</summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "",
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.True => "1",
            JsonTokenType.False => "0",
            _ => "",
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>数字或数字字符串 → int（采集站 page/pagecount 有时是字符串，异常值回落 0）。</summary>
public sealed class LenientIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var value) ? value : (int)Math.Clamp(reader.GetDouble(), int.MinValue, int.MaxValue);
            case JsonTokenType.String:
                return int.TryParse(reader.GetString(), out var parsed) ? parsed : 0;
            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
