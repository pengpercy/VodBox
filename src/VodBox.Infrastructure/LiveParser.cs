using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Globalization;
using VodBox.Core;

namespace VodBox.Infrastructure;

public static partial class LiveParser
{
    [GeneratedRegex("([\\w-]+)=\"([^\"]*)\"")]
    private static partial Regex Attributes();
    public static IReadOnlyList<LiveChannel> Parse(string content)
    {
        content = content.TrimStart('\uFEFF', ' ', '\n', '\r');
        if (content.StartsWith('[')) return JsonSerializer.Deserialize(content, VodBoxJson.Default.ListLiveChannel) ?? [];
        var channels = new List<LiveChannel>(); string group = "未分组";
        string? name = null, id = null, logo = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.LastIndexOf(','); name = comma >= 0 ? line[(comma + 1)..] : "频道";
                var attrs = Attributes().Matches(line).ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);
                group = attrs.GetValueOrDefault("group-title", "未分组"); id = attrs.GetValueOrDefault("tvg-id"); logo = attrs.GetValueOrDefault("tvg-logo");
            }
            else if (!line.StartsWith('#'))
            {
                if (name is not null) { Add(name, group, line, id, logo); name = null; }
                else
                {
                    var comma = line.IndexOf(','); if (comma < 0) continue;
                    var title = line[..comma]; var url = line[(comma + 1)..];
                    if (url == "#genre#") group = title; else Add(title, group, url, null, null);
                }
            }
        }
        return channels;
        void Add(string title, string category, string url, string? tvg, string? image)
        {
            var existing = channels.FindIndex(x => x.Name == title && x.Group == category);
            if (existing >= 0) channels[existing] = channels[existing] with { Uris = channels[existing].Uris.Append(url).Distinct().ToList() };
            else channels.Add(new(tvg ?? $"{category}/{title}", title, category, [url], image, tvg));
        }
    }

    public static IReadOnlyList<Programme> ParseEpg(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var programs = new List<Programme>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme") continue;
            var channel = reader.GetAttribute("channel"); var start = reader.GetAttribute("start"); var end = reader.GetAttribute("stop");
            using var subtree = reader.ReadSubtree(); string title = "";
            while (subtree.Read()) if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "title") { title = subtree.ReadElementContentAsString(); break; }
            if (channel is not null && start is not null && end is not null)
                programs.Add(new(channel, title, ParseTime(start), ParseTime(end)));
        }
        return programs;
    }
    private static DateTimeOffset ParseTime(string value)
    {
        // Explicit numeric offset is required; never infer the machine's local zone.
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[1].Length != 5) throw new InvalidDataException("XMLTV 时间必须包含 ±HHMM 时区。");
        var formatted = parts[0] + " " + parts[1].Insert(3, ":");
        return DateTimeOffset.ParseExact(formatted, "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture).ToUniversalTime();
    }
}
