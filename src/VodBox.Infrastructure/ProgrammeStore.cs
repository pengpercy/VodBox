using System.Globalization;
using System.Xml.Linq;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>节目表独立于原始导入文件持久化，使用 XMLTV 保持 AOT 无反射。</summary>
public sealed class ProgrammeStore(string path)
{
    public Task SaveAsync(IReadOnlyList<Programme> programmes,CancellationToken ct=default)=>AtomicFile.WriteAsync(path,Serialize(programmes),ct);

    public static string Serialize(IReadOnlyList<Programme> programmes)
    {
        var root = new XElement("tv");
        foreach (var group in programmes.GroupBy(x => x.ChannelKey))
            root.Add(new XElement("channel", new XAttribute("id", group.Key), new XElement("display-name", group.First().ChannelName)));
        foreach (var item in programmes)
            root.Add(new XElement("programme", new XAttribute("channel", item.ChannelKey),
                new XAttribute("start", Time(item.Start)), new XAttribute("stop", Time(item.End)), new XElement("title", item.Title)));
        return root.ToString(SaveOptions.DisableFormatting);
    }

    public async Task<IReadOnlyList<Programme>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return [];
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("节目缓存文件过大。");
        return XmlTvParser.Parse(await File.ReadAllTextAsync(path, ct));
    }

    private static string Time(DateTimeOffset time) => time.ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture).Replace(":", "");
}
