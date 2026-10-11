package android.os;

import java.io.File;

/**
 * Android {@code Environment} 最小子集（VodBox 自有实现）。
 *
 * 目录指向宿主为该会话准备的**真实可写目录**（宿主在插件 init 前通过 {@link #configure(File, File)} 设置）。
 * {@link #getExternalStorageState()} 恒返回 {@link #MEDIA_MOUNTED}——该目录确实存在且可写，
 * 因此这是事实而非伪装。未调用 {@link #configure} 时回退到 JVM 临时目录（仍可写）。
 */
public final class Environment {

    public static final String MEDIA_MOUNTED = "mounted";
    public static final String MEDIA_MOUNTED_READ_ONLY = "mounted_ro";
    public static final String MEDIA_UNMOUNTED = "unmounted";
    public static final String MEDIA_REMOVED = "removed";
    public static final String MEDIA_UNKNOWN = "unknown";

    public static final String DIRECTORY_DOWNLOADS = "Download";
    public static final String DIRECTORY_PICTURES = "Pictures";
    public static final String DIRECTORY_MOVIES = "Movies";
    public static final String DIRECTORY_MUSIC = "Music";
    public static final String DIRECTORY_DCIM = "DCIM";
    public static final String DIRECTORY_DOCUMENTS = "Documents";

    private static volatile File dataDirectory = new File(System.getProperty("java.io.tmpdir", "."), "vodbox-android/data");
    private static volatile File externalStorageDirectory = new File(System.getProperty("java.io.tmpdir", "."), "vodbox-android/external");
    private static volatile File downloadCacheDirectory = new File(System.getProperty("java.io.tmpdir", "."), "vodbox-android/cache");

    private Environment() {
    }

    /** 宿主装配入口（非 Android API）：设置数据目录与外部存储根。 */
    public static void configure(File data, File external) {
        if (data != null) {
            dataDirectory = data;
            downloadCacheDirectory = new File(data, "cache");
        }
        if (external != null) externalStorageDirectory = external;
    }

    public static File getDataDirectory() {
        return ensure(dataDirectory);
    }

    public static File getDownloadCacheDirectory() {
        return ensure(downloadCacheDirectory);
    }

    public static File getExternalStorageDirectory() {
        return ensure(externalStorageDirectory);
    }

    public static String getExternalStorageState() {
        File external = getExternalStorageDirectory();
        if (external.exists() && !external.canWrite()) return MEDIA_MOUNTED_READ_ONLY;
        return MEDIA_MOUNTED;
    }

    public static boolean isExternalStorageEmulated() {
        return true;
    }

    public static boolean isExternalStorageRemovable() {
        return false;
    }

    public static File getExternalStoragePublicDirectory(String type) {
        return ensure(new File(getExternalStorageDirectory(), type == null ? "" : type));
    }

    private static File ensure(File directory) {
        if (!directory.exists()) directory.mkdirs();
        return directory;
    }
}
