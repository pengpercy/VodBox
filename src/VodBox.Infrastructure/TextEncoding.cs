using System.Text;

namespace VodBox.Infrastructure;

/// <summary>
/// 站点响应文本解码：BOM 探测 + 严格 UTF-8 → GB18030 回退。
/// 替换 <see cref="DefaultHttp.Decode"/>（旧实现仅在 Windows 回退 GB18030，
/// macOS/Linux 上中文站点会乱码）。GB18030 由 CodePagesEncodingProvider 提供，
/// 故需 System.Text.Encoding.CodePages 包。
/// </summary>
public static class TextEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static TextEncoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>触发静态构造以注册代码页提供器（AOT 下显式调用一次更稳妥）。</summary>
    public static void EnsureRegistered() { }

    public static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 非法 UTF-8：中文站点常见 GB18030/GBK 页面
            return Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                .GetString(bytes);
        }
    }
}
