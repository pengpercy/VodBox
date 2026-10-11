package android.content;

import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.res.AssetManager;
import android.os.Looper;

import java.io.File;

/**
 * Android {@code ContextWrapper} 最小子集（VodBox 自有实现）。
 *
 * 语义与 Android 一致：包装一个 base {@code Context} 并把所有调用转发给它。
 * {@link #attachBaseContext(Context)} 是 protected，子类（如 {@code android.app.Application}）可加宽为 public。
 */
public class ContextWrapper extends Context {

    private Context mBase;

    public ContextWrapper(Context base) {
        mBase = base;
    }

    protected void attachBaseContext(Context base) {
        mBase = base;
    }

    public Context getBaseContext() {
        return mBase;
    }

    /** 未附着 base 时的调用一律明确失败，而不是静默返回 null。 */
    private Context base() {
        if (mBase == null) {
            throw new UnsupportedOperationException("Context 尚未附着 base（attachBaseContext 未调用）");
        }
        return mBase;
    }

    @Override
    public AssetManager getAssets() {
        return base().getAssets();
    }

    @Override
    public PackageManager getPackageManager() {
        return base().getPackageManager();
    }

    @Override
    public String getPackageName() {
        return base().getPackageName();
    }

    @Override
    public ApplicationInfo getApplicationInfo() {
        return base().getApplicationInfo();
    }

    @Override
    public Context getApplicationContext() {
        return base().getApplicationContext();
    }

    @Override
    public File getFilesDir() {
        return base().getFilesDir();
    }

    @Override
    public File getCacheDir() {
        return base().getCacheDir();
    }

    @Override
    public File getNoBackupFilesDir() {
        return base().getNoBackupFilesDir();
    }

    @Override
    public File getExternalFilesDir(String type) {
        return base().getExternalFilesDir(type);
    }

    @Override
    public File getExternalCacheDir() {
        return base().getExternalCacheDir();
    }

    @Override
    public File getDir(String name, int mode) {
        return base().getDir(name, mode);
    }

    @Override
    public File getFileStreamPath(String name) {
        return base().getFileStreamPath(name);
    }

    @Override
    public File getDatabasePath(String name) {
        return base().getDatabasePath(name);
    }

    @Override
    public SharedPreferences getSharedPreferences(String name, int mode) {
        return base().getSharedPreferences(name, mode);
    }

    @Override
    public ClassLoader getClassLoader() {
        return base().getClassLoader();
    }

    @Override
    public Looper getMainLooper() {
        return base().getMainLooper();
    }

    @Override
    public Object getSystemService(String name) {
        return base().getSystemService(name);
    }
}
