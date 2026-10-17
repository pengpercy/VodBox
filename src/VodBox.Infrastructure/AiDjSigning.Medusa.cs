using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VodBox.Infrastructure;

internal static partial class AiDjSigning
{
    internal sealed record Entropy(uint Timestamp, long Ticket, ushort GorgonRandom, uint HeliosRandom, uint ProtoRandom, uint HashRandom, uint XmRandom, int LaunchTime, int ProcessId)
    {
        internal static Entropy Create()
        {
            uint Random32() => BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
            var now = DateTimeOffset.UtcNow;
            return new((uint)now.ToUnixTimeSeconds(), now.ToUnixTimeMilliseconds(), (ushort)(Random32() & 65535), Random32(), Random32(), Random32(), Random32(), RandomNumberGenerator.GetInt32(100, 121), RandomNumberGenerator.GetInt32(10001, 12001));
        }
    }
    internal sealed record SignedRequest(string Url, IReadOnlyDictionary<string, string> Headers);

    /// <summary>The exact UTF-8 JSON body must be sent unchanged after signing.</summary>
    internal static SignedRequest Sign(string endpoint, IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<string, string> devices,
        ReadOnlySpan<byte> jsonBody, IReadOnlyDictionary<string, string> headers, Entropy? entropy = null)
    {
        var random = entropy ?? Entropy.Create();
        var query = new Dictionary<string, string>(parameters, StringComparer.Ordinal);
        foreach (var (key, value) in devices)
        {
            if (query.ContainsKey(key)) query[key] = value;
            else if (key == "device_id" && query.ContainsKey("did")) query["did"] = value;
        }
        query["ts"] = random.Timestamp.ToString(CultureInfo.InvariantCulture); query["_rticket"] = random.Ticket.ToString(CultureInfo.InvariantCulture);
        // Python urllib.parse.quote preserves ~; leave it literal after escaping.
        string encodedQuery = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key).Replace("%2A", "*", StringComparison.Ordinal) + "=" + Uri.EscapeDataString(pair.Value).Replace("%2A", "*", StringComparison.Ordinal)));
        var url = endpoint.Split('?')[0] + "?" + encodedQuery;
        var result = headers.ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => pair.Value, StringComparer.Ordinal);
        var big = new byte[4]; var little = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(big, random.Timestamp); BinaryPrimitives.WriteUInt32LittleEndian(little, random.Timestamp);
        result["x-ladon"] = Convert.ToBase64String(big); result["x-argus"] = Convert.ToBase64String(little);
        result["x-khronos"] = random.Timestamp.ToString(CultureInfo.InvariantCulture);
        result["x-gorgon"] = AiDjCrypto.Gorgon(jsonBody, encodedQuery, random.Timestamp, random.GorgonRandom);
        result["x-helios"] = AiDjCrypto.Helios(random.Timestamp, random.HeliosRandom);
        result["x-medusa"] = Medusa(url, query, devices, jsonBody, random);
        if (!jsonBody.IsEmpty) result["x-ss-stub"] = Convert.ToHexString(MD5.HashData(jsonBody));
        if (devices.TryGetValue("ua", out var ua) && ua.Length > 0) result["user-agent"] = ua;
        result["x-tt-dt"] = devices.GetValueOrDefault("x_tt_dt", "");
        return new(url, result);
    }
    internal static string Medusa(string url, IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<string, string> devices, ReadOnlySpan<byte> body, Entropy entropy)
    {
        string Device(string key, string parameter = "", string fallback = "") => devices.TryGetValue(key, out var value) ? value : parameter.Length > 0 && parameters.TryGetValue(parameter, out value) ? value : fallback;
        var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : "";
        var queryHash = Sm3(Encoding.UTF8.GetBytes(query)); var bodyHash = body.IsEmpty ? new byte[16] : MD5.HashData(body);
        var hash = HashF13(queryHash, bodyHash, entropy.Timestamp);
        var joined = Encoding.UTF8.GetBytes(query).Concat(bodyHash).Concat("none"u8.ToArray()).ToArray();
        var deviceId = Device("device_id", "device_id", parameters.GetValueOrDefault("did", ""));
        var version = Device("version_name", "version_name", "7.1.3.32");
        var model = Device("device_model", "device_type"); var brand = Device("device_brand", "device_brand");
        const long report = 1728388016635;
        var device = Proto(
            (1, 1L, 'i'), (2, 2L, 'i'), (3, "8662", 's'), (4, deviceId, 's'), (5, Device("device_sec_device_token"), 's'), (6, "!noperm!", 's'),
            (7, -888888L, 'i'), (8, -888888L, 'i'), (9, 3L, 'i'), (10, -888888L, 'i'), (11, "!notset!", 's'), (12, "Asia/Shanghai,8", 's'), (13, "zh_CN", 's'), (14, 4L, 'i'),
            (16, 255.24993896484375, 'f'), (17, 35.58599090576172, 'f'), (18, 3.467449188232422, 'f'), (19, 3.467449188232422, 'f'), (20, 255.1754913330078, 'f'), (21, 42.17544174194336, 'f'),
            (22, Device("os_version", "os_version"), 's'), (23, 41L, 'i'), (24, 36L, 'i'), (25, report, 'i'), (26, report, 'i'), (27, report, 'i'), (28, report + 2, 'i'),
            (29, -1L, 'i'), (30, model, 's'), (31, brand, 's'), (32, model, 's'), (33, model, 's'), (34, Device("device_manufacturer", "", brand), 's'), (35, brand, 's'), (36, brand, 's'), (38, 31L, 'i'));
        var env = Proto((1, (long)entropy.LaunchTime, 'i'), (2, 146331399L, 'i'), (3, 146331396L, 'i'), (5, 7L, 'i'), (6, "v04.06.04.03-bugfix", 's'), (7, (long)entropy.ProcessId, 'i'), (12, device, 'b'),
            (13, Proto((1, (long)entropy.Timestamp, 'i'), (2, -2L, 'i'), (4, 200L, 'i')), 'b'), (14, version, 's'));
        var proto = Proto((1, Convert.FromHexString("f7e85ffad7d7dc3bd62ac87057cf6118"), 'b'), (2, 3L, 'i'), (3, (long)entropy.ProtoRandom, 'i'), (4, "8662", 's'), (5, deviceId, 's'), (6, "1588093228", 's'), (7, version, 's'), (8, "v04.06.04-ml-android", 's'), (9, 67503104L, 'i'),
            (10, new byte[] { 64, 1, 0, 0, 0, 0, 0, 0 }, 'b'), (12, (long)entropy.Timestamp, 'i'), (13, hash, 'b'), (14, queryHash[..6], 'b'),
            (15, Proto((1, 111L, 'i'), (2, 10L, 'i'), (3, 694367L, 'i'), (5, 586952199L, 'i')), 'b'),
            (16, Device("sec_device_token"), 's'), (17, (long)entropy.Timestamp, 'i'), (19, Sm3(joined), 'b'), (20, "none", 's'), (21, 312L, 'i'), (23, env, 'b'),
            (24, "{\"cmr\":16777216,\"cmr2\":16777216,\"un_h\":1879194040,\"vpn\":0,\"kd\":0,\"fkd\":3672518972,\"pd\":-1872573247,\"dyn\":\"\",\"do\":0,\"tk\":true}", 's'));
        var (key, seed) = KeyHash(Convert.FromHexString("8ebdfa3806ecc5cee79423e6029ed82540bc2218bb7eaef71cb691f7aa8aa2f5"), entropy.HashRandom);
        var mixed = XmXor(proto, key);
        var prefix = new byte[8 + mixed.Length]; BinaryPrimitives.WriteUInt64LittleEndian(prefix, 320); mixed.CopyTo(prefix, 8);
        Array.Reverse(prefix); for (int i = 0; i < prefix.Length; i++) prefix[i] ^= seed[~i & 3];
        var hashRandom = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(hashRandom, entropy.HashRandom);
        uint check = ((uint)(queryHash[0] & 63) << 14) | 0x18000001 | ((uint)(hash[0] & 63) << 8);
        var packed = new byte[11 + prefix.Length]; packed[0] = 0x35; BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(1), entropy.XmRandom); BinaryPrimitives.WriteUInt32LittleEndian(packed.AsSpan(5), check);
        prefix.CopyTo(packed, 9); hashRandom.AsSpan(2, 2).CopyTo(packed.AsSpan(9 + prefix.Length));
        var encrypted = AesV3(packed, Convert.FromHexString("f1593376766ea98d34f31b057a9d5be4"), Convert.FromHexString("1fe109a4125283f418de9e051a969e12"), entropy.Timestamp);
        var versionBytes = Convert.FromHexString("03000000f7e85ffad7d7dc3bd62ac87057cf6118");
        var output = new byte[24 + encrypted.Length];
        for (int i = 0; i < 20; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i), BinaryPrimitives.ReadUInt32LittleEndian(versionBytes.AsSpan(i)) ^ entropy.Timestamp);
        hashRandom.AsSpan(0, 2).CopyTo(output.AsSpan(20)); BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(22), 256); encrypted.CopyTo(output, 24);
        return Convert.ToBase64String(output);
    }
    private static byte[] XmXor(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key)
    {
        static int Rotate8(int value, int bits) => ((value << bits) | (value >> (8 - bits))) & 255;
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            int position = (i * 4) & 28, d0 = key[position], d1 = key[position + 1];
            int d2 = (Rotate8(data[i], 4) + d0) ^ d1; d2 = Rotate8(~d2 & 255, 3); d2 = ((d2 + d1) & 255) ^ d0;
            result[^(i + 1)] = unchecked((byte)~d2);
        }
        int flag = result[^1] ^ result[^2], data0 = result[0];
        result[0] = unchecked((byte)(~flag + result[0])); result[1] = unchecked((byte)((result[0] ^ result[^1] ^ 254) + result[1]));
        result[2] = unchecked((byte)(result[2] + ((flag - data0) ^ Rotate8(result[1], 3) ^ 2)));
        for (int i = 0; i < result.Length - 4; i++) { int temp = Rotate8(result[i + 2], 3) ^ result[i + 1] ^ (i + 3); result[i + 3] = unchecked((byte)(~temp + result[i + 3])); }
        result[^1] ^= result[^2];
        int sum = 0; for (int i = 1; i < result.Length; i++) sum += result[i]; result[0] = unchecked((byte)((result[0] ^ result[1]) + sum));
        return result;
    }
    private static byte[] Proto(params (int Field, object Value, char Kind)[] fields)
    {
        using var output = new MemoryStream();
        void Varint(ulong value)
        { while (value > 127) { output.WriteByte((byte)((value & 127) | 128)); value >>= 7; } output.WriteByte((byte)value); }
        foreach (var (field, value, kind) in fields)
        {
            if (kind == 'i')
            {
                long integer = (long)value; if (integer == 0) continue;
                Varint((ulong)(field << 3)); Varint(integer >= 0 ? (ulong)integer << 1 : unchecked((ulong)((integer << 1) ^ -1L)));
            }
            else if (kind == 'f')
            {
                double number = (double)value; if (number == 0) continue;
                Varint((ulong)((field << 3) | 5)); var bytes = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(bytes, (float)number); output.Write(bytes);
            }
            else
            {
                var raw = kind == 's' ? Encoding.UTF8.GetBytes((string)value) : (byte[])value; if (raw.Length == 0) continue;
                Varint((ulong)((field << 3) | 2)); Varint((ulong)raw.Length); output.Write(raw);
            }
        }
        return output.ToArray();
    }
}
