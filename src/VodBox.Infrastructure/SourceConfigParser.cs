using System.Numerics;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>
/// LunaTV 订阅配置解析：订阅地址返回原始 JSON 或 base58 编码的 JSON（LunaTV 管理后台「配置订阅」格式）。
/// 解析宽容：允许注释与尾逗号、BOM，字段类型数字/字符串混用。
/// </summary>
public static class SourceConfigParser
{
    private static readonly JsonSerializerOptions LenientOptions = new(Json.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>把订阅响应解码为配置 JSON 文本：JSON 原样返回，否则按 base58 解码；都不是则抛出。</summary>
    public static string Decode(string content)
    {
        var text = content.Trim().TrimStart('﻿');
        if (text.StartsWith('{')) return text;
        if (TryDecodeBase58(text, out var decoded) && decoded.TrimStart().TrimStart('﻿').StartsWith('{')) return decoded.Trim().TrimStart('﻿');
        throw new InvalidDataException("订阅内容既不是 JSON 也不是 base58 编码的 JSON。");
    }

    /// <summary>解析配置 JSON（已解码）。</summary>
    public static LunaConfigFile Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LunaConfigFile>)LenientOptions.GetTypeInfo(typeof(LunaConfigFile)))
                ?? throw new InvalidDataException("配置文件为空。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"配置文件不是有效的 JSON：{error.Message}", error);
        }
    }

    /// <summary>检查配置文件是否至少声明了一个采集源或直播源。</summary>
    public static void Validate(LunaConfigFile config)
    {
        if ((config.ApiSite?.Count ?? 0) == 0 && (config.Lives?.Count ?? 0) == 0 && (config.CustomCategory?.Count ?? 0) == 0)
            throw new InvalidDataException("配置文件中没有 api_site / lives / custom_category。");
    }

    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>比特币字母表 base58 解码为 UTF-8 文本。</summary>
    public static bool TryDecodeBase58(string text, out string decoded)
    {
        decoded = "";
        if (text.Length == 0) return false;
        var value = BigInteger.Zero;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            var digit = Alphabet.IndexOf(c);
            if (digit < 0) return false;
            value = value * 58 + digit;
        }
        var bytes = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = text.TakeWhile(c => c == '1').Count();
        var result = new byte[leadingZeros + bytes.Length];
        bytes.CopyTo(result, leadingZeros);
        try
        {
            decoded = new UTF8Encoding(false, true).GetString(result);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>base58 编码（导出订阅、测试用）。</summary>
    public static string EncodeBase58(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var builder = new StringBuilder();
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            builder.Insert(0, Alphabet[(int)remainder]);
        }
        foreach (var b in bytes) { if (b != 0) break; builder.Insert(0, '1'); }
        return builder.ToString();
    }
}
