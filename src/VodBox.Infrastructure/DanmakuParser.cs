using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>哔哩哔哩 XML弹幕：限制尺寸/数量，禁止外部实体，跳过高级脚本弹幕。</summary>
public static class DanmakuParser
{
    public static IReadOnlyList<DanmakuComment> Parse(byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("弹幕数据超过上限。");
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var compressed = new MemoryStream(bytes);
            using var input = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (output.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("弹幕解压后超过上限。");
                output.Write(buffer, 0, count);
            }
            bytes = output.ToArray();
        }
        var text = DefaultHttp.Decode(bytes);
        return text.TrimStart().StartsWith('<') ? ParseXml(text) : ParseJson(text);
    }

    /// <summary>显式JSON格式：[{time,text,color,mode}]，mode为scroll/top/bottom。</summary>
    public static IReadOnlyList<DanmakuComment> ParseJson(string json)
    {
        if (json.Length > 8 * 1024 * 1024) throw new InvalidDataException("弹幕JSON超过上限。");
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("comments", out var comments)) root = comments;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Array) throw new InvalidDataException("弹幕JSON不是数组。");
        var result = new List<DanmakuComment>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object || !item.TryGetProperty("time", out var time) || time.ValueKind != System.Text.Json.JsonValueKind.Number || !time.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 0 ||
                !item.TryGetProperty("text", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            var text = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 300) continue;
            var color = 0xFFFFFFu;
            if (item.TryGetProperty("color", out var colorValue) && (colorValue.ValueKind != System.Text.Json.JsonValueKind.Number || !colorValue.TryGetUInt32(out color) || color > 0xFFFFFF)) continue;
            var mode = DanmakuMode.Scroll;
            if (item.TryGetProperty("mode", out var modeValue))
            {
                if (modeValue.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var label = modeValue.GetString();
                if (label is not ("scroll" or "top" or "bottom")) continue;
                mode = label == "top" ? DanmakuMode.Top : label == "bottom" ? DanmakuMode.Bottom : DanmakuMode.Scroll;
            }
            if (result.Count >= 50000) throw new InvalidDataException("弹幕数量超过上限。");
            result.Add(new DanmakuComment(seconds, text, color, mode));
        }
        return result.OrderBy(item => item.Seconds).ToArray();
    }

    public static IReadOnlyList<DanmakuComment> ParseXml(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        var result = new List<DanmakuComment>();
        foreach (var item in document.Descendants("d"))
        {
            var attributes = ((string?)item.Attribute("p"))?.Split(',');
            if (attributes is null || attributes.Length < 4 ||
                !double.TryParse(attributes[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds < 0 ||
                !int.TryParse(attributes[1], out var mode) || mode is not (1 or 2 or 3 or 4 or 5) ||
                !uint.TryParse(attributes[3], out var color) || color > 0xFFFFFF) continue;
            var text = item.Value.Trim();
            if (text.Length == 0 || text.Length > 300) continue;
            if (result.Count >= 50000) throw new InvalidDataException("弹幕数量超过上限。");
            result.Add(new DanmakuComment(seconds, text, color, mode == 4 ? DanmakuMode.Bottom : mode == 5 ? DanmakuMode.Top : DanmakuMode.Scroll));
        }
        return result.OrderBy(item => item.Seconds).ToArray();
    }
}
