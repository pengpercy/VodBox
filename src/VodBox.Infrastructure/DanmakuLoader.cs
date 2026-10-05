using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed class DanmakuLoader(HttpClient http)
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumComments = 100000;
    public async Task<IReadOnlyList<DanmakuComment>> LoadAsync(string location, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20)); token = deadline.Token;
        byte[] bytes;
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); bytes = await ReadBoundedAsync(body, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentEncoding.Count > 3) throw new InvalidDataException("弹幕 HTTP 压缩层数超过三层。");
            foreach (string coding in response.Content.Headers.ContentEncoding.Reverse())
                bytes = await DecompressAsync(bytes, coding, token).ConfigureAwait(false);
        }
        else
        {
            string path = uri?.IsFile == true ? uri.LocalPath : location;
            if (uri is not null && !uri.IsFile) throw new InvalidDataException("弹幕仅支持本地文件或 HTTP / HTTPS。");
            await using var file = File.OpenRead(path); bytes = await ReadBoundedAsync(file, token).ConfigureAwait(false);
        }
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        { using var input = new MemoryStream(bytes); await using var gzip = new GZipStream(input, CompressionMode.Decompress); bytes = await ReadBoundedAsync(gzip, token).ConfigureAwait(false); }
        return await Task.Run(() => Parse(bytes, token), token).ConfigureAwait(false);
    }
    private static async Task<byte[]> DecompressAsync(byte[] bytes, string coding, CancellationToken token)
    {
        if (coding.Equals("identity", StringComparison.OrdinalIgnoreCase)) return bytes;
        using var input = new MemoryStream(bytes);
        // HTTP deflate is deployed both as RFC 1950 zlib and as raw RFC 1951 streams.
        bool zlib = bytes.Length >= 2 && (bytes[0] & 15) == 8 && (bytes[0] >> 4) <= 7 && ((bytes[0] << 8) + bytes[1]) % 31 == 0;
        await using Stream decoded = coding.ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            "deflate" when zlib => new ZLibStream(input, CompressionMode.Decompress),
            "deflate" => new DeflateStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new InvalidDataException("不支持的弹幕 HTTP 压缩格式：" + coding)
        };
        return await ReadBoundedAsync(decoded, token).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); byte[] buffer = new byte[32768]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > MaximumBytes) throw new InvalidDataException("弹幕数据超过 8 MiB 限制。"); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
        return output.ToArray();
    }
    public static IReadOnlyList<DanmakuComment> Parse(byte[] bytes, CancellationToken token = default)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("弹幕数据超过 8 MiB 限制。");
        string text = TextEncoding.Decode(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var result = new List<DanmakuComment>();
        if (text.StartsWith('<'))
        {
            using var reader = XmlReader.Create(new StringReader(text), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "d") continue;
                string[] fields = (reader.GetAttribute("p") ?? "").Split(',');
                if (fields.Length < 4 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > 604800 || !int.TryParse(fields[1], out int mode) || !uint.TryParse(fields[3], out uint color)) continue;
                if (mode is not (1 or 2 or 3 or 4 or 5)) continue;
                using var subtree = reader.ReadSubtree(); subtree.Read(); string content = subtree.ReadElementContentAsString();
                Add(result, new((long)(seconds * 1000), content, mode == 4 ? DanmakuMode.Bottom : mode == 5 ? DanmakuMode.Top : DanmakuMode.Scroll, color));
            }
        }
        else
        {
            var document = JsonSerializer.Deserialize(text, VodBoxJson.Default.DanmakuDocument) ?? throw new InvalidDataException("弹幕 JSON 为空。");
            if (document.Version != 1 || document.Comments is null) throw new InvalidDataException("弹幕 JSON 版本或字段无效。");
            if (document.Comments.Count > MaximumComments) throw new InvalidDataException("弹幕条目超过 100000 条。");
            foreach (var comment in document.Comments) { token.ThrowIfCancellationRequested(); if (comment is not null) Add(result, comment); }
        }
        // LINQ's stable ordering retains source order for equal timestamps.
        return result.OrderBy(x => x.TimeMs).ToArray();
    }
    private static void Add(List<DanmakuComment> result, DanmakuComment comment)
    {
        if (comment.TimeMs < 0 || comment.TimeMs > 604800000 || !Enum.IsDefined(comment.Mode) || string.IsNullOrWhiteSpace(comment.Text) || comment.Text.Length > 512 || comment.Color > 0xFFFFFF) return;
        if (result.Count >= MaximumComments) throw new InvalidDataException("弹幕条目超过 100000 条。");
        string text = comment.Text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        result.Add(comment with { Text = text });
    }
}
