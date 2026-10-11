package android.text;

import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.StringJoiner;
import java.util.regex.Pattern;

/**
 * Android {@code android.text.TextUtils} 最小子集（VodBox 自有实现，独立编写，未复制任何上游/GPL 源码）。
 *
 * 这里的方法都是**纯字符串语义**，因此可以完整实现、行为确定，也是普通 CatVod 插件用得最多的一批
 * （判断空串、拼接口、切分、数字判断、查找）。语义与 Android 一致的地方都在注释里点明：
 * <ul>
 *   <li>{@link #isEmpty(CharSequence)} 把 {@code null} 与长度 0 都视为空；</li>
 *   <li>{@link #equals(CharSequence, CharSequence)} 是 {@code Object.equals} 的语义
 *       （同引用或内容相等），不引入排序规则；</li>
 *   <li>{@link #split(String, String)} / {@link #split(String, java.util.regex.Pattern)}
 *       与 Android 一致地使用 limit = -1，**保留尾部的空串**；</li>
 *   <li>{@link #join(CharSequence, Object[])} / {@link #join(CharSequence, Iterable)}
 *       对 {@code null} 元素写成 {@code "null"}（与 Android 的 {@code String.valueOf} 一致）。</li>
 * </ul>
 *
 * **明确不实现**（依赖 Android 的 Spannable/TextPaint/Region 体系，调用时会以
 * {@code NoSuchMethodError} 明确失败并被归类为 {@code unsupported_android}）：
 * {@code copySpansFrom}、{@code ellipsize}、{@code getOffsetBefore}/{@code getOffsetAfter}、
 * {@code getLayoutDirectionFromLocale}、{@code makeSafeForPresentation}。
 */
public final class TextUtils {

    private static final String[] EMPTY_STRING_ARRAY = new String[0];

    private TextUtils() {
    }

    public static boolean isEmpty(CharSequence text) {
        return text == null || text.length() == 0;
    }

    /** Android 契约：同引用或内容相等；两者都为 {@code null} 视为相等。 */
    public static boolean equals(CharSequence first, CharSequence second) {
        if (first == second) return true;
        if (first == null || second == null) return false;
        int length = first.length();
        if (length != second.length()) return false;
        if (first instanceof String && second instanceof String) return first.equals(second);
        for (int i = 0; i < length; i++) {
            if (first.charAt(i) != second.charAt(i)) return false;
        }
        return true;
    }

    public static int getTrimmedLength(CharSequence text) {
        if (text == null) return 0;
        int end = text.length();
        int start = 0;
        while (start < end && Character.isWhitespace(text.charAt(start))) start++;
        while (end > start && Character.isWhitespace(text.charAt(end - 1))) end--;
        return end - start;
    }

    /** Android 契约：分隔符为 {@code null} 时等价于空前缀（元素直接相接）。 */
    public static String join(CharSequence delimiter, Object[] tokens) {
        if (tokens == null) return "";
        StringJoiner joiner = new StringJoiner(delimiter == null ? "" : delimiter);
        for (Object token : tokens) joiner.add(String.valueOf(token));
        return joiner.toString();
    }

    public static String join(CharSequence delimiter, Iterable<?> tokens) {
        if (tokens == null) return "";
        StringJoiner joiner = new StringJoiner(delimiter == null ? "" : delimiter);
        for (Object token : tokens) joiner.add(String.valueOf(token));
        return joiner.toString();
    }

    /** Android 契约：空串返回空数组；否则按正则切分并**保留尾部空串**。 */
    public static String[] split(String text, String expression) {
        if (text == null || text.length() == 0) return EMPTY_STRING_ARRAY;
        return text.split(expression, -1);
    }

    public static String[] split(String text, Pattern pattern) {
        if (text == null || text.length() == 0) return EMPTY_STRING_ARRAY;
        return pattern.split(text, -1);
    }

    /** Android 契约：全部是 0-9 时才为 {@code true}（空序列视为 {@code true}）。 */
    public static boolean isDigitsOnly(CharSequence text) {
        if (text == null) return true;
        for (int i = 0; i < text.length(); i++) {
            char current = text.charAt(i);
            if (current < '0' || current > '9') return false;
        }
        return true;
    }

    /** Android 契约：**任一**字符是可显示图形字符即 {@code true}（控制符/格式符/空白分隔符不算）。 */
    public static boolean isGraphic(CharSequence text) {
        if (text == null) return false;
        for (int i = 0; i < text.length(); i++) {
            int type = Character.getType(text.charAt(i));
            if (type != Character.CONTROL && type != Character.FORMAT && type != Character.SURROGATE
                    && type != Character.UNASSIGNED && type != Character.LINE_SEPARATOR
                    && type != Character.PARAGRAPH_SEPARATOR && type != Character.SPACE_SEPARATOR) {
                return true;
            }
        }
        return false;
    }

