using System.Text.Json.Serialization;

namespace VodBox.Infrastructure;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(QingtingGraphRequest))]
[JsonSerializable(typeof(GuaziFilterRequest))]
[JsonSerializable(typeof(GuaziDetailRequest))]
internal partial class SportsJsonContext : JsonSerializerContext;

internal sealed record QingtingGraphRequest(string Query);
internal sealed record GuaziFilterRequest(string Frame, string Hot, string Tag, string Type);
internal sealed record GuaziDetailRequest(string Mid);
