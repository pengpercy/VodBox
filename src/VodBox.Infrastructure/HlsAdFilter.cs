using System.Globalization;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>
/// HLS 去广告（对应 LunaTV 的 blockAd，但能真正剔除广告）：采集站插播广告的典型形态是
/// 以 <c>#EXT-X-DISCONTINUITY</c> 隔开的一小段分片，分片来自与正片不同的目录/域名，并重设 <c>#EXT-X-KEY</c>。
/// 规则：按分片所在目录（解析为绝对地址后去掉文件名）统计时长，时长最多的目录视为正片；
/// 其余目录组成、且总时长不超过 <see cref="MaxAdSeconds"/> 的分段整段删除。
/// 删除分段里的 KEY 状态会延续给下一个未声明 KEY 的保留分段，保证解密状态与原清单一致。
/// 主清单（多码流）、单分段或无法判定正片的清单原样返回。
/// </summary>
public static class HlsAdFilter
{
    public const double MaxAdSeconds = 300;

    private sealed class Section
    {
        public List<string> Lines { get; } = [];
        public List<string> Directories { get; } = [];
        public double Duration { get; set; }
        public bool HasKeyBeforeSegment { get; set; }
        public string? LastKey { get; set; }
    }

    public static string Filter(string playlist, Uri baseUri) => Filter(playlist, baseUri, out _);

    public static string Filter(string playlist, Uri baseUri, out int removedSegments)
    {
        removedSegments = 0;
        if (playlist.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal) || !playlist.Contains("#EXT-X-DISCONTINUITY", StringComparison.Ordinal)) return playlist;
        var header = new List<string>();
        var sections = new List<Section>();
        Section? current = null;
        double pendingDuration = 0;
        var trailer = new List<string>();
        foreach (var raw in playlist.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal))
            {
                current = new Section();
                sections.Add(current);
                continue;
            }
            if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) { trailer.Add(line); continue; }
            if (current is null)
            {
                // 首个分片之前的全局头单独保留；首个分片开始即进入第 0 段。
                if (line.StartsWith("#EXTINF", StringComparison.Ordinal) || (line.Length > 0 && !line.StartsWith('#')))
                {
                    current = new Section();
                    sections.Add(current);
                }
                else
                {
                    if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal)) { current = new Section(); sections.Add(current); }
                    else { header.Add(line); continue; }
                }
            }
            current.Lines.Add(line);
            if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                current.LastKey = line;
                if (current.Directories.Count == 0) current.HasKeyBeforeSegment = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..].Split(',')[0];
                pendingDuration = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                current.Directories.Add(Uri.TryCreate(baseUri, line, out var segment) ? segment.GetLeftPart(UriPartial.Path)[..(segment.GetLeftPart(UriPartial.Path).LastIndexOf('/') + 1)] : "");
                current.Duration += pendingDuration;
                pendingDuration = 0;
            }
        }
        var withSegments = sections.Where(section => section.Directories.Count > 0).ToArray();
        if (withSegments.Length < 2) return playlist;
        var durations = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var section in withSegments)
            foreach (var directory in section.Directories.Distinct())
                durations[directory] = durations.GetValueOrDefault(directory) + section.Duration / section.Directories.Distinct().Count();
        var main = durations.OrderByDescending(pair => pair.Value).First();
        var total = durations.Values.Sum();
        // 正片目录至少占一半时长才判定，避免把多目录正片误删。
        if (main.Value < total * 0.5) return playlist;

        var output = new StringBuilder();
        foreach (var line in header) output.Append(line).Append('\n');
        string? carriedKey = null;
        var emitted = 0;
        foreach (var section in sections)
        {
            var isAd = section.Directories.Count > 0 && section.Directories.All(directory => directory != main.Key) && section.Duration <= MaxAdSeconds;
            if (isAd)
            {
                removedSegments += section.Directories.Count;
                if (section.LastKey is not null) carriedKey = section.LastKey;
                continue;
            }
            if (section.Lines.Count == 0) continue;
            if (emitted > 0) output.Append("#EXT-X-DISCONTINUITY\n");
            if (carriedKey is not null && !section.HasKeyBeforeSegment) output.Append(carriedKey).Append('\n');
            carriedKey = null;
            foreach (var line in section.Lines) output.Append(line).Append('\n');
            emitted++;
        }
        foreach (var line in trailer) output.Append(line).Append('\n');
        return removedSegments == 0 ? playlist : output.ToString();
    }
}
