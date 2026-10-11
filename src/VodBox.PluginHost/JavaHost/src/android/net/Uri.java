package android.net;

import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/**
 * Android {@code Uri} 最小子集（VodBox 自有实现）。
 *
 * 覆盖插件常用路径：{@link #parse}、{@code getScheme/getHost/getPath/getQuery/getFragment}、
 * {@code getLastPathSegment/getPathSegments/getQueryParameter(s)/getQueryParameterNames}、
 * {@link #buildUpon()} + {@link Builder#appendQueryParameter}，以及 {@link #encode}/{@link #decode}。
 *
 * 行为明确：内部按「原串 + 惰性拆分」保存；{@code getPath}/{@code getQueryParameter} 返回**解码后**的值，
 * {@code getEncodedPath}/{@code getEncodedQuery} 返回原始编码值；{@link #toString()} 恒等于传入的原串。
 * 不含 Parcelable 建模（未建模的成员在插件链接阶段明确失败）。
 */
public abstract class Uri {

    private Uri() {
    }

    public abstract String getScheme();

    public abstract String getSchemeSpecificPart();

    public abstract String getEncodedSchemeSpecificPart();

    public abstract String getAuthority();

    public abstract String getUserInfo();

    public abstract String getHost();

    public abstract int getPort();

    public abstract String getPath();

    public abstract String getEncodedPath();

    public abstract String getQuery();

    public abstract String getEncodedQuery();

    public abstract String getFragment();

    public abstract String getLastPathSegment();

    public abstract List<String> getPathSegments();

    public abstract String getQueryParameter(String key);

    public abstract Set<String> getQueryParameterNames();

    public abstract List<String> getQueryParameters(String key);

    public abstract boolean isHierarchical();

    public abstract boolean isRelative();

    public abstract Builder buildUpon();

    @Override
    public abstract String toString();

    // ---------- 静态工厂与编解码 ----------

    public static Uri parse(String uriString) {
        if (uriString == null) throw new NullPointerException("uriString == null");
        return new Built(uriString);
    }

    public static Uri fromFile(java.io.File file) {
        if (file == null) throw new NullPointerException("file == null");
        return new Built("file://" + file.getAbsolutePath());
    }

    private static final String ALLOWED = "_-!.~'()*";

    public static String encode(String s) {
        return encode(s, null);
    }

    /** 额外允许 {@code allow} 中的字符不做转义；其余非字母数字按 UTF-8 百分号编码。 */
    public static String encode(String s, String allow) {
        if (s == null) return null;
        StringBuilder builder = new StringBuilder(s.length());
        byte[] bytes = s.getBytes(StandardCharsets.UTF_8);
        for (byte value : bytes) {
            int c = value & 0xFF;
            char ch = (char) c;
            if (isUnreserved(ch) || (allow != null && allow.indexOf(ch) >= 0)) {
                builder.append(ch);
            } else {
                builder.append('%').append(HEX[(c >> 4) & 0xF]).append(HEX[c & 0xF]);
            }
        }
        return builder.toString();
    }

