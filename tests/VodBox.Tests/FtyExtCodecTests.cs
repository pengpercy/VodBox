using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class FtyExtCodecTests
{
    [Fact]
    public void MatchesEveryCapturedNativeOracleVector()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "fty-ext-v8-vectors.json")));
        var vectors = fixture.RootElement.GetProperty("vectors");
        Assert.Equal(20, vectors.GetArrayLength());
        foreach (var vector in vectors.EnumerateArray())
        {
            string decoded = FtyExtCodec.Decode(vector.GetProperty("encrypted").GetString()!);
            Assert.Equal(vector.GetProperty("utf16Length").GetInt32(), decoded.Length);
            Assert.Equal(vector.GetProperty("utf8Sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(decoded))));
        }
    }

    [Fact]
    public void DecodingIsRepeatableAndDoesNotRecursivelyDecodePlaintext()
    {
        const string wire = "5++kwLhNYm9UrO9wh7Dl7eKamTee4s/5";
        Assert.Equal("56We56eY55qE5ZOl5ZOl5Lus", FtyExtCodec.Decode(wire));
        Assert.Equal(FtyExtCodec.Decode(wire), FtyExtCodec.Decode(wire));
        Assert.Equal("", FtyExtCodec.Decode(""));
        Assert.Equal("中文", FtyExtCodec.Decode("NG9SQRn8"));
    }

    [Fact]
    public void JsonConfigPreservesPlaintextAndBoundsBothForms()
    {
        Assert.Equal("{\"host\":\"https://x\"}", FtyExtCodec.JsonConfig(" rfOX1voDIQhH8epBwpmIsuS87CY= "));
        Assert.Equal("{\"type\":1}", FtyExtCodec.JsonConfig(" {\"type\":1} "));
        Assert.Throws<InvalidDataException>(() => FtyExtCodec.JsonConfig(null));
        Assert.Throws<InvalidDataException>(() => FtyExtCodec.Decode("invalid!"));
        Assert.Throws<InvalidDataException>(() => FtyExtCodec.Decode(new string('A', FtyExtCodec.MaxCharacters + 1)));
        Assert.Throws<InvalidDataException>(() => FtyExtCodec.JsonConfig("{" + new string(' ', FtyExtCodec.MaxCharacters) + "}"));
        // The restarted RC4 stream begins with 0xd0: a lone zero cipher byte is invalid UTF-8.
        Assert.Throws<InvalidDataException>(() => FtyExtCodec.Decode("AA=="));
    }
}
