package android.os;

import java.io.File;

/**
 * Android {@code StatFs} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 语义是**真实的**文件系统统计：容量/可用空间来自 JDK 的 {@link File#getTotalSpace()} 等，
 * 块信息按固定的 4096 字节块折算（Android 上块大小来自 {@code statfs(2)}；这里固定一个值，
 * 因而 {@code getBlockSizeLong() * getBlockCountLong()} 与 {@code getTotalBytes()} 自洽）。
 * 插件用它给图片/数据缓存定容量时得到的是真实磁盘数字，不是臆造值。
 */
public class StatFs {

    /** 折算用的块大小；与 {@code getTotalBytes() / getBlockCountLong()} 自洽。 */
    private static final long BLOCK_SIZE = 4096L;

    private File root;

    public StatFs(String path) {
        restat(path);
    }

    public void restat(String path) {
        root = new File(path == null || path.isEmpty()
                ? System.getProperty("java.io.tmpdir", ".")
                : path);
    }

    public long getTotalBytes() {
        return nonNegative(root.getTotalSpace());
    }

    public long getFreeBytes() {
        return nonNegative(root.getFreeSpace());
    }

    public long getAvailableBytes() {
        return nonNegative(root.getUsableSpace());
    }

    public long getBlockSizeLong() {
        return BLOCK_SIZE;
    }

    public long getBlockCountLong() {
        return getTotalBytes() / BLOCK_SIZE;
    }

    public long getFreeBlocksLong() {
        return getFreeBytes() / BLOCK_SIZE;
    }

    public long getAvailableBlocksLong() {
        return getAvailableBytes() / BLOCK_SIZE;
    }

    /** @deprecated 与 Android 一致：请使用 {@link #getBlockSizeLong()}。 */
    @Deprecated
    public int getBlockSize() {
        return (int) BLOCK_SIZE;
    }

    /** @deprecated 与 Android 一致：请使用 {@link #getBlockCountLong()}。 */
    @Deprecated
    public int getBlockCount() {
        return clamp(getBlockCountLong());
    }

    /** @deprecated 与 Android 一致：请使用 {@link #getFreeBlocksLong()}。 */
    @Deprecated
    public int getFreeBlocks() {
        return clamp(getFreeBlocksLong());
    }

    /** @deprecated 与 Android 一致：请使用 {@link #getAvailableBlocksLong()}。 */
    @Deprecated
    public int getAvailableBlocks() {
        return clamp(getAvailableBlocksLong());
    }

    /** 不存在的路径在 JDK 上返回 0（Android 的 {@code statfs} 亦以 0 表示不可用）。 */
    private static long nonNegative(long value) {
        return Math.max(0L, value);
    }

    private static int clamp(long value) {
        return value > Integer.MAX_VALUE ? Integer.MAX_VALUE : (int) value;
    }
}
