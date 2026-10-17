using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace VodBox.Infrastructure;

/// <summary>
/// Native ports of the embedded AiDj Python's deterministic primitives.
/// This is not a complete signer: Medusa and its custom AES/SM3 branches remain separate work.
/// Inputs include timestamp/random values so the orchestrator can verify fixed vectors.
/// </summary>
internal static class AiDjCrypto
{
    internal static byte[] DeriveContentKey(string spade)
    {
        var raw = Decode64(spade);
        if (raw.Length < 3) throw new InvalidDataException("AiDj spade_a 太短。");
        int count = raw.Length - (raw[0] ^ raw[1] ^ raw[2]) + 47;
        if (count <= 0 || 1 + count > raw.Length) count = raw.Length - 1;
        if (count < 33) throw new InvalidDataException("AiDj spade_a 长度异常。");
        var value = raw.AsSpan(1, count).ToArray();
        int va = 85, vb = 246;
        for (int index = 0; index < count; index++)
        {
            int previous = (index & 1) != 0 ? va : vb;
            if ((index & 1) != 0) va = value[index]; else vb = value[index];
            value[index] = unchecked((byte)(-21 - BitOperations.PopCount((uint)index) + (previous ^ value[index])));
        }
        try { return Convert.FromHexString(Encoding.ASCII.GetString(value, 1, 32)); }
        catch (FormatException ex) { throw new InvalidDataException("AiDj spade_a 密钥材料无效。", ex); }
    }

    internal static string Gorgon(ReadOnlySpan<byte> body, string encodedQuery, uint timestamp, ushort random)
    {
        var data = new byte[20];
        MD5.HashData(Encoding.UTF8.GetBytes(encodedQuery)).AsSpan(0, 4).CopyTo(data);
        if (!body.IsEmpty) MD5.HashData(body).AsSpan(0, 4).CopyTo(data.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 67503104);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), timestamp);
        byte[] key = [0x4a, 320 & 0xff, 0x16, (byte)(random >> 8), 0x47, 0x6c, 1, (byte)(random & 255)];
        var table = Enumerable.Range(0, 256).ToArray();
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + table[i] + key[i % key.Length]) & 255;
            // The source intentionally copies S[j] into S[i] instead of swapping.
            table[i] = table[j];
        }
        int cursor = 0; j = 0;
        for (int index = 0; index < data.Length; index++)
        {
            cursor = (cursor + 1) & 255;
            int x = table[cursor]; j = (j + x) & 255; int y = table[j];
            // The original variant intentionally overwrites S[i] without swapping S[j].
            table[cursor] = y;
            data[index] ^= (byte)table[(y + y) & 255];
        }
        for (int index = 0; index < data.Length; index++)
        {
            int value = ((data[index] >> 4) | (data[index] << 4)) & 255;
            int following = index + 1 < data.Length ? data[index + 1] : data[0];
            data[index] = unchecked((byte)~(ReverseBits((byte)(following ^ value)) ^ 20));
        }
        var result = new byte[26]; result[0] = 0x84; result[1] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), random);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 320);
        data.CopyTo(result, 6);
        return Convert.ToHexString(result).ToLowerInvariant();
    }

    internal static string Helios(uint timestamp, uint random)
    {
        // Caller supplies nonzero randomness (Python generates it when its argument is zero).
        var seed = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(seed, random);
        "8662"u8.CopyTo(seed.AsSpan(4));
        var key = Encoding.ASCII.GetBytes(Convert.ToHexString(MD5.HashData(seed)).ToLowerInvariant());
        var table = new ulong[35]; table[0] = BinaryPrimitives.ReadUInt64LittleEndian(key);
        var queue = new Queue<ulong>();
        queue.Enqueue(BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(16)));
        queue.Enqueue(BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(24)));
        ulong first = table[0], second = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8));
        unchecked
        {
            for (int index = 0; index < 34; index++)
            {
                ulong value = (BitOperations.RotateRight(second, 8) + first) ^ (ulong)index;
                queue.Enqueue(value);
                value ^= BitOperations.RotateRight(first, 61);
                table[index + 1] = value;
                first = value; second = queue.Dequeue();
            }
            var raw = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "-1588093228-8662");
            int padding = 16 - raw.Length % 16;
            var padded = new byte[raw.Length + padding]; raw.CopyTo(padded, 0); padded.AsSpan(raw.Length).Fill((byte)padding);
            var output = new byte[4 + padded.Length]; BinaryPrimitives.WriteUInt32LittleEndian(output, random);
            for (int offset = 0; offset < padded.Length; offset += 16)
            {
                ulong left = BinaryPrimitives.ReadUInt64LittleEndian(padded.AsSpan(offset));
                ulong right = BinaryPrimitives.ReadUInt64LittleEndian(padded.AsSpan(offset + 8));
                for (int index = 0; index < 34; index++)
                {
                    right = table[index] ^ (left + BitOperations.RotateRight(right, 8));
                    left = right ^ BitOperations.RotateRight(left, 61);
                }
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(4 + offset), left);
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(12 + offset), right);
            }
            return Convert.ToBase64String(output);
        }
    }

    internal static byte[] AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> counter, ReadOnlySpan<byte> input)
    {
        if (key.Length != 16 || counter.Length != 16) throw new InvalidDataException("AiDj CTR 密钥或计数器长度无效。");
        using var aes = Aes.Create(); aes.Key = key.ToArray();
        var block = counter.ToArray(); var mask = new byte[16]; var output = new byte[input.Length];
        for (int offset = 0; offset < input.Length; offset += 16)
        {
            aes.EncryptEcb(block, mask, PaddingMode.None);
            int length = Math.Min(16, input.Length - offset);
            for (int i = 0; i < length; i++) output[offset + i] = (byte)(input[offset + i] ^ mask[i]);
            for (int i = block.Length - 1; i >= 0; i--) { block[i] = unchecked((byte)(block[i] + 1)); if (block[i] != 0) break; }
        }
        return output;
    }

    private static byte ReverseBits(byte value)
    {
        int result = 0; for (int i = 0; i < 8; i++) { result = (result << 1) | (value & 1); value >>= 1; }
        return (byte)result;
    }
    private static byte[] Decode64(string value)
    {
        value = value.Trim().Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=')); }
        catch (FormatException ex) { throw new InvalidDataException("AiDj Base64 数据无效。", ex); }
    }
}
