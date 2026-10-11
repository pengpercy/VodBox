namespace VodBox.Infrastructure;

/// <summary>
/// 内置节目表来源定义。URL 取自 taksssss/tv 的 README 镜像列表（Gitee raw），
/// 2026-10-11 实测 8 个镜像均 HTTP 200、gzip 完整解压、XML 可解析。
/// 抓取时间点不代表持续可用性承诺，应用必须能容忍单源失败。
/// </summary>
/// <param name="Id">来源命名空间 id：作为 (sourceId, channelId) 的一部分，跨源数字 id 永不合并。</param>
/// <param name="Name">设置页展示名。</param>
/// <param name="Url">下载地址（gzip XMLTV）。</param>
/// <param name="IsSupplement">是否为补充源（只在主源缺当前节目时按来源级补充）。</param>
/// <param name="Note">运维备注：上游原始地址与已知数据风险，不参与逻辑。</param>
public sealed record EpgSourceDefinition(string Id, string Name, Uri Url, bool IsSupplement = false, string? Note = null);

/// <summary>内置节目表来源目录。默认只启用「一个主源 + 一个补充源」的最小组合。</summary>
public static class EpgSources
{
    /// <summary>主源：112114。当天 540/540 频道、18,725 条节目（2026-10-11 快照），覆盖最广。</summary>
    public const string PrimaryId = "112114";
    /// <summary>补充源：老张的 EPG（51zmt）。100 个核心频道当天 100/100，体量小，用于补主源缺当前节目的频道。</summary>
    public const string SupplementId = "51zmt";

    /// <summary>单源下载上限（压缩字节）。实测 112114 约 221 KiB、51zmt 约 57 KiB，8 MiB 留足余量又防失控。</summary>
    public const int MaxDownloadBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 自动下载组合。刻意只含主源 + 补充源：
    /// e1/e2、HK、epgpw 三源、erw、TW 不进入自动组合——erw/TW 超过现有导入字符/节目上限，
    /// CN 镜像当天仅 121/710 覆盖且滞后，e1/e2 会显著增大体积并带来名称为歧义与时区复杂度。
    /// </summary>
    public static IReadOnlyList<EpgSourceDefinition> BuiltIn { get; } =
    [
        new(PrimaryId, "112114 节目表",
            new Uri("https://gitee.com/taksssss/tv/raw/main/epg/112114.xml.gz"),
            Note: "上游 https://epg.112114.xyz/pp.xml.gz；重复 channel id（CCTV4K）与超额重复节目需去重。"),
        new(SupplementId, "老张的 EPG（51zmt）",
            new Uri("https://gitee.com/taksssss/tv/raw/main/epg/51zmt.xml.gz"),
            IsSupplement: true,
            Note: "上游 http://epg.51zmt.top:8000/e.xml；小整数 id，仅经可信匹配补充主源缺失频道。"),
    ];

    public static EpgSourceDefinition Primary => BuiltIn[0];
    public static EpgSourceDefinition? Supplement => BuiltIn.Count > 1 ? BuiltIn[1] : null;
}
