package dev.vodbox.jarhost;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * 极小的 JSON 读写实现，供宿主在无任何第三方依赖的情况下解析协议报文。
 * 只支持标准 JSON；解析结果为 Map&lt;String,Object&gt; / List&lt;Object&gt; / String / Long / Double / Boolean / null。
 *
 * 这是本仓库自有代码（不引入任何上游源码或未声明依赖）。
 */
final class Json {
    private final String text;
    private int index;

    private Json(String text) {
        this.text = text;
    }

    static Object parse(String text) {
        Json parser = new Json(text == null ? "" : text);
        parser.skipWhitespace();
        if (parser.index >= parser.text.length()) return null;
        Object value = parser.readValue();
        parser.skipWhitespace();
        return value;
    }

    @SuppressWarnings("unchecked")
    static Map<String, Object> parseObject(String text) {
        Object value = parse(text);
        return value instanceof Map ? (Map<String, Object>) value : new LinkedHashMap<>();
    }

    private Object readValue() {
        char c = text.charAt(index);
        switch (c) {
            case '{': return readObject();
            case '[': return readArray();
            case '"': return readString();
            case 't': expect("true"); return Boolean.TRUE;
            case 'f': expect("false"); return Boolean.FALSE;
            case 'n': expect("null"); return null;
            default: return readNumber();
        }
    }

    private Map<String, Object> readObject() {
        Map<String, Object> map = new LinkedHashMap<>();
        index++; // {
        skipWhitespace();
        if (peek() == '}') { index++; return map; }
        while (true) {
            skipWhitespace();
            String key = readString();
            skipWhitespace();
            if (text.charAt(index) != ':') throw new IllegalArgumentException("expected ':' at " + index);
            index++;
            skipWhitespace();
            map.put(key, readValue());
            skipWhitespace();
            char next = text.charAt(index++);
            if (next == '}') return map;
            if (next != ',') throw new IllegalArgumentException("expected ',' or '}' at " + (index - 1));
        }
    }

    private List<Object> readArray() {
        List<Object> list = new ArrayList<>();
        index++; // [
        skipWhitespace();
        if (peek() == ']') { index++; return list; }
        while (true) {
            skipWhitespace();
            list.add(readValue());
            skipWhitespace();
            char next = text.charAt(index++);
            if (next == ']') return list;
            if (next != ',') throw new IllegalArgumentException("expected ',' or ']' at " + (index - 1));
        }
    }

    private String readString() {
        if (text.charAt(index) != '"') throw new IllegalArgumentException("expected '\"' at " + index);
        index++;
        StringBuilder builder = new StringBuilder();
        while (true) {
            char c = text.charAt(index++);
            if (c == '"') return builder.toString();
            if (c != '\\') { builder.append(c); continue; }
            char escape = text.charAt(index++);
            switch (escape) {
                case '"': builder.append('"'); break;
                case '\\': builder.append('\\'); break;
                case '/': builder.append('/'); break;
                case 'b': builder.append('\b'); break;
                case 'f': builder.append('\f'); break;
                case 'n': builder.append('\n'); break;
                case 'r': builder.append('\r'); break;
                case 't': builder.append('\t'); break;
                case 'u':
                    builder.append((char) Integer.parseInt(text.substring(index, index + 4), 16));
                    index += 4;
                    break;
                default: throw new IllegalArgumentException("bad escape \\" + escape);
            }
        }
    }

    private Object readNumber() {
        int start = index;
        while (index < text.length() && "-+.eE0123456789".indexOf(text.charAt(index)) >= 0) index++;
        String number = text.substring(start, index);
        if (number.isEmpty()) throw new IllegalArgumentException("bad number at " + start);
        if (number.indexOf('.') >= 0 || number.indexOf('e') >= 0 || number.indexOf('E') >= 0) return Double.parseDouble(number);
        return Long.parseLong(number);
    }

    private void expect(String literal) {
        if (!text.startsWith(literal, index)) throw new IllegalArgumentException("expected " + literal + " at " + index);
        index += literal.length();
    }

    private char peek() {
        return index < text.length() ? text.charAt(index) : '\0';
    }

    private void skipWhitespace() {
        while (index < text.length() && Character.isWhitespace(text.charAt(index))) index++;
    }

    // ---------- 写出 ----------

    static String stringify(Object value) {
        StringBuilder builder = new StringBuilder();
        write(builder, value);
        return builder.toString();
    }

    private static void write(StringBuilder builder, Object value) {
        if (value == null) { builder.append("null"); return; }
        if (value instanceof String) { writeString(builder, (String) value); return; }
        if (value instanceof Boolean || value instanceof Long || value instanceof Integer) { builder.append(value); return; }
        if (value instanceof Double || value instanceof Float) { builder.append(value); return; }
        if (value instanceof Map) {
            builder.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> entry : ((Map<?, ?>) value).entrySet()) {
                if (!first) builder.append(',');
                first = false;
                writeString(builder, String.valueOf(entry.getKey()));
                builder.append(':');
                write(builder, entry.getValue());
            }
            builder.append('}');
            return;
        }
        if (value instanceof Iterable) {
            builder.append('[');
            boolean first = true;
            for (Object item : (Iterable<?>) value) {
                if (!first) builder.append(',');
                first = false;
                write(builder, item);
            }
            builder.append(']');
            return;
        }
        writeString(builder, String.valueOf(value));
    }

    private static void writeString(StringBuilder builder, String value) {
        builder.append('"');
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '"': builder.append("\\\""); break;
                case '\\': builder.append("\\\\"); break;
                case '\n': builder.append("\\n"); break;
                case '\r': builder.append("\\r"); break;
                case '\t': builder.append("\\t"); break;
                default:
                    if (c < 0x20) builder.append(String.format("\\u%04x", (int) c));
                    else builder.append(c);
            }
        }
        builder.append('"');
    }
}
