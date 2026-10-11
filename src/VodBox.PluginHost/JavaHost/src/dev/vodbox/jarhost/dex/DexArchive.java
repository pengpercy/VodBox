package dev.vodbox.jarhost.dex;

import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Set;
import java.util.TreeSet;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * 只读归档里的 DEX 事实：多 DEX 排序、DEX 头校验与关键计数。
 *
 * 这是本仓库自有实现（纯 JDK 标准库，不含任何上游源码），仅做**结构检查**：
 * 判断归档是不是 DEX 包、按 Android 约定排序 {@code classes.dex} / {@code classes2.dex} / …、
 * 读取 DEX 头里的版本与大小。它不做字节码翻译（翻译交给 {@link DexToolchain} 发现的转换器）。
 * 结构检查先行可以给出比转换器更准确的失败分类（损坏 vs 版本不支持）。
 */
public final class DexArchive {
    /** Android 约定：classes.dex, classes2.dex … classesN.dex（大小写敏感）。 */
    private static final Pattern DEX_ENTRY = Pattern.compile("^classes(\\d*)\\.dex$");

    /** DEX 头支持的最高版本（含）。超出即明确报不支持，不猜测。 */
    private static final int MIN_VERSION = 35;
    private static final int MAX_VERSION = 41;

    private DexArchive() {}

    /** 归档里是否存在按约定命名的 DEX 入口。 */
    public static boolean hasDex(Path archive) {
        try {
            return !dexEntries(archive).isEmpty();
        } catch (IOException error) {
            return false;
        }
    }

