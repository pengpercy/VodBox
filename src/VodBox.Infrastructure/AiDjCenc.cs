using System.Buffers.Binary;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>AiDj progressive MP4 CENC index; patches arbitrary ranges without downloading the whole film.</summary>
internal sealed class AiDjCenc
{
    internal sealed record Sample(long Offset, int Size, byte[] Counter);
    private readonly byte[] _key;
    private readonly byte[] _plainMoov;
    internal long MoovStart { get; }
    internal long TotalLength { get; }
    internal IReadOnlyList<Sample> Samples { get; }
    private const int MaximumSamples = 1_000_000;
    private sealed record Box(int Offset, int Size, int Header, string Type)
    { internal int Body => Offset + Header; internal int End => Offset + Size; }

    internal AiDjCenc(byte[] moov, long moovStart, long totalLength, ReadOnlySpan<byte> key)
    {
        if (key.Length != 16 || moovStart < 0 || totalLength < 16 || moov.Length is < 8 or > 8 * 1024 * 1024 || moovStart > totalLength - moov.Length)
            throw new InvalidDataException("AiDj CENC moov/key bounds invalid.");
        _key = key.ToArray(); MoovStart = moovStart; TotalLength = totalLength;
        var root = Boxes(moov, 0, moov.Length).SingleOrDefault();
        if (root is null || root.Type != "moov" || root.Size != moov.Length) throw new InvalidDataException("AiDj expected one complete moov box.");
        var samples = new List<Sample>();
        foreach (var track in Boxes(moov, root.Body, root.End).Where(b => b.Type == "trak"))
        {
            var mdia = Child(moov, track, "mdia"); if (mdia is null) continue;
            var minf = Child(moov, mdia, "minf"); if (minf is null) continue;
            var stbl = Child(moov, minf, "stbl"); if (stbl is null) continue;
            var children = Boxes(moov, stbl.Body, stbl.End).ToDictionary(b => b.Type, StringComparer.Ordinal);
            // Unencrypted tracks need no patch. Incomplete encryption metadata must fail closed.
            bool encrypted = children.ContainsKey("saiz") || children.ContainsKey("saio");
            if (!encrypted) continue;
            if (!children.TryGetValue("stsz", out var stsz) || !children.TryGetValue("stsc", out var stsc) || !children.TryGetValue("saiz", out var saiz) || !children.TryGetValue("saio", out var saio))
                throw new InvalidDataException("AiDj incomplete CENC sample tables.");
            var chunks = children.GetValueOrDefault("stco") ?? children.GetValueOrDefault("co64") ?? throw new InvalidDataException("AiDj missing chunk offsets.");
            ParseTrack(moov, stsz, stsc, saiz, saio, chunks, moovStart, totalLength, samples);
        }
        if (samples.Count == 0) throw new InvalidDataException("AiDj moov has no supported encrypted samples.");
        var sorted = samples.OrderBy(s => s.Offset).ToArray();
        for (int i = 1; i < sorted.Length; i++) if (sorted[i].Offset < sorted[i - 1].Offset + sorted[i - 1].Size) throw new InvalidDataException("AiDj overlapping encrypted samples.");
        Samples = sorted; _plainMoov = RewriteMoov(moov);
    }
    private static void ParseTrack(byte[] moov, Box stsz, Box stsc, Box saiz, Box saio, Box chunks, long moovStart, long total, List<Sample> output)
    {
        var sizesData = Body(moov, stsz); Require(sizesData, 12);
        uint defaultSize = U32(sizesData, 4); int count = Count(U32(sizesData, 8));
        if (count == 0) return;
        if (defaultSize == 0) Require(sizesData, checked(12 + count * 4));
        var sizes = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint size = defaultSize == 0 ? U32(sizesData, 12 + i * 4) : defaultSize;
            if (size is 0 or > 64 * 1024 * 1024) throw new InvalidDataException("AiDj sample size invalid."); sizes[i] = (int)size;
        }
        var chunkData = Body(moov, chunks); Require(chunkData, 8);
        int chunkCount = Count(U32(chunkData, 4)), width = chunks.Type == "co64" ? 8 : 4;
        Require(chunkData, checked(8 + chunkCount * width));
        var offsets = new long[chunkCount];
        for (int i = 0; i < chunkCount; i++) offsets[i] = width == 4 ? U32(chunkData, 8 + i * 4) : SignedOffset(U64(chunkData, 8 + i * 8));
        var mapping = Body(moov, stsc); Require(mapping, 8); int entries = Count(U32(mapping, 4)); Require(mapping, checked(8 + entries * 12));
        if (entries == 0) throw new InvalidDataException("AiDj empty sample-to-chunk table.");
        var perChunk = new int[chunkCount]; int previous = 0;
        for (int i = 0; i < entries; i++)
        {
            int first = Count(U32(mapping, 8 + i * 12)); int per = Count(U32(mapping, 12 + i * 12));
            int next = i + 1 < entries ? Count(U32(mapping, 20 + i * 12)) : chunkCount + 1;
            if (first < 1 || (i == 0 && first != 1) || first <= previous || next <= first || next > chunkCount + 1 || per == 0)
                throw new InvalidDataException("AiDj invalid sample-to-chunk mapping.");
            for (int chunk = first - 1; chunk < next - 1; chunk++) perChunk[chunk] = per;
            previous = first;
        }
        var auxSizes = Body(moov, saiz); Require(auxSizes, 4);
        int auxCursor = (U32(auxSizes, 0) & 1) != 0 ? 12 : 4; Require(auxSizes, auxCursor + 5);
        int defaultAux = auxSizes[auxCursor], auxCount = Count(U32(auxSizes, auxCursor + 1));
        if (auxCount != count) throw new InvalidDataException("AiDj CENC auxiliary sample count differs.");
        if (defaultAux == 0) Require(auxSizes, checked(auxCursor + 5 + count));
        // Source algorithm is full-sample AES-CTR with an 8-byte IV. Subsample encryption needs another parser.
        for (int i = 0; i < count; i++) if ((defaultAux == 0 ? auxSizes[auxCursor + 5 + i] : defaultAux) != 8)
            throw new NotSupportedException("AiDj CENC requires unsupported IV/subsample encryption metadata.");
        var auxOffsets = Body(moov, saio); Require(auxOffsets, 4);
        int auxOffsetCursor = (U32(auxOffsets, 0) & 1) != 0 ? 12 : 4, auxWidth = auxOffsets[0] == 1 ? 8 : 4;
        Require(auxOffsets, auxOffsetCursor + 4 + auxWidth);
        if (U32(auxOffsets, auxOffsetCursor) != 1 || auxOffsets[0] > 1) throw new NotSupportedException("AiDj CENC auxiliary offset layout unsupported.");
        long auxOffset = auxWidth == 4 ? U32(auxOffsets, auxOffsetCursor + 4) : SignedOffset(U64(auxOffsets, auxOffsetCursor + 4));
        long local = auxOffset - moovStart;
        if (local < 0 || local > moov.Length - (long)count * 8) throw new NotSupportedException("AiDj CENC auxiliary data must be inside moov for range playback.");
        int sample = 0;
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            long current = offsets[chunk];
            for (int i = 0; i < perChunk[chunk]; i++)
            {
                if (sample >= count) throw new InvalidDataException("AiDj sample-to-chunk count exceeds sample sizes.");
                int size = sizes[sample];
                if (current < 0 || current > total - size || current < moovStart + moov.Length && current + size > moovStart)
                    throw new InvalidDataException("AiDj encrypted sample bounds invalid.");
                var counter = new byte[16]; moov.AsSpan((int)local + sample * 8, 8).CopyTo(counter);
                output.Add(new(current, size, counter)); if (output.Count > MaximumSamples) throw new InvalidDataException("AiDj encrypted sample index too large.");
                current += size; sample++;
            }
        }
        if (sample != count) throw new InvalidDataException("AiDj chunk table does not cover every encrypted sample.");
    }
    internal void PatchRange(byte[] ciphertext, long start)
    {
        if (start < 0 || start > TotalLength - ciphertext.Length) throw new InvalidDataException("AiDj media range out of bounds.");
        long end = start + ciphertext.Length;
        int low = 0, high = Samples.Count;
        while (low < high) { int mid = low + (high - low) / 2; if (Samples[mid].Offset <= start) low = mid + 1; else high = mid; }
        for (int i = Math.Max(0, low - 1); i < Samples.Count; i++)
        {
            var sample = Samples[i]; if (sample.Offset >= end) break;
            long first = Math.Max(sample.Offset, start), last = Math.Min(sample.Offset + sample.Size, end); if (last <= first) continue;
            long skip = first - sample.Offset; int padding = (int)(skip & 15); var counter = (byte[])sample.Counter.Clone();
            ulong blocks = (ulong)(skip / 16);
            for (int n = 15; n >= 0 && blocks != 0; n--) { ulong sum = counter[n] + (blocks & 255); counter[n] = (byte)sum; blocks = (blocks >> 8) + (sum >> 8); }
            var input = new byte[checked(padding + (int)(last - first))]; ciphertext.AsSpan((int)(first - start), (int)(last - first)).CopyTo(input.AsSpan(padding));
            var plain = AiDjCrypto.AesCtr(_key, counter, input); plain.AsSpan(padding).CopyTo(ciphertext.AsSpan((int)(first - start)));
        }
        long moovEnd = MoovStart + _plainMoov.Length;
        if (start < moovEnd && end > MoovStart)
        {
            long first = Math.Max(start, MoovStart), last = Math.Min(end, moovEnd);
            _plainMoov.AsSpan((int)(first - MoovStart), (int)(last - first)).CopyTo(ciphertext.AsSpan((int)(first - start)));
        }
    }
    internal static byte[] DecryptFile(byte[] data, ReadOnlySpan<byte> key)
    {
        var box = Boxes(data, 0, data.Length).FirstOrDefault(b => b.Type == "moov") ?? throw new InvalidDataException("AiDj MP4 lacks moov.");
        var index = new AiDjCenc(data.AsSpan(box.Offset, box.Size).ToArray(), box.Offset, data.Length, key);
        var result = (byte[])data.Clone(); index.PatchRange(result, 0); return result;
    }
    private static byte[] RewriteMoov(byte[] moov)
    {
        var result = (byte[])moov.Clone();
        for (int i = 4; i + 4 <= result.Length; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(i - 4));
            if (size < 8 || size > result.Length - i + 4) continue;
            var type = result.AsSpan(i, 4);
            if (type.SequenceEqual("encv"u8)) "hvc1"u8.CopyTo(type);
            else if (type.SequenceEqual("enca"u8)) "mp4a"u8.CopyTo(type);
            else if (type.SequenceEqual("sinf"u8)) { "free"u8.CopyTo(type); result.AsSpan(i + 4, (int)size - 8).Clear(); i += (int)size - 5; }
        }
        return result;
    }
    private static List<Box> Boxes(byte[] data, int start, int end)
    {
        var result = new List<Box>(); int cursor = start;
        while (cursor < end)
        {
            if (end - cursor < 8) throw new InvalidDataException("AiDj truncated MP4 box header.");
            uint shortSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor)); int header = shortSize == 1 ? 16 : 8;
            if (end - cursor < header) throw new InvalidDataException("AiDj truncated extended MP4 box header.");
            ulong size = shortSize == 0 ? (ulong)(end - cursor) : shortSize == 1 ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(cursor + 8)) : shortSize;
            if (size < (ulong)header || size > (ulong)(end - cursor)) throw new InvalidDataException("AiDj MP4 box outside parent bounds.");
            result.Add(new(cursor, (int)size, header, Encoding.ASCII.GetString(data, cursor + 4, 4))); cursor += (int)size;
        }
        return result;
    }
    private static Box? Child(byte[] data, Box box, string type) => Boxes(data, box.Body, box.End).SingleOrDefault(b => b.Type == type);
    private static ReadOnlySpan<byte> Body(byte[] data, Box box) => data.AsSpan(box.Body, box.Size - box.Header);
    private static void Require(ReadOnlySpan<byte> data, int minimum) { if (data.Length < minimum) throw new InvalidDataException("AiDj truncated MP4 sample table."); }
    private static uint U32(ReadOnlySpan<byte> data, int offset) { Require(data, checked(offset + 4)); return BinaryPrimitives.ReadUInt32BigEndian(data[offset..]); }
    private static ulong U64(ReadOnlySpan<byte> data, int offset) { Require(data, checked(offset + 8)); return BinaryPrimitives.ReadUInt64BigEndian(data[offset..]); }
    private static int Count(uint count) => count <= MaximumSamples ? (int)count : throw new InvalidDataException("AiDj MP4 table too large.");
    private static long SignedOffset(ulong offset) => offset <= long.MaxValue ? (long)offset : throw new InvalidDataException("AiDj MP4 offset too large.");
}
