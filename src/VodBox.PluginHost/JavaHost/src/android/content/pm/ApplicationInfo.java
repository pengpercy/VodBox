package android.content.pm;

import android.os.Bundle;

/**
 * Android {@code ApplicationInfo} 最小子集（VodBox 自有实现）。
 * 目录字段指向宿主的隔离目录；{@code metaData} 恒为 null（本宿主不解析 AndroidManifest）。
 */
public class ApplicationInfo {

    public static final int FLAG_SYSTEM = 1;
    public static final int FLAG_DEBUGGABLE = 1 << 1;
    public static final int FLAG_UPDATED_SYSTEM_APP = 1 << 7;
    public static final int FLAG_STOPPED = 1 << 5;

    public String packageName;
    public String sourceDir;
    public String publicSourceDir;
    public String dataDir;
    public String nativeLibraryDir;
    public int flags;
    public int targetSdkVersion;
    public int minSdkVersion;
    public int uid;
    /** 本宿主不解析 manifest，恒为 null。 */
    public Bundle metaData;

    public ApplicationInfo() {}

    @Override
    public String toString() {
        return "ApplicationInfo{" + packageName + "}";
    }
}
