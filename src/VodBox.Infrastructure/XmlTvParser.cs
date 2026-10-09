using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>有界 XMLTV 解析。禁用 DTD/外部实体；无时区时间拒绝，避免把服务器本地时间当节目时间。</summary>
public static class XmlTvParser
{
    public static IReadOnlyList<Programme> Parse(string xml)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "tv") throw new InvalidDataException("不是 XMLTV 节目表。");
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channel in document.Root.Elements("channel"))
        {
            var id = (string?)channel.Attribute("id");
            if (!string.IsNullOrWhiteSpace(id)) names[id] = channel.Element("display-name")?.Value.Trim() ?? id;
        }
        var result = new List<Programme>();
        foreach (var item in document.Root.Elements("programme"))
        {
            var id = (string?)item.Attribute("channel");
            var title = item.Element("title")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) ||
                !TryTime((string?)item.Attribute("start"), out var start) ||
                !TryTime((string?)item.Attribute("stop"), out var end) || end <= start) continue;
            if (result.Count >= 100000) throw new InvalidDataException("XMLTV 节目数量超过上限。");
            result.Add(new Programme(id, title, start, end, names.GetValueOrDefault(id, id)));
        }
        return result.Distinct().OrderBy(x => x.Start).ToArray();
    }

    private static bool TryTime(string? text, out DateTimeOffset time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[1].Length != 5 || parts[1][0] is not ('+' or '-')) return false;
        var zone = parts[1];
        var normalized = parts[0] + " " + zone[..3] + ":" + zone[3..];
        return DateTimeOffset.TryParseExact(normalized, "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);
    }
}
