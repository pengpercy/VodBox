using System.Text.Json;
using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

public sealed class JsonMetadataTests
{
    [Fact]
    public void GeneratedMetadata_PreservesWebOptionsAndNumericStrings()
    {
        var response = JsonSerializer.Deserialize("{\"page\":\"2\",\"pagecount\":\"3\",\"list\":[]}",
            Json.TypeInfo<MacCmsListResponse>());
        Assert.NotNull(response);
        Assert.Equal(2, response.Page);
        Assert.Equal(3, response.PageCount);
    }

    [Fact]
    public void GeneratedMetadata_SerializesProtocolPayloadWithoutReflection()
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement("中文", Json.TypeInfo<string>()),
            ["page"] = JsonSerializer.SerializeToElement(2, Json.TypeInfo<int>()),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json.TypeInfo<Dictionary<string, JsonElement>>());
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal("中文", document.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("page").GetInt32());
    }
}
