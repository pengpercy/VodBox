package dev.vodbox.jarhost.guard;

import java.io.IOException;
import java.io.InputStream;
import java.io.StringReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.Properties;
import java.util.TreeMap;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * 插件归档里 ARM64 原生 guard 库的**安全抽取与校验**（本仓库独立实现）。
 *
 * <p>覆盖的归档来源（对齐上游可观测的命名约定）：
 * <ul>
 *   <li>{@code assets/&lt;name&gt;[-_]v8.so}：CatVod 原生 guard 的命名约定；</li>
 *   <li>{@code lib/arm64-v8a/lib&lt;name&gt;.so} 及其 {@code assets/} 下的变体：按名解析（{@link #resolve}）。</li>
 * </ul>
 *
 * <p><b>安全边界（本类绝不越界）</b>：
 * <ol>
 *   <li>抽取到隔离目录，逐字节校验大小上限与 ARM64 小端 ELF 头；可选与归档内 md5 声明比对；</li>
 *   <li><b>绝不</b>调用 {@code System.load}/{@code System.loadLibrary}/{@code Runtime.load*}——
 *       Android {@code .so} 永远不会被本 JVM dlopen 进宿主进程；</li>
 *   <li>校验失败一律抛 {@link UnsupportedLibrary}，由宿主统一归类为 {@code unsupported_native_library}，
 *       绝不把缺失的原生能力伪装成可用。</li>
 * </ol>
 *
 * <p>本宿主没有可执行的宽松许可 ARM64 模拟后端，因此命中 guard 约定时只能明确禁用；
 * 普通 guard 与 FTY（饭太硬）分支的区别仅体现在库名诊断上，不改变结论。
 */
public final class NativeGuards {
    /** 单个原生库的硬上限：16 MiB。 */
    public static final long MAX_LIBRARY_BYTES = 16L * 1024 * 1024;
    /** md5 声明文件的上限：4 KiB。 */
    public static final int MAX_CHECKSUM_BYTES = 4096;

    /** {@code assets/<leaf>[-_]v8.so}（单层，无子目录）。 */
    private static final Pattern GUARD_ENTRY = Pattern.compile("assets/[^/]+[-_]v8\\.so");
    /** 合法的库名（拼进路径前先约束字符集）。 */
    private static final Pattern SAFE_NAME = Pattern.compile("[A-Za-z0-9_.-]+");
    /** FTY（饭太硬）特有的 guard 库名。 */
    private static final List<String> FTY_GUARD_NAMES = List.of("ftyguard_v8.so", "ftyguard-v8.so");

    /** ARM64 相关：EI_CLASS(64)、EI_DATA(小端)、EM_AARCH64。 */
    private static final byte ELF_CLASS_64 = 2;
    private static final byte ELF_DATA_LSB = 1;
    private static final int EM_AARCH64 = 183;
    /** 校验所需的最小前缀长度：e_ident(16) + e_type(2) + e_machine(2)。 */
    private static final int ELF_HEADER_MIN = 20;

    private NativeGuards() {}

    /** 需要一个隔离的 ARM64 模拟后端才能运行的原生库。宿主据此归类为 {@code unsupported_native_library}。 */
    public static final class UnsupportedLibrary extends UnsupportedOperationException {
        private static final long serialVersionUID = 1L;
        /** 具体库名（写进响应信封的 {@code library} 字段）。 */
        public final String library;
        public UnsupportedLibrary(String library, String reason) {
            super(library + ": " + reason);
            this.library = library;
        }
    }

    /** 是否为 {@code assets/*[-_]v8.so} 的 guard 约定条目。 */
    public static boolean isGuardEntry(String entryName) {
        return entryName != null && GUARD_ENTRY.matcher(entryName).matches();
    }

    /** 是否为 FTY（饭太硬）特有的 guard 库名（上游单独分支）。 */
    public static boolean isFtyGuard(String fileName) {
        if (fileName == null) return false;
        for (String name : FTY_GUARD_NAMES) if (name.equalsIgnoreCase(fileName)) return true;
        return false;
    }

    /**
     * 扫描归档里的 guard 约定库，逐个抽取并校验，返回 {@code 叶名 → 隔离目录内路径}。
     * 任一库不合法即抛 {@link UnsupportedLibrary}（不写出该库）。无 guard 时返回空 Map。
     */
    public static Map<String, Path> prepareGuards(Path archive, Path directory) throws IOException {
        Map<String, Path> libraries = new TreeMap<>();
        if (archive == null || !Files.isRegularFile(archive)) return libraries;
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            var entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                String name = entry.getName();
                if (entry.isDirectory() || !isGuardEntry(name)) continue;
                String leaf = name.substring("assets/".length());
                if (leaf.isEmpty() || leaf.contains("/")) continue;
                libraries.put(leaf, extract(zip, entry, leaf, directory));
            }
        }
        return libraries;
    }

    /**
     * 按名解析归档里的 ARM64 库（对齐上游候选顺序）。找不到返回 null；名字非法即抛 {@link UnsupportedLibrary}。
     */
    public static Path resolve(Path archive, String name, Path directory) throws IOException {
        if (name == null || name.isEmpty() || !SAFE_NAME.matcher(name).matches()) {
            throw new UnsupportedLibrary(String.valueOf(name), "非法的原生库名");
        }
        String base = name.endsWith(".so") ? name.substring(0, name.length() - 3) : name;
        String[] candidates = {
                "lib/arm64-v8a/lib" + base + ".so",
                "assets/lib" + base + ".so",
                "assets/" + base + ".so",
                "assets/" + base + "-v8.so",
                "assets/" + base + "_v8.so",
        };
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            for (String candidate : candidates) {
                ZipEntry entry = zip.getEntry(candidate);
                if (entry == null) continue;
                String leaf = candidate.substring(candidate.lastIndexOf('/') + 1);
                return extract(zip, entry, leaf, directory);
            }
        }
        return null;
    }

    /**
     * 抽取单个库：大小上限 → ARM64 小端 ELF 头 → 可选 md5 声明比对 → 原子写出到隔离目录。
     * <b>只写文件，不加载。</b>
     */
    private static Path extract(ZipFile zip, ZipEntry entry, String leaf, Path directory) throws IOException {
        byte[] bytes;
        try (InputStream stream = zip.getInputStream(entry)) {
            bytes = stream.readNBytes((int) MAX_LIBRARY_BYTES + 1);
        }
        if (bytes.length > MAX_LIBRARY_BYTES) throw new UnsupportedLibrary(leaf, "超过 16 MiB 上限");
        validateArm64(bytes, leaf);
        String declared = declaredMd5(zip, leaf);
        if (declared != null && !md5(bytes).equalsIgnoreCase(declared.trim())) {
            throw new UnsupportedLibrary(leaf, "与归档内 md5 声明不一致");
        }
        Files.createDirectories(directory);
        Path target = directory.resolve(leaf);
        Path temporary = Files.createTempFile(directory, "native-", ".tmp");
        try {
            Files.write(temporary, bytes);
            Files.move(temporary, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } finally {
            Files.deleteIfExists(temporary);
        }
        return target;
    }

    /** 读取归档内 {@code assets/<stem>.md5} 声明的 arm64-v8a 摘要；无声明/超限/非法一律返回 null。 */
    private static String declaredMd5(ZipFile zip, String leaf) {
        String stem = stripGuardSuffix(leaf);
        ZipEntry checksum = zip.getEntry("assets/" + stem + ".md5");
        if (checksum == null) return null;
        try (InputStream stream = zip.getInputStream(checksum)) {
            byte[] buffer = stream.readNBytes(MAX_CHECKSUM_BYTES + 1);
            if (buffer.length > MAX_CHECKSUM_BYTES) return null;
            Properties values = new Properties();
            values.load(new StringReader(new String(buffer, StandardCharsets.UTF_8)));
            String expected = values.getProperty("arm64-v8a");
            return expected == null || expected.isBlank() ? null : expected;
        } catch (IOException error) {
            return null;
        }
    }

    /** ARM64 小端 ELF 的最小结构校验：magic、EI_CLASS、EI_DATA、e_machine。 */
    public static void validateArm64(byte[] bytes, String name) {
        if (bytes == null || bytes.length < ELF_HEADER_MIN
                || bytes[0] != 0x7F || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F'
                || bytes[4] != ELF_CLASS_64 || bytes[5] != ELF_DATA_LSB
                || (bytes[18] & 0xFF) != EM_AARCH64 || bytes[19] != 0) {
            throw new UnsupportedLibrary(name, "期望小端 ARM64 ELF 库（ELF64/EM_AARCH64）");
        }
    }

    private static String md5(byte[] bytes) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("MD5").digest(bytes));
        } catch (java.security.NoSuchAlgorithmException unavailable) {
            throw new IllegalStateException("MD5 不可用", unavailable);
        }
    }

    private static String stripGuardSuffix(String fileName) {
        for (String suffix : new String[]{"-v8.so", "_v8.so"}) {
            if (fileName.toLowerCase().endsWith(suffix)) return fileName.substring(0, fileName.length() - suffix.length());
        }
        return fileName.toLowerCase().endsWith(".so") ? fileName.substring(0, fileName.length() - 3) : fileName;
    }
}
