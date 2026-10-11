package android.content.pm;

import java.util.List;

/**
 * Android {@code PackageManager} 最小子集（VodBox 自有实现）。
 *
 * 只建模插件常见的「读取自身包信息」路径：{@link #getPackageInfo} / {@link #getApplicationInfo} /
 * {@link #getInstalledPackages}。行为明确：只认宿主自己的包名（{@code Context.getPackageName()}），
 * 其它包名一律抛 {@link NameNotFoundException}——**绝不**假装任意包已安装。
 */
public abstract class PackageManager {

    public static final int GET_ACTIVITIES = 0x00000001;
    public static final int GET_CONFIGURATIONS = 0x00004000;
    public static final int GET_META_DATA = 0x00000080;
    public static final int GET_PERMISSIONS = 0x00001000;
    public static final int GET_SIGNATURES = 0x00000040;
    public static final int GET_SIGNING_CERTIFICATES = 0x08000000;
    public static final int GET_DISABLED_COMPONENTS = 0x00000200;

    protected PackageManager() {}

    public abstract PackageInfo getPackageInfo(String packageName, int flags) throws NameNotFoundException;

    public abstract ApplicationInfo getApplicationInfo(String packageName, int flags) throws NameNotFoundException;

    public abstract List<PackageInfo> getInstalledPackages(int flags);

    /** 与 Android 一致：包不存在时抛出（受检）。 */
    public static class NameNotFoundException extends Exception {
        public NameNotFoundException() {
            super();
        }

        public NameNotFoundException(String name) {
            super(name);
        }
    }
}
