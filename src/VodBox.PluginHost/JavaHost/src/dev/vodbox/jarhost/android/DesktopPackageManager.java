package dev.vodbox.jarhost.android;

import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;

import java.util.Collections;
import java.util.List;

/**
 * 桌面宿主的最小 {@link PackageManager}（VodBox 自有实现）。
 *
 * 只报告宿主自己的包（{@link #packageName()}）——插件读取自身包名/版本时可用；
 * 查询任何其它包名都会抛 {@link NameNotFoundException}，**不假装**任意应用已安装。
 */
public final class DesktopPackageManager extends PackageManager {

    private final String packageName;
    private final PackageInfo packageInfo;
    private final ApplicationInfo applicationInfo;

    public DesktopPackageManager(String packageName, String versionName, int versionCode,
                                 String dataDir, String sourceDir, String nativeLibraryDir) {
        this.packageName = packageName;
        this.applicationInfo = new ApplicationInfo();
        applicationInfo.packageName = packageName;
        applicationInfo.dataDir = dataDir;
        applicationInfo.sourceDir = sourceDir;
        applicationInfo.publicSourceDir = sourceDir;
        applicationInfo.nativeLibraryDir = nativeLibraryDir;
        applicationInfo.targetSdkVersion = android.os.Build.VERSION.SDK_INT;
        applicationInfo.minSdkVersion = android.os.Build.VERSION.SDK_INT;
        applicationInfo.flags = ApplicationInfo.FLAG_DEBUGGABLE;

        this.packageInfo = new PackageInfo();
        packageInfo.packageName = packageName;
        packageInfo.versionName = versionName;
        packageInfo.versionCode = versionCode;
        packageInfo.firstInstallTime = 0L;
        packageInfo.lastUpdateTime = 0L;
        packageInfo.applicationInfo = applicationInfo;
    }

    public String packageName() {
        return packageName;
    }

    @Override
    public PackageInfo getPackageInfo(String name, int flags) throws NameNotFoundException {
        if (!packageName.equals(name)) throw new NameNotFoundException(name);
        return packageInfo;
    }

    @Override
    public ApplicationInfo getApplicationInfo(String name, int flags) throws NameNotFoundException {
        if (!packageName.equals(name)) throw new NameNotFoundException(name);
        return applicationInfo;
    }

    @Override
    public List<PackageInfo> getInstalledPackages(int flags) {
        return Collections.singletonList(packageInfo);
    }
}
