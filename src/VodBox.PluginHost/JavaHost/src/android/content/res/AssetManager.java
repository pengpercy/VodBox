package android.content.res;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import java.util.Arrays;

/**
 * Android {@code AssetManager} 最小子集（VodBox 自有实现）。
 *
 * 这里把「资源根目录」当作物理解释：宿主在初始化时把插件 JAR 内的 {@code assets/**} 解包到
 * 该目录，{@code open}/{@code list} 即在该目录内解析。**只实现文件读写语义**：
 * {@code openFd}（需要 APK 文件描述符）等未实现的成员不在此声明，插件调用时会以
 * {@code NoSuchMethodError} 明确失败并被归类为 {@code unsupported_android}，不伪造可用。
 *
 * 安全：拒绝绝对路径与任何含 {@code ..} 的路径段（防越出资源根）。
 */
public final class AssetManager {

    public static final int ACCESS_UNKNOWN = 0;
    public static final int ACCESS_RANDOM = 1;
    public static final int ACCESS_STREAMING = 2;
    public static final int ACCESS_BUFFER = 3;

    private final File root;

    /** root 为 null 时表示没有任何资源：任何 {@code open} 都抛 {@link FileNotFoundException}。 */
    public AssetManager(File root) {
        this.root = root;
    }

    public InputStream open(String fileName) throws IOException {
        return open(fileName, ACCESS_STREAMING);
    }

    public InputStream open(String fileName, int access) throws IOException {
        File target = resolve(fileName);
        if (target == null || !target.isFile()) {
            throw new FileNotFoundException("Asset not found: " + fileName);
        }
        return new FileInputStream(target);
    }

    /** 列出一个目录下的直接子项；目录不存在返回空数组（与 Android 一致）。 */
    public String[] list(String path) {
        File dir = resolve(path == null || path.isEmpty() ? "" : path);
        if (dir == null || !dir.isDirectory()) return new String[0];
        String[] names = dir.list();
        if (names == null) return new String[0];
        Arrays.sort(names);
        return names;
    }

    public void close() {
        // 无底层句柄需要释放（与 Android 的 AssetManager 语义一致：句柄在 GC 时释放）。
    }

    /** 解析为根目录下的真实文件；越界/非法路径返回 null。 */
    private File resolve(String relative) {
        if (root == null) return null;
        if (relative == null) return null;
        String normalized = relative.replace('\\', '/');
        while (normalized.startsWith("/")) normalized = normalized.substring(1);
        if (normalized.isEmpty()) return root;
        for (String segment : normalized.split("/")) {
            if (segment.equals("..")) return null;
        }
        return new File(root, normalized);
    }
}
