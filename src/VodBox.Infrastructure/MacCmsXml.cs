using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>MacCMS XML 采集格式；禁用 DTD/外部实体并限制解析大小。</summary>
internal static class MacCmsXml
{
    internal static MacCmsListResponse Parse(string text)
    {
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name.LocalName != "rss") throw new InvalidDataException("不是 MacCMS XML 采集响应");
        var list = root.Element("list");
        var result = new MacCmsListResponse
        {
            Page = Number(list?.Attribute("page")?.Value, 1),
            PageCount = Number(list?.Attribute("pagecount")?.Value, 1),
            Total = Number(list?.Attribute("recordcount")?.Value, 0),
            Classes = root.Element("class")?.Elements("ty").Select(item => new MacCmsClass
            {
                TypeId = item.Attribute("id")?.Value ?? "", TypeName = item.Value.Trim()
            }).ToList() ?? []
        };
        foreach (var video in list?.Elements("video") ?? [])
        {
            string Field(string name) => video.Element(name)?.Value.Trim() ?? "";
            var lines = video.Element("dl")?.Elements("dd").ToArray() ?? [];
            result.List.Add(new MacCmsVod
            {
                VodId = Field("id"), VodName = Field("name"), VodPic = Field("pic"),
                VodRemarks = Field("note"), VodYear = Field("year"), VodArea = Field("area"),
                TypeName = Field("type"), VodDirector = Field("director"), VodActor = Field("actor"),
                VodContent = Field("des"),
                VodPlayFrom = string.Join("$$$", lines.Select((line, index) => line.Attribute("flag")?.Value ?? $"line-{index + 1}")),
                VodPlayUrl = string.Join("$$$", lines.Select(line => line.Value.Trim()))
            });
        }
        return result;
    }

    private static int Number(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : fallback;
}
