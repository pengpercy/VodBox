package android.content.pm;

import android.os.Bundle;

/**
 * Android {@code PackageInfo} 最小子集（VodBox 自有实现）。
 * 仅承载插件常见的版本字段；{@code applicationInfo} 指向 {@link ApplicationInfo}。
 */
public class PackageInfo {

    public String packageName;
    /** 已废弃但被广泛使用（如 {@code getPackageInfo(...).versionCode}）。 */
    @Deprecated
    public int versionCode;
    public int versionCodeMajor;
    public String versionName;
    public long firstInstallTime;
    public long lastUpdateTime;
    public ApplicationInfo applicationInfo;
    public Bundle metaData;

    public PackageInfo() {}

    /** 与 Android 一致：把 (versionCodeMajor, versionCode) 拼成 64 位版本号。 */
    public long getLongVersionCode() {
        return ((long) versionCodeMajor << 32) | (versionCode & 0xffffffffL);
    }

    @Override
    public String toString() {
        return "PackageInfo{" + packageName + " v" + versionName + " (" + versionCode + ")}";
    }
}
