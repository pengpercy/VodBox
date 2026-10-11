package dev.vodbox.jarhost.android;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.res.AssetManager;
import android.os.Looper;

import java.io.File;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;

/**
 * 桌面宿主的 {@link Context} 实现（VodBox 自有实现）。
 *
 * 目录布局（都在会话 profile 目录下，互不越界）：
 * <pre>
 *   &lt;profile&gt;/data/files       getFilesDir
 *   &lt;profile&gt;/data/cache       getCacheDir
 *   &lt;profile&gt;/data/no_backup   getNoBackupFilesDir
 *   &lt;profile&gt;/data/databases   getDatabasePath 的父目录
 *   &lt;profile&gt;/data/shared_prefs 文件型 SharedPreferences
 *   &lt;profile&gt;/external         getExternalFilesDir / Environment 外部存储根
 *   &lt;profile&gt;/assets           插件 JAR 中 assets/** 的解包根
 * </pre>
 *
 * **不实现系统服务**：{@link #getSystemService(String)} 一律抛 {@link UnsupportedOperationException}，
 * 由宿主归类为 {@code unsupported_android}——不把空壳服务伪装成可用。
 */
public final class DesktopContext extends Context {

    private final ClassLoader classLoader;
    private final String packageName;
    private final File dataDir;
    private final File filesDir;
    private final File cacheDir;
    private final File noBackupDir;
    private final File databasesDir;
    private final File sharedPrefsDir;
    private final File externalDir;
    private final File externalCacheDir;
    private final AssetManager assets;
    private final DesktopPackageManager packageManager;
    private final Map<String, SharedPreferences> preferences = new ConcurrentHashMap<>();

    public DesktopContext(ClassLoader classLoader, String packageName, File profileRoot,
                          File externalRoot, File assetsRoot, String sourceDir, String nativeLibraryDir) {
        this.classLoader = classLoader;
        this.packageName = packageName;
        this.dataDir = new File(profileRoot, "data");
        this.filesDir = new File(dataDir, "files");
        this.cacheDir = new File(dataDir, "cache");
        this.noBackupDir = new File(dataDir, "no_backup");
        this.databasesDir = new File(dataDir, "databases");
        this.sharedPrefsDir = new File(dataDir, "shared_prefs");
        this.externalDir = externalRoot;
        this.externalCacheDir = new File(externalRoot, "cache");
        this.assets = new AssetManager(assetsRoot);
        this.packageManager = new DesktopPackageManager(
                packageName, "1.0.0", 1, dataDir.getAbsolutePath(), sourceDir, nativeLibraryDir);
    }

    private static File ensure(File directory) {
        if (!directory.exists()) directory.mkdirs();
        return directory;
    }

    @Override public AssetManager getAssets() { return assets; }

    @Override public PackageManager getPackageManager() { return packageManager; }

    @Override public String getPackageName() { return packageName; }

    @Override public ApplicationInfo getApplicationInfo() {
        try {
            return packageManager.getApplicationInfo(packageName, 0);
        } catch (PackageManager.NameNotFoundException impossible) {
            throw new IllegalStateException(impossible);
        }
    }

    @Override public Context getApplicationContext() { return this; }

    @Override public File getFilesDir() { return ensure(filesDir); }

    @Override public File getCacheDir() { return ensure(cacheDir); }

    @Override public File getNoBackupFilesDir() { return ensure(noBackupDir); }

    @Override public File getExternalFilesDir(String type) {
        File base = new File(externalDir, "files");
        return ensure(type == null || type.isEmpty() ? base : new File(base, type));
    }

    @Override public File getExternalCacheDir() { return ensure(externalCacheDir); }

    @Override public File getDir(String name, int mode) {
        if (name == null) throw new NullPointerException("name == null");
        return ensure(new File(dataDir, "app_" + name));
    }

    @Override public File getFileStreamPath(String name) {
        if (name == null) throw new NullPointerException("name == null");
        return new File(getFilesDir(), name);
    }

    @Override public File getDatabasePath(String name) {
        if (name == null) throw new NullPointerException("name == null");
        File file = name.startsWith("/") ? new File(name) : new File(databasesDir, name);
        File parent = file.getParentFile();
        if (parent != null) parent.mkdirs();
        return file;
    }

    @Override public SharedPreferences getSharedPreferences(String name, int mode) {
        if (name == null) throw new NullPointerException("name == null");
        return preferences.computeIfAbsent(name, key -> {
            File directory = ensure(sharedPrefsDir);
            return new FileSharedPreferences(new File(directory, key + ".xml"));
        });
    }

    @Override public ClassLoader getClassLoader() { return classLoader; }

    @Override public Looper getMainLooper() { return Looper.getMainLooper(); }

    @Override public Object getSystemService(String name) {
        throw new UnsupportedOperationException("本宿主不实现 Android 系统服务：" + name);
    }

    /** 供宿主诊断：已创建的偏好实例名。 */
    public Map<String, SharedPreferences> openedPreferences() {
        return new HashMap<>(preferences);
    }
}
