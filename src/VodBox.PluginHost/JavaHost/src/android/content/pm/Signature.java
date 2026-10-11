package android.content.pm;

import java.util.Arrays;

/**
 * Android {@code android.content.pm.Signature} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 真实值类型：字节↔十六进制字符串转换、相等性、哈希都与 Android 的**公开契约**一致
 * （{@code Signature(String)} 解析十六进制文本，{@code toCharsString()} 输出小写十六进制）。
 *
 * <b>重要边界</b>：本宿主**不会伪造**应用签名。{@code PackageManager.getPackageInfo(..., GET_SIGNATURES)}
 * 不返回任何签名（桌面进程没有 Android 签名证书），因此依赖签名做校验的 guard 会拿到 {@code null}
 * 并失败——这比给它们一个假的证书更诚实。
 *
 * 未实现（调用即 NoSuchMethodError）：Parcelable 的 {@code writeToParcel}/{@code CREATOR}。
 */
public class Signature {

    private final byte[] signature;

    public Signature(byte[] signature) {
        if (signature == null) throw new IllegalArgumentException("signature == null");
        this.signature = Arrays.copyOf(signature, signature.length);
    }

    /** Android 契约：{@code text} 是十六进制文本（长度必须为偶数）。 */
    public Signature(String text) {
        if (text == null) throw new IllegalArgumentException("text == null");
        int length = text.length();
        if (length % 2 != 0) throw new IllegalArgumentException("text size must be even");
        byte[] decoded = new byte[length / 2];
        for (int i = 0; i < decoded.length; i++) {
            int high = Character.digit(text.charAt(i * 2), 16);
            int low = Character.digit(text.charAt(i * 2 + 1), 16);
            if (high < 0 || low < 0) throw new IllegalArgumentException("invalid hex text");
            decoded[i] = (byte) ((high << 4) | low);
        }
        this.signature = decoded;
    }

    public byte[] toByteArray() {
        return Arrays.copyOf(signature, signature.length);
    }

    public char[] toChars() {
        return toCharsString().toCharArray();
    }

    /** 小写十六进制（与 Android 一致）。 */
    public String toCharsString() {
        StringBuilder text = new StringBuilder(signature.length * 2);
        for (byte value : signature) {
            text.append(Character.forDigit((value >> 4) & 0xF, 16));
            text.append(Character.forDigit(value & 0xF, 16));
        }
        return text.toString();
    }

    @Override
    public boolean equals(Object object) {
        return object instanceof Signature other && Arrays.equals(signature, other.signature);
    }

    @Override
    public int hashCode() {
        return Arrays.hashCode(signature);
    }

    @Override
    public String toString() {
        return "Signature[" + toCharsString() + "]";
    }
}
