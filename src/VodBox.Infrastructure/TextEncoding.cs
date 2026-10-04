using System.Text;

namespace VodBox.Infrastructure;

public static class TextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static void EnsureRegistered() { }
    public static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return StrictUtf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
    }
}
