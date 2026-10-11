package android.content;

import java.util.Map;
import java.util.Set;

/**
 * Android {@code SharedPreferences} 最小子集（VodBox 自有实现）。
 * 具体持久化见 {@code dev.vodbox.jarhost.android.FileSharedPreferences}（文件型、Android XML 格式）。
 */
public interface SharedPreferences {

    Map<String, ?> getAll();

    String getString(String key, String defValue);

    Set<String> getStringSet(String key, Set<String> defValues);

    int getInt(String key, int defValue);

    long getLong(String key, long defValue);

    float getFloat(String key, float defValue);

    boolean getBoolean(String key, boolean defValue);

    boolean contains(String key);

    Editor edit();

    void registerOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener listener);

    void unregisterOnSharedPreferenceChangeListener(OnSharedPreferenceChangeListener listener);

    interface Editor {
        Editor putString(String key, String value);

        Editor putStringSet(String key, Set<String> values);

        Editor putInt(String key, int value);

        Editor putLong(String key, long value);

        Editor putFloat(String key, float value);

        Editor putBoolean(String key, boolean value);

        Editor remove(String key);

        Editor clear();

        /** 与 Android 一致：写入内存 + 同步落盘，返回是否成功。 */
        boolean commit();

        /** 与 Android 一致：立即写入内存；本宿主同步落盘（确定性、可离线测试），不返回结果。 */
        void apply();
    }

    interface OnSharedPreferenceChangeListener {
        void onSharedPreferenceChanged(SharedPreferences sharedPreferences, String key);
    }
}
