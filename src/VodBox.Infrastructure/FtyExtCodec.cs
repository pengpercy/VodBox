using System.Text;

namespace VodBox.Infrastructure;

/// <summary>
/// FTY v8 ext codec, recovered from the native encrypt/decrypt pair (0x10d4c8/0x10dd9c).
/// The wire is Base64(RC4(UTF8(UTF16-code-unit + 2))). This is a compatibility codec,
/// not cryptographic protection. Call only for a known FTY protocol, never for arbitrary Base64.
/// </summary>
internal static class FtyExtCodec
{
    internal const int MaxCharacters = 65536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Decode(string encoded)
    {
        if (encoded.Length > MaxCharacters) throw new InvalidDataException("FTY ext 超过长度上限。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new InvalidDataException("FTY ext 不是有效的 Base64 密文。", error); }

        // Native DAT_00119198, 13 bytes after the initializer's XOR string decoding.
        ReadOnlySpan<byte> key = "fantaiying921"u8;
        Span<byte> state = stackalloc byte[256];
        for (int i = 0; i < state.Length; i++) state[i] = (byte)i;
        int j = 0;
        for (int i = 0; i < state.Length; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }
        int x = 0;
        j = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            x = (x + 1) & 255;
            j = (j + state[x]) & 255;
            (state[x], state[j]) = (state[j], state[x]);
            bytes[i] ^= state[(state[x] + state[j]) & 255];
        }

        string shifted;
        try { shifted = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("FTY ext 解码失败，插件可能使用了不同的加密版本。", error); }
        var chars = shifted.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            // Native mbrtowc truncates to jchar. Do not silently corrupt a non-BMP ext.
            if (char.IsSurrogate(chars[i])) throw new InvalidDataException("FTY ext 包含此版本不支持的 Unicode 字符。");
            chars[i] = unchecked((char)(chars[i] - 2));
        }
        return new string(chars);
    }

    internal static string JsonConfig(string? ext)
    {
        string text = (ext ?? "").Trim();
        if (text.Length == 0 || text.Length > MaxCharacters)
            throw new InvalidDataException("FTY ext 配置缺失或超过长度上限。");
        return text.StartsWith('{') ? text : Decode(text);
    }
}
