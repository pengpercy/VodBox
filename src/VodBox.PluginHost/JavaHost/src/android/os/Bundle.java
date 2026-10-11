package android.os;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Android {@code Bundle} 最小子集（VodBox 自有实现）。
 *
 * {@link Message#setData}/{@link Message#getData} 以及大量插件用它在回调间传值。
 * 这里按「有序字符串键 → 基础类型值」实现，缺少的键返回默认值/null——**不伪造**「键存在」。
 * 不支持 Parcelable/Serializable（未建模），插件若需要它们会在链接阶段明确失败。
 */
public final class Bundle {

    private final Map<String, Object> map = new LinkedHashMap<>();

    public Bundle() {
    }

    public Bundle(Bundle other) {
        if (other != null) map.putAll(other.map);
    }

    public int size() { return map.size(); }

    public boolean isEmpty() { return map.isEmpty(); }

    public void clear() { map.clear(); }

    public boolean containsKey(String key) { return map.containsKey(key); }

    public Set<String> keySet() { return map.keySet(); }

    public Object get(String key) { return map.get(key); }

    public void remove(String key) { map.remove(key); }

    public void putAll(Bundle other) { if (other != null) map.putAll(other.map); }

    public void putString(String key, String value) { map.put(key, value); }

    public String getString(String key) { return value(key, String.class); }

    public String getString(String key, String defaultValue) {
        String value = getString(key);
        return value == null ? defaultValue : value;
    }

    public void putInt(String key, int value) { map.put(key, Integer.valueOf(value)); }

    public int getInt(String key) { return getInt(key, 0); }

    public int getInt(String key, int defaultValue) {
        Integer value = value(key, Integer.class);
        return value == null ? defaultValue : value;
    }

    public void putLong(String key, long value) { map.put(key, Long.valueOf(value)); }

    public long getLong(String key) { return getLong(key, 0L); }

    public long getLong(String key, long defaultValue) {
        Long value = value(key, Long.class);
        return value == null ? defaultValue : value;
    }

    public void putFloat(String key, float value) { map.put(key, Float.valueOf(value)); }

    public float getFloat(String key) { return getFloat(key, 0f); }

    public float getFloat(String key, float defaultValue) {
        Float value = value(key, Float.class);
        return value == null ? defaultValue : value;
    }

    public void putDouble(String key, double value) { map.put(key, Double.valueOf(value)); }

    public double getDouble(String key) { return getDouble(key, 0d); }

    public double getDouble(String key, double defaultValue) {
        Double value = value(key, Double.class);
        return value == null ? defaultValue : value;
    }

    public void putBoolean(String key, boolean value) { map.put(key, Boolean.valueOf(value)); }

    public boolean getBoolean(String key) { return getBoolean(key, false); }

    public boolean getBoolean(String key, boolean defaultValue) {
        Boolean value = value(key, Boolean.class);
        return value == null ? defaultValue : value;
    }

    public void putByteArray(String key, byte[] value) { map.put(key, value == null ? null : value.clone()); }

    public byte[] getByteArray(String key) {
        byte[] value = value(key, byte[].class);
        return value == null ? null : value.clone();
    }

    public void putStringArray(String key, String[] value) { map.put(key, value == null ? null : value.clone()); }

    public String[] getStringArray(String key) {
        String[] value = value(key, String[].class);
        return value == null ? null : value.clone();
    }

    @SuppressWarnings("unchecked")
    private <T> T value(String key, Class<T> type) {
        Object value = map.get(key);
        return type.isInstance(value) ? (T) value : null;
    }

    @Override
    public String toString() {
        return "Bundle" + map;
    }
}
