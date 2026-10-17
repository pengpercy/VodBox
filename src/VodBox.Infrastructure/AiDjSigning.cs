using System.Buffers.Binary;
using System.Numerics;

namespace VodBox.Infrastructure;

/// <summary>Pure C# Medusa hash primitives from AiDj's embedded Python; no interpreter.</summary>
internal static partial class AiDjSigning
{
    internal static byte[] Sm3(ReadOnlySpan<byte> input)
    {
        int length = checked((input.Length + 9 + 63) / 64 * 64);
        var data = new byte[length]; input.CopyTo(data); data[input.Length] = 128;
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(length - 8), (ulong)input.Length * 8);
        uint[] state = [0x7380166f, 0x4914b2b9, 0x172442d7, 0xda8a0600, 0xa96f30bc, 0x163138aa, 0xe38dee4d, 0xb0fb0e4e];
        unchecked
        {
            for (int offset = 0; offset < length; offset += 64)
            {
                var w = new uint[68];
                for (int j = 0; j < 16; j++) w[j] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + j * 4));
                for (int j = 16; j < 68; j++)
                {
                    uint x = w[j - 16] ^ w[j - 9] ^ Rol(w[j - 3], 15);
                    w[j] = x ^ Rol(x, 15) ^ Rol(x, 23) ^ Rol(w[j - 13], 7) ^ w[j - 6];
                }
                uint a = state[0], b = state[1], c = state[2], d = state[3], e = state[4], f = state[5], g = state[6], h = state[7];
                for (int j = 0; j < 64; j++)
                {
                    uint tj = j < 16 ? 0x79cc4519u : 0x7a879d8au;
                    uint ss1 = Rol(Rol(a, 12) + e + Rol(tj, j % 32), 7), ss2 = ss1 ^ Rol(a, 12);
                    uint ff = j < 16 ? a ^ b ^ c : (a & b) | (a & c) | (b & c);
                    uint gg = j < 16 ? e ^ f ^ g : (e & f) | (~e & g);
                    uint tt1 = ff + d + ss2 + (w[j] ^ w[j + 4]);
                    uint tt2 = gg + h + ss1 + w[j];
                    (d, c, b, a) = (c, Rol(b, 9), a, tt1);
                    (h, g, f, e) = (g, Rol(f, 19), e, tt2 ^ Rol(tt2, 9) ^ Rol(tt2, 17));
                }
                uint[] final = [a, b, c, d, e, f, g, h];
                for (int i = 0; i < 8; i++) state[i] ^= final[i];
            }
        }
        var result = new byte[32]; for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(i * 4), state[i]);
        return result;
    }

    internal static (byte[] Key, byte[] Seed) KeyHash(ReadOnlySpan<byte> key, uint random)
    {
        if (key.Length != 32) throw new InvalidDataException("AiDj Medusa key length invalid.");
        var data = new byte[68]; key.CopyTo(data); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), random); key.CopyTo(data.AsSpan(36));
        uint d1 = (random >> 16) & 255, d2 = (d1 << 11) | (random >> 24); d2 = ~(d2 ^ (d1 >> 5) ^ d1);
        var seed = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(seed, d2);
        return (Sm3(data), seed);
    }

    internal static byte[] AesV3(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, uint timestamp)
    {
        if (key.Length != 16 || iv.Length != 16 || data.Length < 248) throw new InvalidDataException("AiDj AES_V3 input length invalid.");
        int word = (int)(timestamp & 3);
        uint[] initials = [0xca025ddc, 0x823dc546, 0xc9420583, 0xc298225f];
        uint initial = initials[word]; var expanded = new byte[48]; var box = SBox.AsSpan(word << 8, 256).ToArray();
        for (int i = 0; i < 16; i++) expanded[i] = (byte)((initial >> ((i & 3) * 8)) ^ key[i]);
        for (int i = 4, rounds = 8; i < 12; i++, rounds += 2)
        {
            int idx = 4 * (i - 1); byte k0 = expanded[idx], k1 = expanded[idx + 1], k2 = expanded[idx + 2], k3 = expanded[idx + 3];
            if ((i & 3) == 0) (k0, k1, k2, k3) = ((byte)((initial >> (rounds & 24)) ^ box[k1]), box[k2], box[k3], box[k0]);
            expanded[idx + 4] = (byte)(k0 ^ expanded[idx - 12]); expanded[idx + 5] = (byte)(k1 ^ expanded[idx - 11]);
            expanded[idx + 6] = (byte)(k2 ^ expanded[idx - 10]); expanded[idx + 7] = (byte)(k3 ^ expanded[idx - 9]);
        }
        int[][] cons = [[1, 0, 2, 3], [2, 0, 3, 1], [0, 1, 3, 2], [1, 0, 2, 3]];
        int[][] orders = [[0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3], [0, 9, 14, 15, 4, 13, 2, 7, 8, 1, 6, 3, 12, 5, 10, 11], [0, 9, 14, 7, 4, 13, 2, 11, 8, 1, 6, 3, 12, 5, 10, 15], [0, 9, 14, 11, 4, 13, 2, 7, 8, 1, 6, 15, 12, 5, 10, 3]];
        var con = cons[word]; var order = orders[word];
        byte[] EncryptBlock(byte[] input)
        {
            var state = (byte[])input.Clone();
            void Add(int offset, bool permute)
            { for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) state[i * 4 + j] ^= expanded[offset + i * 4 + (permute ? con[j] : j)]; }
            Add(0, true);
            for (int round = 1; round < 3; round++)
            {
                var previous = (byte[])state.Clone();
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) state[i * 4 + j] = box[previous[con[i] * 4 + j]];
                previous = (byte[])state.Clone(); for (int i = 0; i < 16; i++) state[i] = previous[order[i]];
                if (round == 1)
                {
                    previous = (byte[])state.Clone();
                    for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) state[i * 4 + j] = previous[i * 4 + con[j]];
                    for (int col = 0; col < 4; col++)
                    {
                        int t = state[col] ^ state[4 + col] ^ state[8 + col] ^ state[12 + col], u = state[col];
                        state[col] ^= (byte)(t ^ Xtime(state[col] ^ state[4 + col]));
                        state[4 + col] ^= (byte)(t ^ Xtime(state[4 + col] ^ state[8 + col]));
                        state[8 + col] ^= (byte)(t ^ Xtime(state[8 + col] ^ state[12 + col]));
                        state[12 + col] ^= (byte)(t ^ Xtime(state[12 + col] ^ u));
                    }
                }
                Add(round * 16, true);
            }
            Add(16, false); return state;
        }
        var plain = new byte[32];
        for (int i = 0; i < 31; i++)
        {
            int idx = i * 8;
            plain[i] = unchecked((byte)(((data[idx] >> 4) & 2) | (data[idx + 1] & 64) | ((data[idx + 2] >> 2) & 1) | ((data[idx + 3] << 3) & -128) | ((data[idx + 4] >> 1) & 4) | ((data[idx + 5] << 3) & 16) | ((data[idx + 6] << 5) & 32) | ((data[idx + 7] >> 4) & 8)));
        }
        plain[31] = 1;
        var derived = new byte[32]; var previousBlock = iv.ToArray();
        for (int offset = 0; offset < 32; offset += 16)
        {
            var block = plain.AsSpan(offset, 16).ToArray(); for (int i = 0; i < 16; i++) block[i] ^= previousBlock[i];
            previousBlock = EncryptBlock(block); previousBlock.CopyTo(derived, offset);
        }
        var result = new byte[data.Length + 1]; result[0] = derived[^1]; data.CopyTo(result.AsSpan(1));
        for (int i = 0; i < 31; i++)
        {
            int idx = 1 + i * 8, kk = derived[i];
            result[idx] = (byte)((result[idx] & -33) | ((kk << 4) & 32));
            result[idx + 1] = (byte)((result[idx + 1] & -65) | (kk & 64));
            result[idx + 2] = (byte)((result[idx + 2] & -5) | ((kk * 4) & 4));
            result[idx + 3] = (byte)((result[idx + 3] & -17) | ((kk >> 3) & 16));
            result[idx + 4] = (byte)((result[idx + 4] & -9) | ((kk + kk) & 8));
            result[idx + 5] = (byte)((result[idx + 5] & -3) | ((kk >> 3) & 2));
            result[idx + 6] = (byte)((result[idx + 6] & -2) | ((kk >> 5) & 1));
            result[idx + 7] = (byte)((result[idx + 7] & 127) | ((kk << 4) & 128));
        }
        return result;
    }
    private static int Xtime(int a) => (a & 128) != 0 ? ((a << 1) ^ 27) & 255 : a << 1;
    private static uint Rol(uint x, int bits) => BitOperations.RotateLeft(x, bits);
    private static uint Ror(uint x, int bits) => BitOperations.RotateRight(x, bits);

    internal static byte[] HashF13(ReadOnlySpan<byte> querySm3, ReadOnlySpan<byte> bodyMd5, uint timestamp)
    {
        if (querySm3.Length != 32 || bodyMd5.Length != 16) throw new InvalidDataException("AiDj hash_f13 input length invalid.");
        var ts = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(ts, timestamp);
        uint iv = Iv(Iv(Iv(0x20230928, querySm3), bodyMd5), ts);
        int index = (int)(((iv & 15) * 171) >> 9), branch = (int)((iv & 15) - ((uint)index * 3 & 255));
        return branch switch { 0 => Branch0(index, timestamp, querySm3, bodyMd5, ts), 1 => Branch1(index, timestamp, querySm3, bodyMd5, ts), 2 => Branch2(iv, timestamp, querySm3, bodyMd5, ts), _ => throw new InvalidDataException("AiDj hash branch invalid.") };
    }
    private static uint Iv(uint iv, ReadOnlySpan<byte> data)
    {
        unchecked { for (int i = 0; i < data.Length; i++) iv = (i & 1) == 0 ? (iv >> 4) ^ iv ^ (iv << 6) ^ data[i] : ~((iv >> 7) ^ iv ^ (data[i] | iv << 12)); }
        return iv;
    }
    private static uint SumMd5(ReadOnlySpan<byte> data)
    {
        uint sum = 0x20220420;
        unchecked
        {
            for (int i = 0; i < 12; i++)
            {
                uint temp;
                if ((i & 1) == 0) { temp = (sum >> 3) ^ sum; sum = data[i] ^ (sum << 7); }
                else { temp = (sum >> 5) ^ sum; sum = (data[i] | (sum << 11)) ^ 0xffffffff; }
                sum ^= temp;
            }
        }
        return (sum | 4) ^ 0x1000000;
    }
    private static byte[] WithChecksum(ReadOnlySpan<byte> data)
    { var result = new byte[20]; data[..16].CopyTo(result); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), SumMd5(data)); return result; }
    private static byte[] Branch0(int index, uint timestamp, ReadOnlySpan<byte> query, ReadOnlySpan<byte> body, ReadOnlySpan<byte> ts)
    {
        unchecked
        {
            uint[] bases = [0xc4a78580, 0xb3c0fd39, 0xc58c5686, 0xc9aa3ba7, 0xf5a7adf2, 0x963c2ed1];
            uint basis = bases[index], count2 = basis + timestamp;
            int count1 = (int)((count2 + 1) & 255), n0 = (int)((count2 + 2) & 7), count = (int)(count2 & 255);
            var constants = Branch0Table.Select(x => Ror(x, count1)).ToArray();
            byte[] seed = [0xfa, 0x45, 0x61, 0xd7]; var data = new byte[64];
            query.CopyTo(data); body.CopyTo(data.AsSpan(32)); ts.CopyTo(data.AsSpan(48));
            for (int i = 0; i < 4; i++) data[52 + i] = (byte)(((seed[i] << 8) | seed[i]) >> n0);
            data[62] = 1; data[63] = 0xa0;
            uint[] initial = [Ror(0x7aba4fc8, count), Ror(0x67166507, count), Ror(0x6403fa00, count), Ror(0x340f512f, count), Ror(984304912, count), Ror(3005047866, count), Ror(2874125293, count), Ror(2152413264, count)];
            var words = new uint[128];
            for (int i = 0; i < 16; i++) words[i] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 4));
            uint di0 = words[0];
            for (int i = 0; i < 112; i++)
            {
                uint di1 = words[i + 1], di14 = words[i + 14];
                uint r1 = Rol(di1, 14) ^ Rol(di1, 25) ^ (di1 >> 3), r2 = Rol(di14, 13) ^ Rol(di14, 15) ^ (di14 >> 10);
                words[16 + i] = di0 + words[i + 9] + r1 + r2; di0 = di1;
            }
            int[][] parameters = [[101, 5, 7, 6, 3, 2, 1, 0, 5, 4, 3], [96, 0, 6, 7, 5, 3, 2, 1, 5, 4, 4], [96, 7, 6, 2, 1, 4, 0, 5, 4, 3, 5], [99, 3, 6, 2, 4, 5, 1, 0, 0, 7, 6], [96, 0, 5, 6, 7, 3, 1, 2, 5, 4, 4], [100, 2, 0, 3, 5, 4, 6, 7, 2, 1, 5]];
            var p = parameters[index]; var d = (uint[])initial.Clone();
            for (int i = 0; i < p[0]; i++)
            {
                uint offset = basis + (uint)i, v0 = words[offset & 127];
                uint v1 = ((d[p[3]] ^ d[p[4]]) & d[p[1]]) ^ d[p[3]];
                uint v2 = Rol(d[p[1]], 26) ^ Rol(d[p[1]], 21) ^ Rol(d[p[1]], 7);
                uint v4 = v0 + v1 + v2 + constants[offset & 63] + d[p[5]];
                uint v5 = Rol(d[p[2]], 30) ^ Rol(d[p[2]], 19) ^ Rol(d[p[2]], 10);
                uint v6 = (d[p[2]] & d[p[6]]) | ((d[p[2]] | d[p[6]]) & d[p[7]]);
                uint old = d[p[9]];
                uint savedLast = d[7];
                for (int j = 7; j > 0; j--) d[j] = d[j - 1];
                d[0] = savedLast;
                d[p[10]] = v5 + v6 + v4; d[p[8]] = old + v4;
            }
            var ret = new byte[32];
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(ret.AsSpan(i * 4), d[i] + initial[i]);
            for (int i = 0; i < 16; i++) ret[i] ^= ret[i + 16];
            return WithChecksum(ret);
        }
    }
    private static byte[] Branch1(int index, uint timestamp, ReadOnlySpan<byte> query, ReadOnlySpan<byte> body, ReadOnlySpan<byte> ts)
    {
        unchecked
        {
            uint[] bases = [0x808a9c79, 0xf079807e, 0xbadf79c5, 0xa785d3ff, 0x82d8438c];
            uint basis = bases[index]; ulong value = BitOperations.RotateRight((ulong)timestamp, (int)((basis + timestamp) & 255));
            byte[] orders = [5, 7, 1, 2, 4, 0, 6, 3, 0, 5, 2, 4, 1, 3, 7, 6, 5, 7, 2, 4, 1, 6, 3, 0, 3, 0, 2, 4, 6, 7, 1, 5, 4, 5, 0, 3, 6, 2, 1, 7];
            var order = orders.AsSpan(index * 8, 8).ToArray();
            ulong[] rawConstants = [0x87aeea5dab37cd6b, 0x7ff48becb4f54087, 0xb0724c06706bbd5d, 0x1fe5dfb1143e328d, 0x1a2331d00af4f1f2, 0xcaff7131bb1e71ba, 0x33385e1042752218, 0xff01ed65d4a441fb, 0x0adb1ec8828c80e8, 0x62475d12f4e06fe7, 0x00bd0b238da4fe72];
            int shift = (int)((basis + timestamp + 1) & 63);
            var constants = rawConstants.Select(x => BitOperations.RotateRight(x, shift)).ToArray();
            var data = new byte[64]; query.CopyTo(data); body.CopyTo(data.AsSpan(32)); ts.CopyTo(data.AsSpan(48)); data[52] = 128;
            var source = new ulong[8]; for (int i = 0; i < 8; i++) source[i] = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(i * 8));
            var xor = Enumerable.Repeat(value, 8).ToArray();
            var swap = Swap(source, xor, constants, index, order, null);
            var second = new ulong[8]; second[7] = 416;
            xor = [swap[4], swap[3], swap[2], swap[1], swap[0], swap[7], swap[6], swap[5]];
            swap = Swap(second, xor, constants, index, order, swap);
            var ret = new byte[32];
            for (int i = 7, n = 0; i >= 0; i--, n += 4)
            { ulong x = swap[i]; ret[n] = (byte)(x >> 8); ret[n + 1] = (byte)(x >> 24); ret[n + 2] = (byte)(x >> 40); ret[n + 3] = (byte)(x >> 48); }
            for (int i = 0; i < 16; i++) ret[i] ^= ret[i + 16];
            return WithChecksum(ret);
        }
    }
    private static ulong[] Swap(ulong[] source, ulong[] xor, ulong[] constants, int tableIndex, byte[] order, ulong[]? type)
    {
        var da = new ulong[8]; var ha = (ulong[])xor.Clone();
        for (int i = 0; i < 8; i++) da[i] = source[order[i]] ^ xor[i];
        for (int round = 0; round < 10; round++)
        {
            var rr = new ulong[8]; for (int i = 0; i < 8; i++) rr[i] = R00(ha, i, 0, tableIndex);
            rr[7] ^= constants[round + 1];
            var old = (ulong[])da.Clone(); for (int i = 0; i < 8; i++) da[i] = R00(old, i, rr[i], tableIndex);
            ha = rr;
        }
        if (type is null)
        { for (int i = 0; i < 8; i++) source[i] ^= da[(8 - i) & 7] ^ xor[i]; }
        else
        {
            source[0] = da[7] ^ type[1]; source[1] = da[6] ^ type[2]; source[2] = da[5] ^ type[3]; source[3] = da[4] ^ type[4];
            source[4] = da[3] ^ type[5]; source[5] = da[2] ^ type[6]; source[6] = da[1] ^ type[7] ^ source[7]; source[7] = da[0] ^ type[0];
        }
        return source;
    }
    private static ulong R00(ulong[] source, int start, ulong constant, int tableIndex)
    {
        var table = Branch1Table.Value;
        ulong At(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan((tableIndex << 14) + offset, 8));
        ulong Word(int i) => source[(start + i) & 7];
        return constant ^ At((int)(8 * (Word(0) >> 56))) ^ At((int)((Word(1) >> 45) & 2040) + 2048) ^ At((int)((Word(2) >> 37) & 2040) + 4096) ^ At((int)((Word(3) >> 29) & 2040) + 6144) ^ At((int)((Word(4) >> 21) & 2040) + 8192) ^ At((int)((Word(5) >> 13) & 2040) + 10240) ^ At((int)(8 * ((Word(6) >> 8) & 255)) + 12288) ^ At((int)(8 * (Word(7) & 255)) + 14336);
    }
    private static byte[] Branch2(uint iv, uint timestamp, ReadOnlySpan<byte> query, ReadOnlySpan<byte> body, ReadOnlySpan<byte> ts)
    {
        unchecked
        {
            // Python's original `iv & 15 - 2` means iv & 13 (not (iv & 15) - 2).
            uint n0 = (iv & 13) * 86; int index = (int)(((n0 >> 15) & 255) + ((n0 >> 8) & 255));
            uint[] bases = [0x8980f29b, 0xeb549c7f, 0xb08726db, 0xd40cb5e6, 0xe8f559e4];
            uint count2 = bases[index] + timestamp; int count1 = (int)((count2 + 1) & 255), shift = (int)((count2 + 5) & 7);
            var data = new byte[64]; query.CopyTo(data); body.CopyTo(data.AsSpan(32)); ts.CopyTo(data.AsSpan(48));
            byte[] seed = [0x84, 0x96, 0x77, 0x9d, 0xd4, 0x15, 0x0b, 0xf8];
            for (int i = 0; i < 8; i++) data[52 + i] = (byte)(((seed[i] << 8) | seed[i]) >> shift);
            data[60] = 0xa0; data[61] = 1;
            var orders = Branch2Orders.AsSpan(index << 6, 64);
            uint[] start = [Ror(0x79e0f2fb, (int)count2), Ror(0xc8b52570, (int)count2), Ror(0xebc2f8cd, (int)count2), Ror(0x7c104d93, (int)count2)];
            int endShift = (int)((count2 + 6) & 255);
            uint[] end = [Ror(0x19be4866, endShift), Ror(0xe85986b4, endShift), Ror(0xe19b326e, endShift), Ror(0x71d1d7d4, endShift)];
            var words = new uint[16]; for (int i = 0; i < 16; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4));
            uint a = start[0], b = start[1], c = start[2], d = start[3];
            int[][] shifts = [[7, 12, 17, 22], [5, 9, 14, 20], [4, 11, 16, 23], [6, 10, 15, 21]];
            for (int i = 0; i < 64; i++)
            {
                int round = i >> 4; uint f = round switch { 0 => (b & c) | (~b & d), 1 => (b & d) | (c & ~d), 2 => b ^ c ^ d, _ => c ^ (b | ~d) };
                uint next = b + Rol(a + f + words[orders[i]] + Ror(Sv2[i], count1), shifts[round][i & 3]);
                (a, b, c, d) = (d, next, b, c);
            }
            uint[] final = [(start[0] + a) ^ end[0], (start[1] + b) ^ end[1], (start[2] + c) ^ end[2], (start[3] + d) ^ end[3]];
            var result = new byte[16]; for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), final[i]);
            return WithChecksum(result);
        }
    }
}
