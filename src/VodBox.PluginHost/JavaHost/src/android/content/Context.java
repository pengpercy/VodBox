package android.content;

import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.res.AssetManager;
import android.os.Looper;

import java.io.File;

/**
 * Android {@code Context} 的**最小子集**（VodBox 自有实现，独立编写，未复制任何上游/GPL 源码）。
 *
 * 目标：让不依赖 Android UI/WebView/系统服务的普通 CatVod 插件能把 {@code init(Context, String)}
 * 及文件、偏好、资源等常见调用链接并运行起来。这里**只声明确实实现了的成员**：
 * 未列出的 Android API（如 {@code getResources}、{@code getContentResolver}）在插件的链接阶段会以
 * {@code NoSuchMethodError}/{@code NoClassDefFoundError} 明确失败，由宿主归类为 {@code unsupported_android}，
 * **不伪造可用**（见 {@code docs/JAR-PLUGIN-HOST.md}）。
 */
public abstract class Context {

    // ---- SharedPreferences 打开模式（取值与 Android 一致）----
    public static final int MODE_PRIVATE = 0x0000;
    public static final int MODE_WORLD_READABLE = 0x0001;
    public static final int MODE_WORLD_WRITEABLE = 0x0002;
    public static final int MODE_MULTI_PROCESS = 0x0004;
    public static final int MODE_ENABLE_WRITE_AHEAD_LOGGING = 0x0008;
    public static final int MODE_APPEND = 0x8000;

    // ---- 系统服务名常量（仅用于源码兼容；本宿主不实现任何系统服务）----
    public static final String WINDOW_SERVICE = "window";
    public static final String LAYOUT_INFLATER_SERVICE = "layout_inflater";
    public static final String ACTIVITY_SERVICE = "activity";
    public static final String CONNECTIVITY_SERVICE = "connectivity";
    public static final String TELEPHONY_SERVICE = "phone";
    public static final String LOCATION_SERVICE = "location";
    public static final String NOTIFICATION_SERVICE = "notification";
    public static final String STORAGE_SERVICE = "storage";

    protected Context() {}

    public abstract AssetManager getAssets();

    public abstract PackageManager getPackageManager();

    public abstract String getPackageName();

    public abstract ApplicationInfo getApplicationInfo();

    public abstract Context getApplicationContext();

    public abstract File getFilesDir();

    public abstract File getCacheDir();

    public File getCodeCacheDir() {
        File directory = new File(getCacheDir().getParentFile(), "code_cache");
        if (!directory.isDirectory() && !directory.mkdirs())
            throw new IllegalStateException("无法创建 code_cache：" + directory);
        return directory;
    }

    public abstract File getNoBackupFilesDir();

    /** Android 契约：{@code type} 为 null 时返回外部文件根目录。 */
    public abstract File getExternalFilesDir(String type);

    public abstract File getExternalCacheDir();

    public abstract File getDir(String name, int mode);

    public abstract File getFileStreamPath(String name);

    public abstract File getDatabasePath(String name);

    public abstract SharedPreferences getSharedPreferences(String name, int mode);

    public abstract ClassLoader getClassLoader();

    public abstract Looper getMainLooper();

    /**
     * 本宿主**不实现任何 Android 系统服务**：任何名字都会抛 {@link UnsupportedOperationException}，
     * 由宿主归类为 {@code unsupported_android}。这样插件不会拿到一个「看似可用、实则空壳」的服务对象。
     */
    public abstract Object getSystemService(String name);
}
