using System.Text.Json.Serialization;
namespace VodBox.Infrastructure;
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Catalog))]
public partial class CatalogJson : JsonSerializerContext;