    /**
     * 按 Android 多 DEX 加载顺序返回 DEX 入口名：{@code classes.dex} 首位，随后
     * {@code classes2.dex}、{@code classes3.dex} …依数字升序。非法数字后缀按 0 处理但排在显式序号之后。
     */
    public static List<String> dexEntries(Path archive) throws IOException {
        List<DexEntry> entries = new ArrayList<>();
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            var it = zip.entries();
            while (it.hasMoreElements()) {
                ZipEntry entry = it.nextElement();
                if (entry.isDirectory()) continue;
                Matcher matcher = DEX_ENTRY.matcher(entry.getName());
                if (!matcher.matches()) continue;
                String suffix = matcher.group(1);
                entries.add(new DexEntry(entry.getName(), dexOrder(suffix)));
            }
        }
        entries.sort(Comparator.comparingInt((DexEntry e) -> e.order).thenComparing(e -> e.name));
        List<String> names = new ArrayList<>(entries.size());
        for (DexEntry entry : entries) names.add(entry.name);
        return names;
    }

    /** 读取一个 DEX 入口的头部事实；入口不是合法 DEX 时抛 {@link DexException}。 */
    public static Header readHeader(Path archive, String entryName) throws DexException {
        byte[] header = new byte[112];
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            ZipEntry entry = zip.getEntry(entryName);
            if (entry == null) throw new DexException(DexCodes.CORRUPT, "归档缺少 DEX 入口：" + entryName);
            try (InputStream input = zip.getInputStream(entry)) {
                int read = input.readNBytes(header, 0, header.length);
                if (read < header.length) throw new DexException(DexCodes.CORRUPT, "DEX 头部不足：" + entryName);
            }
        } catch (IOException error) {
            throw new DexException(DexCodes.CORRUPT, "读取 DEX 失败：" + entryName + "（" + error + "）", error);
        }
        return parseHeader(header, entryName);
    }

    /** 解析 112 字节 DEX 头（小端）。 */
    static Header parseHeader(byte[] header, String entryName) throws DexException {
        if (header.length < 112 || header[0] != 'd' || header[1] != 'e' || header[2] != 'x' || header[3] != '\n') {
            throw new DexException(DexCodes.CORRUPT, "不是合法 DEX（magic 不符）：" + entryName);
        }
        if (header[7] != 0) throw new DexException(DexCodes.CORRUPT, "DEX magic 结束符异常：" + entryName);
        String version = new String(header, 4, 3, java.nio.charset.StandardCharsets.US_ASCII);
        int numeric;
        try {
            numeric = Integer.parseInt(version);
        } catch (NumberFormatException error) {
            throw new DexException(DexCodes.CORRUPT, "DEX 版本字段非法：" + version);
        }
        if (numeric < MIN_VERSION || numeric > MAX_VERSION) {
            throw new DexException(DexCodes.UNSUPPORTED_VERSION, "不支持的 DEX 版本：" + version);
        }
        ByteBuffer buffer = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN);
        int fileSize = buffer.getInt(0x20);
        int headerSize = buffer.getInt(0x24);
        int stringCount = buffer.getInt(0x38);
        int classCount = buffer.getInt(0x60);
        if (headerSize < 0x70 || fileSize < headerSize) {
            throw new DexException(DexCodes.CORRUPT, "DEX 头字段异常（header_size=" + headerSize + ", file_size=" + fileSize + "）");
        }
        return new Header(version, fileSize, stringCount, classCount);
    }

    /** 校验归档里的所有 DEX 头，返回首个 DEX 的版本与入口总数。 */
    public static Inspected inspect(Path archive) throws DexException {
        List<String> entries = new ArrayList<>();
        try {
            entries.addAll(dexEntries(archive));
        } catch (IOException error) {
            throw new DexException(DexCodes.CORRUPT, "无法读取归档：" + archive + "（" + error + "）", error);
        }
        if (entries.isEmpty()) throw new DexException(DexCodes.CORRUPT, "归档不含 classes.dex");
        int totalClasses = 0;
        String version = "";
        for (String entry : entries) {
            Header header = readHeader(archive, entry);
            if (version.isEmpty()) version = header.version();
            totalClasses += header.classCount();
        }
        return new Inspected(entries.size(), version, totalClasses, List.copyOf(entries));
    }

    /** DEX 头事实。 */
    public record Header(String version, int fileSize, int stringCount, int classCount) {}

    /** 归档级 DEX 事实。 */
    public record Inspected(int dexFileCount, String version, int declaredClassCount, List<String> entries) {}

    /** classes.dex -> 1；classesN.dex -> N；空或溢出的数字后缀按 1（由名称排序兜底）。 */
    private static int dexOrder(String suffix) {
        if (suffix.isEmpty()) return 1;
        try {
            return Integer.parseInt(suffix);
        } catch (NumberFormatException overflow) {
            return 1;
        }
    }

    private record DexEntry(String name, int order) {}

    /** 判断路径是否是一个可读的、含 .class 的 zip（转换产物校验用）。 */
    public static boolean isLoadableJar(Path path) {
        if (path == null || !Files.isRegularFile(path)) return false;
        try (ZipFile zip = new ZipFile(path.toFile())) {
            var it = zip.entries();
            while (it.hasMoreElements()) {
                ZipEntry entry = it.nextElement();
                if (!entry.isDirectory() && entry.getName().endsWith(".class")) return true;
            }
            return false;
        } catch (IOException error) {
            return false;
        }
    }

    /** 归档里的 DEX 入口（读不动时返回空表，不抛）。 */
    static List<String> dexEntriesQuiet(Path archive) {
        try {
            return dexEntries(archive);
        } catch (IOException unreadable) {
            return List.of();
        }
    }

    /** 转换产物 JAR 是否真的含该类（惰性单类转换的产物校验）。 */
    public static boolean containsClass(Path jarPath, String internalName) {
        if (jarPath == null || internalName == null || !Files.isRegularFile(jarPath)) return false;
        String expected = internalName.replace('.', '/') + ".class";
        try (ZipFile zip = new ZipFile(jarPath.toFile())) {
            return zip.getEntry(expected) != null;
        } catch (IOException error) {
            return false;
        }
    }

    /**
     * 归档**引用但未定义**的类（点分隔全限定名，已滤掉平台/宿主库前缀）。
     *
     * <p>这是上游「字节码修补」链（DanglingTypes）的**检测半边**，独立实现：DEX 的 {@code type_ids} 列出
     * 该 DEX 引用到的所有类型，{@code class_defs} 列出它定义的类型，两者之差即「引用但没带」的类型
     * （典型来源：插件构建把捆绑库裁剪过）。ART 只在真正用到时才失败，而 HotSpot 会**整类校验**——
     * 一个缺失接口就可能让无关类都不可加载。因此这份清单是决定「是否需要合成占位类型」的依据。
     *
     * <p><b>边界（不夸大）</b>：本方法只**检测**，不合成也不改写字节码。改写半边由
     * {@link Compatibility} 承担（逐方法体、依赖 ASM/BSD-3-Clause）：它已实现**构造器祖先 owner 修补**与
     * **接口静态调用桥**；**占位类型注入**仍需生成新类、属下一阶段。因此本方法与 {@code Compatibility}
     * 都不把「检测到」说成「已修好」。
     */
    public static List<String> danglingTypes(Path archive) throws DexException {
        Set<String> referenced = new TreeSet<>();
        Set<String> defined = new TreeSet<>();
        for (String entry : dexEntriesQuiet(archive)) {
            scanTypeTables(readDex(archive, entry), referenced, defined);
        }
        List<String> dangling = new ArrayList<>();
        for (String descriptor : referenced) {
            if (!descriptor.startsWith("L") || !descriptor.endsWith(";")) continue;
            String name = descriptor.substring(1, descriptor.length() - 1).replace('/', '.');
            if (defined.contains(descriptor) || platform(name)) continue;
            dangling.add(name);
        }
        return dangling;
    }

    /** 平台与宿主已提供的库前缀：这些缺失是真缺口，绝不合成占位。 */
    private static boolean platform(String name) {
        for (String prefix : PLATFORM_PREFIXES) if (name.startsWith(prefix)) return true;
        return false;
    }

    private static final String[] PLATFORM_PREFIXES = {
            "java.", "javax.", "jdk.", "sun.", "android.", "androidx.", "dalvik.",
            "kotlin.", "kotlinx.", "okhttp3.", "okio.", "org.json.", "com.google.gson.",
            "com.github.catvod.crawler.",
    };

    /** 读出一个 DEX 条目的完整字节。 */
    private static byte[] readDex(Path archive, String entryName) throws DexException {
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            ZipEntry entry = zip.getEntry(entryName);
            if (entry == null) throw new DexException(DexCodes.CORRUPT, "归档缺少 DEX 入口：" + entryName);
            try (InputStream input = zip.getInputStream(entry)) {
                return input.readAllBytes();
            }
        } catch (IOException error) {
            throw new DexException(DexCodes.CORRUPT, "读取 DEX 失败：" + entryName + "（" + error + "）", error);
        }
    }

    /** 从 DEX 的 string_ids / type_ids / class_defs 三张表收集「引用的类型」与「定义的类型」。 */
    private static void scanTypeTables(byte[] dex, Set<String> referenced, Set<String> defined) throws DexException {
        if (dex.length < 0x70 || dex[0] != 'd' || dex[1] != 'e' || dex[2] != 'x') {
            throw new DexException(DexCodes.CORRUPT, "不是合法 DEX（magic 不符）。");
        }
        ByteBuffer buffer = ByteBuffer.wrap(dex).order(ByteOrder.LITTLE_ENDIAN);
        int stringCount = buffer.getInt(0x38);
        int stringOff = buffer.getInt(0x3C);
        int typeCount = buffer.getInt(0x40);
        int typeOff = buffer.getInt(0x44);
        int classCount = buffer.getInt(0x60);
        int classOff = buffer.getInt(0x64);
        if (stringOff < 0 || stringOff + 4L * stringCount > dex.length
                || typeOff < 0 || typeOff + 4L * typeCount > dex.length
                || classOff < 0 || classOff + 32L * classCount > dex.length) {
            throw new DexException(DexCodes.CORRUPT, "DEX 的 string/type/class 表越界。");
        }
        String[] strings = new String[stringCount];
        for (int index = 0; index < stringCount; index++) {
            strings[index] = readStringData(dex, buffer.getInt(stringOff + 4 * index));
        }
        String[] descriptors = new String[typeCount];
        for (int index = 0; index < typeCount; index++) {
            int stringIndex = buffer.getInt(typeOff + 4 * index);
            if (stringIndex >= 0 && stringIndex < stringCount) descriptors[index] = strings[stringIndex];
        }
        for (String descriptor : descriptors) {
            if (descriptor != null) referenced.add(descriptor);
        }
        for (int index = 0; index < classCount; index++) {
            int classIndex = buffer.getInt(classOff + 32 * index);
            if (classIndex >= 0 && classIndex < typeCount && descriptors[classIndex] != null) {
                defined.add(descriptors[classIndex]);
            }
        }
    }

    /** 读一个 string_data_item（uleb128 长度 + MUTF-8 + 0x00）。描述符均为 ASCII，按 UTF-8 解码足够。 */
    private static String readStringData(byte[] dex, int offset) {
        if (offset < 0 || offset >= dex.length) return null;
        int cursor = offset;
        while (cursor < dex.length && (dex[cursor] & 0x80) != 0) cursor++; // 跳过 uleb128 长度
        if (cursor >= dex.length) return null;
        cursor++; // uleb128 末字节
        int end = cursor;
        while (end < dex.length && dex[end] != 0) end++;
        return new String(dex, cursor, end - cursor, java.nio.charset.StandardCharsets.UTF_8);
    }

    /** 统计 zip 内 .class 条目数（转换产物统计用）。 */
    public static int countClasses(Path path) {
        int count = 0;
        try (ZipFile zip = new ZipFile(path.toFile())) {
            var it = zip.entries();
            while (it.hasMoreElements()) {
                ZipEntry entry = it.nextElement();
                if (!entry.isDirectory() && entry.getName().endsWith(".class")) count++;
            }
        } catch (IOException error) {
            return -1;
        }
        return count;
    }
}
