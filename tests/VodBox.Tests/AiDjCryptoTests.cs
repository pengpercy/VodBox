using System.Text;
using System.Text.Json;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AiDjCryptoTests
{
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-aidj-crypto-vectors.json")));
    private static byte[] Hex(JsonElement item, string key) => Convert.FromHexString(item.GetProperty(key).GetString()!);
    private static Dictionary<string, string> Fields(JsonElement item, string key) => item.GetProperty(key).EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.GetString()!);
    [Fact]
    public void Sm3MatchesIndependentEmbeddedPython()
    {
        using var vectors = Vectors();
        foreach (var item in vectors.RootElement.GetProperty("sm3").EnumerateArray())
            Assert.Equal(Hex(item, "output"), AiDjSigning.Sm3(Encoding.UTF8.GetBytes(item.GetProperty("input").GetString()!)));
    }
    [Fact]
    public void GorgonMatchesIndependentEmbeddedPython()
    {
        using var vectors = Vectors();
        foreach (var item in vectors.RootElement.GetProperty("gorgon").EnumerateArray())
            Assert.Equal(item.GetProperty("output").GetString(), AiDjCrypto.Gorgon(Encoding.UTF8.GetBytes(item.GetProperty("body").GetString()!), item.GetProperty("query").GetString()!, item.GetProperty("timestamp").GetUInt32(), item.GetProperty("random").GetUInt16()));
    }
    [Fact]
    public void HeliosMatchesIndependentEmbeddedPython()
    {
        using var vectors = Vectors();
        foreach (var item in vectors.RootElement.GetProperty("helios").EnumerateArray())
            Assert.Equal(item.GetProperty("output").GetString(), AiDjCrypto.Helios(item.GetProperty("timestamp").GetUInt32(), item.GetProperty("random").GetUInt32()));
    }
    [Fact]
    public void AesV3AllFourTimestampPermutationsMatchIndependentPython()
    {
        using var vectors = Vectors();
        foreach (var item in vectors.RootElement.GetProperty("aesV3").EnumerateArray())
            Assert.Equal(Hex(item, "output"), AiDjSigning.AesV3(Hex(item, "input"), Hex(item, "key"), Hex(item, "iv"), item.GetProperty("timestamp").GetUInt32()));
    }
    [Fact]
    public void HashF13BranchesMatchIndependentPython()
    {
        using var vectors = Vectors();
        foreach (var item in vectors.RootElement.GetProperty("hashF13").EnumerateArray())
            Assert.Equal(Hex(item, "output"), AiDjSigning.HashF13(Hex(item, "querySm3"), Hex(item, "bodyMd5"), item.GetProperty("timestamp").GetUInt32()));
    }
    [Fact]
    public void CompleteSignedUrlAndHeadersMatchPythonWithFixedEntropy()
    {
        using var vectors = Vectors();
        foreach (var item in vectors.RootElement.GetProperty("sign").EnumerateArray())
        {
            var result = AiDjSigning.Sign(item.GetProperty("endpoint").GetString()!, Fields(item, "parameters"), Fields(item, "devices"), Encoding.UTF8.GetBytes(item.GetProperty("body").GetString()!), Fields(item, "headers"),
                new(1792260000, 1792260000125, 1234, 123456, 0x12345678, 0xabcdef01, 0x01234567, 111, 11000));
            Assert.Equal(item.GetProperty("url").GetString(), result.Url);
            var expected = Fields(item, "signedHeaders");
            Assert.Equal(expected.Keys.Order(), result.Headers.Keys.Order());
            foreach (var pair in expected) Assert.Equal(pair.Value, result.Headers[pair.Key]);
        }
    }
}
