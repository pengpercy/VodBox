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
