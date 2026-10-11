package android.content;

import java.util.HashMap;
import java.util.Map;
import java.util.Set;

/**
 * Android {@code ContentValues} 最小子集（VodBox 自有实现，独立编写）。
 *
 * **真实键值容器**（不是空壳）：写入、覆盖、删除、键集与取值转换语义与 Android 一致，
 * {@code getAs*} 在无法转换时抛 {@link NumberFormatException}（Android 也是非受检异常）。
 * {@link android.database.sqlite.SQLiteDatabase#insert(String, String, ContentValues)} 等写路径直接消费它。
 *
 * 明确不实现（插件调用时以 {@code NoSuchMethodError} 明确失败）：Parcelable 相关的
 * {@code describeContents}/{@code writeToParcel}。
 */
public final class ContentValues {

    private final HashMap<String, Object> values;

    public ContentValues() {
        this.values = new HashMap<>();
    }

    public ContentValues(int size) {
        this.values = new HashMap<>(size, 1.0f);
    }

    public ContentValues(ContentValues from) {
        this.values = new HashMap<>(from.values);
    }

    @Override
    public boolean equals(Object object) {
        return object instanceof ContentValues other && values.equals(other.values);
    }

    @Override
    public int hashCode() {
        return values.hashCode();
    }

    // ---------- 写入 ----------

    public void put(String key, String value) {
        values.put(key, value);
    }

    public void put(String key, Byte value) {
        values.put(key, value);
    }

    public void put(String key, Short value) {
        values.put(key, value);
    }

    public void put(String key, Integer value) {
        values.put(key, value);
    }

    public void put(String key, Long value) {
        values.put(key, value);
    }

    public void put(String key, Float value) {
        values.put(key, value);
    }

    public void put(String key, Double value) {
        values.put(key, value);
    }

    public void put(String key, Boolean value) {
        values.put(key, value);
    }

    public void put(String key, byte[] value) {
        values.put(key, value);
    }

    /** 与 Android 一致：显式写入 null（区别于 {@link #remove(String)}）。 */
    public void putNull(String key) {
        values.put(key, null);
    }

    public void putAll(ContentValues other) {
        values.putAll(other.values);
    }

    public void remove(String key) {
        values.remove(key);
    }

    public void clear() {
        values.clear();
    }

    // ---------- 读取 ----------

    public boolean containsKey(String key) {
        return values.containsKey(key);
    }

    public int size() {
        return values.size();
    }

    public Set<String> keySet() {
        return values.keySet();
    }

    /** Android 契约：{@code valueSet} 里应包含 null（键存在但值为 null）。 */
    public Set<Map.Entry<String, Object>> valueSet() {
        return values.entrySet();
    }

    public Object get(String key) {
        return values.get(key);
    }

    public String getAsString(String key) {
        Object value = values.get(key);
        return value == null ? null : String.valueOf(value);
    }

    public Long getAsLong(String key) {
        Object value = values.get(key);
        if (value == null) return null;
        if (value instanceof Number number) return number.longValue();
        return Long.valueOf(String.valueOf(value).trim());
    }

    public Integer getAsInteger(String key) {
        Object value = values.get(key);
        if (value == null) return null;
        if (value instanceof Number number) return number.intValue();
        return Integer.valueOf(String.valueOf(value).trim());
    }

    public Short getAsShort(String key) {
        Object value = values.get(key);
        if (value == null) return null;
        if (value instanceof Number number) return number.shortValue();
        return Short.valueOf(String.valueOf(value).trim());
    }

    public Byte getAsByte(String key) {
        Object value = values.get(key);
        if (value == null) return null;
        if (value instanceof Number number) return number.byteValue();
        return Byte.valueOf(String.valueOf(value).trim());
    }

    public Float getAsFloat(String key) {
        Object value = values.get(key);
        if (value == null) return null;
        if (value instanceof Number number) return number.floatValue();
        return Float.valueOf(String.valueOf(value).trim());
    }

    public Double getAsDouble(String key) {
        Object value = values.get(key);
        if (value == null) return null;
        if (value instanceof Number number) return number.doubleValue();
        return Double.valueOf(String.valueOf(value).trim());
    }

    /** Android 契约：{@code Boolean} 直接返回；数字按「非 0 为 true」；字符串用 {@code Boolean.valueOf}。 */
    public Boolean getAsBoolean(String key) {
        Object value = values.get(key);
        if (value == null) return null;
        if (value instanceof Boolean flag) return flag;
        if (value instanceof Number number) return number.doubleValue() != 0d;
        return Boolean.valueOf(String.valueOf(value));
    }

    public byte[] getAsByteArray(String key) {
        Object value = values.get(key);
        return value instanceof byte[] bytes ? bytes : null;
    }

    @Override
    public String toString() {
        return values.toString();
    }
}
