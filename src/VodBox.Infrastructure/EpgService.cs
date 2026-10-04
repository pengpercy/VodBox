using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using VodBox.Core;

namespace VodBox.Infrastructure;

public sealed record EpgSchedule(IReadOnlyList<Programme> Programmes, IReadOnlyDictionary<string, string[]> ChannelNames);

public sealed class EpgService(HttpClient http, string cacheDirectory)
{
    private sealed record Cached(EpgSchedule Schedule, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Cached> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _load = new(1, 1);
    public async Task<EpgSchedule> LoadAsync(string location, CancellationToken token = default)
    {
        if (_memory.TryGetValue(location, out var value) && value.Expires > DateTimeOffset.UtcNow) return value.Schedule;
        await _load.WaitAsync(token);
        try
        {
            if (_memory.TryGetValue(location, out value) && value.Expires > DateTimeOffset.UtcNow) return value.Schedule;
            string file = Path.Combine(cacheDirectory, "epg", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location))) + ".xml");
            byte[] data; EpgSchedule? schedule = null;
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddHours(-6)) data = await File.ReadAllBytesAsync(file, token);
            else
            {
                var uri = new Uri(location);
                await using var stream = uri.IsFile ? File.OpenRead(uri.LocalPath) : await http.GetStreamAsync(uri, token);
                data = await BoundedContent.ReadAsync(stream, 32 * 1024 * 1024, token);
                if (data.Length > 1 && data[0] == 0x1f && data[1] == 0x8b)
                {
                    using var compressed = new MemoryStream(data); using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                    data = await BoundedContent.ReadAsync(gzip, 32 * 1024 * 1024, token);
                }
                schedule = await Task.Run(() => Parse(data, token), token);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string temporary = file + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, data, token); File.Move(temporary, file, true); }
                finally { File.Delete(temporary); }
            }
            schedule ??= await Task.Run(() => Parse(data, token), token);
            if (_memory.Count >= 8) _memory.Clear();
            _memory[location] = new(schedule, DateTimeOffset.UtcNow.AddHours(1)); return schedule;
        }
        finally { _load.Release(); }
    }
    private static EpgSchedule Parse(byte[] data, CancellationToken token)
    {
        TextEncoding.EnsureRegistered();
        using var stream = new MemoryStream(data);
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
        var names = new Dictionary<string, string[]>(StringComparer.Ordinal); var programmes = new List<Programme>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name == "channel")
            {
                string? id = reader.GetAttribute("id"); var displays = new List<string>();
                using var channel = reader.ReadSubtree();
                while (channel.Read()) if (channel.NodeType == XmlNodeType.Element && channel.Name == "display-name") displays.Add(channel.ReadString());
                if (id is not null) names[id] = displays.ToArray();
            }
            else if (reader.Name == "programme")
            {
                string? id = reader.GetAttribute("channel"), start = reader.GetAttribute("start"), end = reader.GetAttribute("stop"); string title = "";
                using var programme = reader.ReadSubtree();
                while (programme.Read()) if (programme.NodeType == XmlNodeType.Element && programme.Name == "title") { title = programme.ReadString(); break; }
                if (id is not null && start is not null && end is not null)
                    programmes.Add(new(id, title, LiveParser.ParseTime(start), LiveParser.ParseTime(end)));
            }
        }
        return new(programmes, names);
    }
    public static IReadOnlyList<Programme> ForChannel(EpgSchedule schedule, LiveChannel channel, string? mappedId = null, DateTimeOffset? now = null)
    {
        string? id = mappedId ?? channel.TvgId;
        if (id is null || !schedule.Programmes.Any(x => x.ChannelId == id))
            id = schedule.ChannelNames.FirstOrDefault(x => x.Value.Any(name => Normalize(name) == Normalize(channel.Name))).Key ?? channel.Name;
        var time = now ?? DateTimeOffset.UtcNow;
        return schedule.Programmes.Where(x => x.ChannelId == id && x.End > time.AddHours(-2) && x.Start < time.AddDays(2)).OrderBy(x => x.Start).Take(80).ToList();
    }
    private static string Normalize(string name) => name.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
}
