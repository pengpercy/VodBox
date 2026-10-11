package android.util;

import java.nio.charset.StandardCharsets;

/**
 * Android {@code android.util.Base64} 最小子集（VodBox 自有实现，基于 JDK {@code java.util.Base64}）。
 *
 * 标志位取值与 Android 一致（它们是编译期内联常量，插件字节码里已写死这些值）：
 * {@link #DEFAULT}=0、{@link #NO_PADDING}=1、{@link #NO_WRAP}=2、{@link #CRLF}=4、{@link #URL_SAFE}=8、
 * {@link #NO_CLOSE}=16。
 *
 * 行为明确：
 * <ul>
 *   <li>编码：{@code URL_SAFE} 用 URL 字母表；{@code NO_PADDING} 省略 {@code =}；
 *       {@code NO_WRAP} 不换行；否则每 76 字符插入换行，{@code CRLF} 决定换行符是 {@code \r\n} 还是 {@code \n}
 *       （本实现**不追加结尾换行**）。</li>
 *   <li>解码：忽略空白字符（空格/换行/制表）；按需补齐 {@code =}；字母表由 {@code URL_SAFE} 决定；
 *       非法字符或非法长度抛 {@link IllegalArgumentException}（与 Android 一致）。</li>
 * </ul>
 */
public final class Base64 {

    public static final int DEFAULT = 0;
    public static final int NO_PADDING = 1;
    public static final int NO_WRAP = 2;
    public static final int CRLF = 4;
    public static final int URL_SAFE = 8;
    public static final int NO_CLOSE = 16;

    private static final int LINE_LENGTH = 76;

    private Base64() {}

    public static String encodeToString(byte[] input, int flags) {
        return new String(encode(input, flags), StandardCharsets.US_ASCII);
    }

    public static String encodeToString(byte[] input, int offset, int len, int flags) {
        byte[] slice = new byte[len];
        System.arraycopy(input, offset, slice, 0, len);
        return encodeToString(slice, flags);
    }

    public static byte[] encode(byte[] input, int flags) {
        if (input == null) throw new IllegalArgumentException("input == null!");
        java.util.Base64.Encoder encoder = ((flags & URL_SAFE) != 0)
                ? java.util.Base64.getUrlEncoder()
                : java.util.Base64.getEncoder();
        if ((flags & NO_PADDING) != 0) encoder = encoder.withoutPadding();
        String encoded = encoder.encodeToString(input);
        if ((flags & NO_WRAP) != 0) return encoded.getBytes(StandardCharsets.US_ASCII);
        String separator = ((flags & CRLF) != 0) ? "\r\n" : "\n";
        StringBuilder wrapped = new StringBuilder(encoded.length() + encoded.length() / LINE_LENGTH + 2);
        for (int i = 0; i < encoded.length(); i += LINE_LENGTH) {
            if (i > 0) wrapped.append(separator);
            wrapped.append(encoded, i, Math.min(i + LINE_LENGTH, encoded.length()));
        }
        return wrapped.toString().getBytes(StandardCharsets.US_ASCII);
    }

    public static byte[] decode(String str, int flags) {
        if (str == null) throw new IllegalArgumentException("input == null!");
        return decode(str.getBytes(StandardCharsets.US_ASCII), flags);
    }

    public static byte[] decode(byte[] input, int flags) {
        if (input == null) throw new IllegalArgumentException("input == null!");
        StringBuilder compact = new StringBuilder(input.length);
        for (byte value : input) {
            char c = (char) (value & 0xFF);
            if (c == ' ' || c == '\n' || c == '\r' || c == '\t') continue;
            compact.append(c);
        }
        int remainder = compact.length() % 4;
        if (remainder == 2) compact.append("==");
        else if (remainder == 3) compact.append('=');
        // remainder == 1 非法：交给解码器抛 IllegalArgumentException。
        try {
            java.util.Base64.Decoder decoder = ((flags & URL_SAFE) != 0)
                    ? java.util.Base64.getUrlDecoder()
                    : java.util.Base64.getDecoder();
            return decoder.decode(compact.toString());
        } catch (IllegalArgumentException invalid) {
            throw new IllegalArgumentException("bad base-64");
        }
    }
}