    /** 百分号解码（UTF-8）；{@code +} 不当作空格（与 Android 一致）。 */
    public static String decode(String s) {
        if (s == null) return null;
        if (s.indexOf('%') < 0) return s;
        ByteArrayOutputStream out = new ByteArrayOutputStream(s.length());
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '%' && i + 2 < s.length()) {
                int hi = hex(s.charAt(i + 1));
                int lo = hex(s.charAt(i + 2));
                if (hi >= 0 && lo >= 0) {
                    out.write((hi << 4) | lo);
                    i += 2;
                    continue;
                }
            }
            byte[] raw = String.valueOf(c).getBytes(StandardCharsets.UTF_8);
            out.write(raw, 0, raw.length);
        }
        return new String(out.toByteArray(), StandardCharsets.UTF_8);
    }

    private static final char[] HEX = "0123456789ABCDEF".toCharArray();

    private static boolean isUnreserved(char c) {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || ALLOWED.indexOf(c) >= 0;
    }

    private static int hex(char c) {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    // ---------- 实现 ----------

    private static final class Built extends Uri {
        private final String original;
        private final String scheme;
        private final String authority;
        private final String path;
        private final String query;
        private final String fragment;

        Built(String original) {
            this.original = original;
            String rest = original;
            String fragment = null;
            int hash = rest.indexOf('#');
            if (hash >= 0) {
                fragment = rest.substring(hash + 1);
                rest = rest.substring(0, hash);
            }
            String scheme = null;
            int colon = schemeColon(rest);
            if (colon > 0) {
                scheme = rest.substring(0, colon);
                rest = rest.substring(colon + 1);
            }
            String authority = null;
            if (rest.startsWith("//")) {
                int end = rest.length();
                int slash = rest.indexOf('/', 2);
                int question = rest.indexOf('?', 2);
                if (slash >= 0) end = slash;
                if (question >= 0 && question < end) end = question;
                authority = rest.substring(2, end);
                rest = rest.substring(end);
            }
            String query = null;
            int question = rest.indexOf('?');
            if (question >= 0) {
                query = rest.substring(question + 1);
                rest = rest.substring(0, question);
            }
            this.scheme = scheme;
            this.authority = authority;
            this.path = rest;
            this.query = query;
            this.fragment = fragment;
        }

        private static int schemeColon(String value) {
            for (int i = 0; i < value.length(); i++) {
                char c = value.charAt(i);
                if (c == ':') return i == 0 || !isValidScheme(value.substring(0, i)) ? -1 : i;
                if (c == '/' || c == '?') return -1;
            }
            return -1;
        }

        private static boolean isValidScheme(String candidate) {
            char first = candidate.charAt(0);
            if (!Character.isLetter(first)) return false;
            for (int i = 1; i < candidate.length(); i++) {
                char c = candidate.charAt(i);
                if (!Character.isLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return false;
            }
            return true;
        }

        @Override public String getScheme() { return scheme; }

        @Override public String getEncodedSchemeSpecificPart() {
            StringBuilder builder = new StringBuilder();
            if (authority != null) builder.append("//").append(authority);
            if (path != null) builder.append(path);
            if (query != null) builder.append('?').append(query);
            return builder.toString();
        }

        @Override public String getSchemeSpecificPart() { return decode(getEncodedSchemeSpecificPart()); }

        @Override public String getAuthority() { return authority; }

        @Override public String getUserInfo() {
            if (authority == null) return null;
            int at = authority.indexOf('@');
            return at >= 0 ? authority.substring(0, at) : null;
        }

        @Override public String getHost() {
            if (authority == null) return null;
            String host = authority;
            int at = host.indexOf('@');
            if (at >= 0) host = host.substring(at + 1);
            if (host.startsWith("[")) {
                int close = host.indexOf(']');
                return close >= 0 ? host.substring(0, close + 1) : host;
            }
            int colon = host.indexOf(':');
            return colon >= 0 ? host.substring(0, colon) : host;
        }

        @Override public int getPort() {
            if (authority == null) return -1;
            int at = authority.indexOf('@');
            String host = at >= 0 ? authority.substring(at + 1) : authority;
            if (host.startsWith("[")) {
                int close = host.indexOf(']');
                if (close < 0) return -1;
                host = host.substring(close + 1);
            }
            int colon = host.lastIndexOf(':');
            if (colon < 0) return -1;
            try {
                return Integer.parseInt(host.substring(colon + 1));
            } catch (NumberFormatException invalid) {
                return -1;
            }
        }

        @Override public String getEncodedPath() { return path; }

        @Override public String getPath() { return path == null ? null : decode(path); }

        @Override public String getEncodedQuery() { return query; }

        @Override public String getQuery() { return query; }

        @Override public String getFragment() { return fragment; }

        @Override public String getLastPathSegment() {
            List<String> segments = getPathSegments();
            return segments.isEmpty() ? null : segments.get(segments.size() - 1);
        }

        @Override public List<String> getPathSegments() {
            List<String> segments = new ArrayList<>();
            if (path == null) return segments;
            for (String segment : path.split("/")) {
                if (!segment.isEmpty()) segments.add(decode(segment));
            }
            return segments;
        }

        @Override public Set<String> getQueryParameterNames() {
            Set<String> names = new LinkedHashSet<>();
            for (String pair : queryPairs()) names.add(decode(key(pair)));
            return Collections.unmodifiableSet(names);
        }

        @Override public String getQueryParameter(String key) {
            List<String> values = getQueryParameters(key);
            return values.isEmpty() ? null : values.get(0);
        }

        @Override public List<String> getQueryParameters(String key) {
            List<String> values = new ArrayList<>();
            if (key == null) throw new NullPointerException("key == null");
            for (String pair : queryPairs()) {
                if (key.equals(decode(key(pair)))) values.add(decode(value(pair)));
            }
            return values;
        }

        private String[] queryPairs() {
            if (query == null || query.isEmpty()) return new String[0];
            return query.split("&");
        }

        private static String key(String pair) {
            int eq = pair.indexOf('=');
            return eq >= 0 ? pair.substring(0, eq) : pair;
        }

        private static String value(String pair) {
            int eq = pair.indexOf('=');
            return eq >= 0 ? pair.substring(eq + 1) : "";
        }

        @Override public boolean isHierarchical() { return !isOpaque(); }

        private boolean isOpaque() {
            return scheme != null && !getEncodedSchemeSpecificPart().startsWith("/");
        }

        @Override public boolean isRelative() { return scheme == null; }

        @Override public Builder buildUpon() { return new Builder(this); }

        @Override public String toString() { return original; }

        @Override public boolean equals(Object other) {
            return other instanceof Uri && original.equals(other.toString());
        }

        @Override public int hashCode() { return original.hashCode(); }
    }

    /** 与 Android 同名的可变构造器。 */
    public static final class Builder {
        private String scheme;
        private String authority;
        private final StringBuilder path = new StringBuilder();
        private final StringBuilder query = new StringBuilder();
        private String fragment;

        public Builder() {
        }

        Builder(Uri uri) {
            scheme = uri.getScheme();
            authority = uri.getAuthority();
            if (uri.getEncodedPath() != null) path.append(uri.getEncodedPath());
            if (uri.getEncodedQuery() != null) query.append(uri.getEncodedQuery());
            fragment = uri.getFragment();
        }

        public Builder scheme(String value) {
            scheme = value;
            return this;
        }

        public Builder authority(String value) {
            authority = value;
            return this;
        }

        public Builder path(String value) {
            path.setLength(0);
            if (value != null) path.append(value);
            return this;
        }

        public Builder appendPath(String value) {
            if (value == null) return this;
            if (path.length() > 0 && path.charAt(path.length() - 1) != '/') path.append('/');
            path.append(value);
            return this;
        }

        public Builder query(String value) {
            query.setLength(0);
            if (value != null) query.append(value);
            return this;
        }

        public Builder appendQueryParameter(String key, String value) {
            if (key == null) throw new NullPointerException("key == null");
            if (query.length() > 0) query.append('&');
            query.append(encode(key)).append('=').append(value == null ? "" : encode(value));
            return this;
        }

        public Builder clearQuery() {
            query.setLength(0);
            return this;
        }

        public Builder fragment(String value) {
            fragment = value;
            return this;
        }

        public Uri build() {
            StringBuilder value = new StringBuilder();
            if (scheme != null) value.append(scheme).append(':');
            if (authority != null) value.append("//").append(authority);
            value.append(path);
            if (query.length() > 0) value.append('?').append(query);
            if (fragment != null) value.append('#').append(fragment);
            return new Built(value.toString());
        }
    }
}
