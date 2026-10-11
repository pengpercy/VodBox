package android.preference;

import android.content.Context;
import android.content.SharedPreferences;

/**
 * Android {@code android.preference.PreferenceManager} 最小子集（VodBox 自有实现）。
 *
 * 只保留**不依赖 XML 资源**的静态入口：默认偏好文件名与 Android 一致
 * （{@code <包名>_preferences}），因此插件写进去的值会真实落盘到该会话的
 * {@code shared_prefs/} 目录（见 {@code dev.vodbox.jarhost.android.FileSharedPreferences}）。
 *
 * 未实现（调用即 NoSuchMethodError → {@code unsupported_android}）：
 * {@code setDefaultValues}（需要解析 {@code PreferenceScreen} XML 资源）与
 * {@code getPreferenceScreen}（需要 UI 层）。
 */
public final class PreferenceManager {

    /** Android 契约：默认偏好文件名后缀。 */
    public static final String KEY_HAS_SET_DEFAULT_VALUES = "_has_set_default_values";

    private PreferenceManager() {
    }

    public static SharedPreferences getDefaultSharedPreferences(Context context) {
        return context.getSharedPreferences(getDefaultSharedPreferencesName(context), Context.MODE_PRIVATE);
    }

    public static String getDefaultSharedPreferencesName(Context context) {
        return context.getPackageName() + "_preferences";
    }
}
