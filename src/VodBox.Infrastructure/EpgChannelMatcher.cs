using System.Globalization;
using System.Text;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>频道匹配结果。</summary>
public enum EpgMatchKind
{
    /// <summary>源内没有可信匹配。</summary>
    None,
    /// <summary>当前来源明确的 tvg-id 精确命中。</summary>
    ByTvgId,
    /// <summary>tvg-id 失败后，经审阅的规范化名称在源内唯一命中。</summary>
    ByUniqueName,
    /// <summary>名称命中多个 id（同台多名/id），需人工确认，不任取第一项。</summary>
    Ambiguous,
}

/// <summary>
/// 直播频道 → 节目表频道 的匹配器。
/// 优先源内 tvg-id 精确匹配；失败后只接受源内唯一的规范化名称候选；
/// 绝不跨源按数字 id 直接合并。
/// </summary>
public static class EpgChannelMatcher
{
    /// <summary>把直播频道匹配到某个来源索引内的频道键。</summary>
    public static EpgMatchKind Resolve(EpgProgrammeIndex index, LiveChannel channel, out EpgChannelKey key)
    {
        key = default;
        // tvg-id 命中「源内声明过」的频道即可：声明了但当天没有节目记录的频道也算匹配成功，
        // 由调用方显示「暂无节目安排」，不能退化成「未匹配」。
        if (!string.IsNullOrWhiteSpace(channel.TvgId) && index.HasChannel(channel.TvgId!))
        {
            key = new EpgChannelKey(index.SourceId, channel.TvgId!);
            return EpgMatchKind.ByTvgId;
        }
        var normalized = Normalize(channel.Name);
        if (normalized.Length == 0) return EpgMatchKind.None;
        var candidates = index.ChannelIdsByName(normalized);
        if (candidates.Count == 1)
        {
            key = new EpgChannelKey(index.SourceId, candidates[0]);
            return EpgMatchKind.ByUniqueName;
        }
        return candidates.Count > 1 ? EpgMatchKind.Ambiguous : EpgMatchKind.None;
    }

    /// <summary>规范化名称：NFKC → 大写 → 审阅过的繁简/异体折叠 → 去空白与分隔标点。</summary>
    /// <remarks>
    /// 刻意保留 +、4K/8K、HD/SD、地区（港澳台、国际、欧洲、美洲）等差异：
    /// 不全局删「HD/台/频道」，否则 CCTV4K/CCTV8K 会被折叠成 CCTV，造成误配。
    /// </remarks>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var text = name.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            if (IsSeparator(raw)) continue;
            var ch = Fold(raw);
            builder.Append(char.ToUpperInvariant(ch));
        }
        return builder.ToString();
    }

    /// <summary>纯数字 id：不作为名称候选，避免小整数 id 被当成频道名误配。</summary>
    public static bool IsNumericId(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var ch in value)
            if (!char.IsAsciiDigit(ch)) return false;
        return true;
    }

    private static bool IsSeparator(char ch) =>
        char.IsWhiteSpace(ch) || ch is '-' or '_' or '.' or '·' or '•' or '・'
            or '(' or ')' or '（' or '）' or '[' or ']' or '【' or '】' or '《' or '》'
            or '「' or '」' or '/' or '\\' or '|' or ':' or '：'
            or ',' or '，' or '、' or '!' or '！' or '?' or '？'
            or '"' or '\'' or '“' or '”' or '‘' or '’' or '*';

    /// <summary>
    /// 审阅过的繁简 / 异体字形折叠。只含电视常用字，避免用全局「删字」规则造成碰撞。
    /// 折叠只降低同一实体的写法差异；是否采信仍由「源内唯一候选」把关。
    /// </summary>
    private static char Fold(char ch) => ch switch
    {
        '臺' => '台', '視' => '视', '衛' => '卫', '頻' => '频', '峽' => '峡',
        '廣' => '广', '東' => '东', '華' => '华', '電' => '电', '劇' => '剧',
        '綜' => '综', '藝' => '艺', '體' => '体', '灣' => '湾', '聞' => '闻',
        '聯' => '联', '節' => '节', '娛' => '娱', '樂' => '乐', '戲' => '戏',
        '農' => '农', '業' => '业', '軍' => '军', '寧' => '宁', '肅' => '肃',
        '陝' => '陕', '雲' => '云', '貴' => '贵', '慶' => '庆', '遼' => '辽',
        '龍' => '龙', '蘇' => '苏', '財' => '财', '經' => '经', '兒' => '儿',
        '動' => '动', '畫' => '画', '紀' => '纪', '錄' => '录', '實' => '实',
        '際' => '际', '國' => '国', '學' => '学', '醫' => '医',
        '藥' => '药', '車' => '车', '馬' => '马', '門' => '门', '風' => '风',
        '飛' => '飞', '養' => '养', '產' => '产', '網' => '网', '線' => '线',
        '紅' => '红', '綠' => '绿', '藍' => '蓝', '黃' => '黄', '銀' => '银',
        _ => ch,
    };
}
