using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>有界 XMLTV 解析。禁用 DTD/外部实体；无时区时间拒绝，避免把服务器本地时间当节目时间。</summary>
public static class XmlTvParser
{
    /// <summary>解析为扁平节目列表（SourceId 取自 programme 的 source 属性，缺省为空）。</summary>
    public static IReadOnlyList<Programme> Parse(string xml) => ParseDocument(xml, null).Programmes;

    /// <summary>
    /// 按来源解析：强制 SourceId，并保留每个频道的全部 display-name（供唯一名候选匹配）。
    /// 不改变 <see cref="Parse"/> 的既有语义。
    /// </summary>
    public static EpgSourceData ParseSource(string xml, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("来源 id 不能为空。", nameof(sourceId));
        var document = ParseDocument(xml, sourceId);
        return new EpgSourceData(sourceId, document.Programmes, document.Names);
    }

    private static (IReadOnlyList<Programme> Programmes, IReadOnlyDictionary<string, IReadOnlyList<string>> Names)
        ParseDocument(string xml, string? forceSourceId)
    {
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "tv") throw new InvalidDataException("不是 XMLTV 节目表。");
        var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (document.Root is { } root)
        {
            foreach (var channel in root.Elements("channel"))
            {
                var id = (string?)channel.Attribute("id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var displayNames = channel.Elements("display-name")
                    .Select(element => element.Value.Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                names[id] = displayNames.Length > 0 ? displayNames : [id];
            }
        }
        var result = new List<Programme>();
        foreach (var item in document.Root!.Elements("programme"))
        {
            var id = (string?)item.Attribute("channel");
            var title = item.Element("title")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) ||
                !TryTime((string?)item.Attribute("start"), out var start) ||
                !TryTime((string?)item.Attribute("stop"), out var end) || end <= start) continue;
            if (result.Count >= 100000) throw new InvalidDataException("XMLTV 节目数量超过上限。");
            var channelName = names.TryGetValue(id, out var declared) ? declared[0] : id;
            var sourceId = forceSourceId ?? (string?)item.Attribute("source") ?? "";
            result.Add(new Programme(id, title, start, end, channelName, sourceId));
        }
        return (result.Distinct().OrderBy(x => x.Start).ToArray(), names);
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