    public static CharSequence concat(CharSequence... text) {
        if (text == null || text.length == 0) return "";
        StringBuilder builder = new StringBuilder();
        for (CharSequence part : text) if (part != null) builder.append(part);
        return builder.toString();
    }

    public static int indexOf(CharSequence text, char ch) {
        return indexOf(text, ch, 0);
    }

    public static int indexOf(CharSequence text, char ch, int start) {
        if (text == null) return -1;
        for (int i = Math.max(start, 0); i < text.length(); i++) {
            if (text.charAt(i) == ch) return i;
        }
        return -1;
    }

    public static int indexOf(CharSequence text, CharSequence needle) {
        return indexOf(text, needle, 0);
    }

    public static int indexOf(CharSequence text, CharSequence needle, int start) {
        if (text == null || needle == null) return -1;
        int needleLength = needle.length();
        if (needleLength == 0) return Math.min(Math.max(start, 0), text.length());
        int from = Math.max(start, 0);
        for (int i = from; i + needleLength <= text.length(); i++) {
            if (regionMatches(text, i, needle, 0, needleLength)) return i;
        }
        return -1;
    }

    public static int lastIndexOf(CharSequence text, char ch) {
        return lastIndexOf(text, ch, text == null ? -1 : text.length());
    }

    public static int lastIndexOf(CharSequence text, char ch, int last) {
        if (text == null) return -1;
        for (int i = Math.min(last, text.length() - 1); i >= 0; i--) {
            if (text.charAt(i) == ch) return i;
        }
        return -1;
    }

    public static int lastIndexOf(CharSequence text, CharSequence needle) {
        return lastIndexOf(text, needle, text == null ? -1 : text.length());
    }

    public static int lastIndexOf(CharSequence text, CharSequence needle, int last) {
        if (text == null || needle == null) return -1;
        int needleLength = needle.length();
        int limit = Math.min(last, text.length());
        if (needleLength == 0) return Math.max(limit, 0);
        for (int i = limit - needleLength; i >= 0; i--) {
            if (regionMatches(text, i, needle, 0, needleLength)) return i;
        }
        return -1;
    }

    /** Android 契约：任一字符不同即 {@code false}；长度不足时按越界返回 {@code false}。 */
    public static boolean regionMatches(CharSequence one, int toffset, CharSequence two, int ooffset, int len) {
        if (one == null || two == null) return false;
        if (len < 0 || toffset < 0 || ooffset < 0) return false;
        if (toffset + len > one.length() || ooffset + len > two.length()) return false;
        for (int i = 0; i < len; i++) {
            if (one.charAt(toffset + i) != two.charAt(ooffset + i)) return false;
        }
        return true;
    }

    /** Android 契约：返回 {@code [start, end)} 的逆序副本。 */
    public static CharSequence getReverse(CharSequence source, int start, int end) {
        if (source == null) return "";
        char[] reversed = new char[Math.max(0, end - start)];
        for (int i = start; i < end; i++) reversed[end - i - 1] = source.charAt(i);
        return new String(reversed);
    }

    /** Android 的 {@code TextUtils.StringSplitter}：可复用的单分隔符切分器接口。 */
    public interface StringSplitter extends Iterable<String> {

        void setString(String string);
    }

    /**
     * Android {@code TextUtils.SimpleStringSplitter} 最小子集（VodBox 自有实现）。
     *
     * 真实语义：按**单个字符**分隔符切分；连续分隔符产生空串；**不**保留尾部空串
     * （与 Android 的 {@code SimpleStringSplitter} 一致）。可复用于多次 {@link #setString(String)}。
     */
    public static class SimpleStringSplitter implements StringSplitter, Iterator<String> {

        private final char delimiter;
        private String text = "";
        private int position;
        private int length;

        public SimpleStringSplitter(char delimiter) {
            this.delimiter = delimiter;
        }

        @Override
        public void setString(String string) {
            text = string == null ? "" : string;
            position = 0;
            length = text.length();
        }

        @Override
        public Iterator<String> iterator() {
            return this;
        }

        @Override
        public boolean hasNext() {
            return position < length;
        }

        @Override
        public String next() {
            if (position >= length) throw new NoSuchElementException();
            int end = text.indexOf(delimiter, position);
            if (end == -1) end = length;
            String token = text.substring(position, end);
            position = end + 1;
            return token;
        }

        @Override
        public void remove() {
            throw new UnsupportedOperationException();
        }
    }
}
