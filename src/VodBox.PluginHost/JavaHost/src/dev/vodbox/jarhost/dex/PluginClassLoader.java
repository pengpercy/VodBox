package dev.vodbox.jarhost.dex;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.function.Function;
import java.util.stream.Stream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * 惰性 DEX 插件类加载器（VodBox 自有实现，与 guard 包的 native 桥接加载器相互独立）。
 *
 * <h2>为什么需要它</h2>
 * 整包转换对首屏不可接受——上游实测一个 4.4 MB 插件冷转换 178 个类耗时 217 s。本加载器把
 * 「用到才转」接到类加载路径上：{@link #findClass(String)} 命中归档里**未转换**的 DEX 类时，
 * 调 {@link DexCache#resolveClassSafely(Path, String, List)} 只转换**这一个类**（内容寻址缓存 +
 * 跨进程锁 + 原子提交，见 {@link DexCache}），再 {@code defineClass} 把它定义进本加载器。
 *
 * <h2>字节来源与优先级</h2>
 * <ol>
 *   <li><b>插件归档</b>：归档本身就是普通字节码 JAR 时直接取 {@code .class}（插件自己的类优先）；</li>
 *   <li><b>随包运行时库</b>（{@link #discoverLibraries}）：okhttp/gson/jsoup 等普通 jar，先于惰性转换，
 *       免得为每个库类都跑一次注定失败的转换；</li>
 *   <li><b>惰性 DEX 转换</b>：仅当归档含 {@code classes.dex} 且提供了 {@link DexCache} 时启用。</li>
 * </ol>
 * 三处都取不到即抛 {@link ClassNotFoundException}——**不**伪造空类。
 *
 * <h2>native 改写接口</h2>
 * 构造时传入的 {@code Function<byte[], byte[]> rewriter} 在 {@code defineClass} **之前**逐类应用
 * （与 guard 包的 native 桥接加载器 {@code NativeBridgingClassLoader} 同一契约）；
 * 为 {@code null}（ASM 缺失）时退化为不改写。本类不引用任何第三方类型，编译期只需 JDK 标准库。
 *
 * <h2>资源归档支持</h2>
 * 委派语义不变（父加载器优先），但归档里的**非类资源**（{@code assets/**}、{@code META-INF/**}、
 * 插件自带配置文件）也由本加载器提供：{@link #findResource(String)} 回落 {@code jar:...!/} URL，
 * {@link #getResourceAsStream(String)} 直接从归档 zip 读，因此对扩展名为 {@code .dex}/{@code .zip}
 * 的归档（URLClassLoader 不一定当 jar 处理）同样有效。
 *
 * <p><b>边界（不夸大）</b>：本类只负责「按需把归档/DEX 里的类变成 JVM 类」，不改写磁盘归档，
 * 也不修补字节码内容；转换成功后插件若引用**未实现**的 {@code android.*} 类或原生库，
 * 仍会在加载/链接阶段以 {@code unsupported_android} / {@code unsupported_native_library} 明确失败。
 */
public final class PluginClassLoader extends URLClassLoader {

    /** 随包第三方运行时库目录名（相对宿主 JAR 所在目录）。 */
    public static final String BUNDLED_LIBRARY_DIRECTORY = "runtime";
    /** 显式指定运行时库 classpath（jar 或目录，路径分隔符分隔）。 */
    public static final String LIBRARY_CLASSPATH_ENVIRONMENT_VARIABLE = "VODBOX_JARHOST_CLASSPATH";

    private final Path archive;
    private final DexCache cache;
    private final List<Path> dexClasspath;
    private final List<Path> libraries;
    private final Function<byte[], byte[]> rewriter;
    private final boolean lazy;

    /**
     * @param archive       插件归档（普通字节码 JAR 或含 DEX 的归档）；可为 {@code null}（只用运行时库）
     * @param cache         惰性单类转换缓存；为 {@code null} 时不做惰性转换（归档须已是 JVM 产物）
     * @param dexClasspath  转换引擎（dex2jar）类路径，由 {@link DexToolchain#discover(Path)} 发现
     * @param libraries     随包第三方运行时库（普通 jar/目录），先于惰性转换解析
     * @param rewriter      native 改写函数（{@code null} 表示不改写）
     * @param parent        父加载器（宿主自带类由父加载，委派语义不变）
     */
    public PluginClassLoader(Path archive, DexCache cache, List<Path> dexClasspath,
                             List<Path> libraries, Function<byte[], byte[]> rewriter, ClassLoader parent) {
        super(urls(archive, libraries), parent);
        this.archive = archive;
        this.cache = cache;
        this.dexClasspath = dexClasspath == null ? List.of() : List.copyOf(dexClasspath);
        this.libraries = libraries == null ? List.of() : List.copyOf(libraries);
        this.rewriter = rewriter;
        this.lazy = archive != null && cache != null && DexArchive.hasDex(archive);
    }

    /** 无随包运行时库的重载。 */
    public PluginClassLoader(Path archive, DexCache cache, List<Path> dexClasspath,
                             Function<byte[], byte[]> rewriter, ClassLoader parent) {
        this(archive, cache, dexClasspath, List.of(), rewriter, parent);
    }

    /** 便捷工厂：把 {@code <hostDirectory>/runtime/} 的随包库自动并进 classpath。 */
    public static PluginClassLoader bundled(Path archive, DexCache cache, List<Path> dexClasspath,
                                            Function<byte[], byte[]> rewriter, ClassLoader parent, Path hostDirectory) {
        return new PluginClassLoader(archive, cache, dexClasspath, discoverLibraries(hostDirectory), rewriter, parent);
    }

    // ---------- 类加载 ----------

    @Override
    protected Class<?> findClass(String name) throws ClassNotFoundException {
        // 1) 归档（普通字节码 JAR）→ 2) 随包运行时库 → 3) 惰性逐类 DEX 转换。
        byte[] bytes = readClass(name);
        if (bytes == null) bytes = convertClass(name);
        if (bytes == null) throw new ClassNotFoundException(name + "（插件归档与运行时库都没有这个类）");
        if (rewriter != null) bytes = rewriter.apply(bytes);
        return defineClass(name, bytes, 0, bytes.length);
    }

    /** 从归档或随包库读 {@code .class}；都没有时返回 {@code null}。归档优先（插件自己的类优先）。 */
    private byte[] readClass(String className) {
        String entry = internalClass(className);
        List<Path> sources = new ArrayList<>();
        if (archive != null) sources.add(archive);
        sources.addAll(libraries);
        for (Path source : sources) {
            byte[] bytes = readEntry(source, entry);
            if (bytes != null) return bytes;
        }
        return null;
    }

    /** 惰性路径：把归档里的单个类现转成 JVM 字节码并读回；不做惰性转换时返回 {@code null}。 */
    private byte[] convertClass(String className) throws ClassNotFoundException {
        if (!lazy) return null;
        Path converted;
        try {
            converted = cache.resolveClassSafely(archive, className, dexClasspath);
        } catch (DexException error) {
            throw new ClassNotFoundException(
                    className + "：惰性 DEX 转换失败（" + error.code() + "：" + error.getMessage() + "）", error);
        }
        byte[] bytes = readEntry(converted, internalClass(className));
        if (bytes == null) {
            throw new ClassNotFoundException(className + "：转换产物不含该类（" + converted.getFileName() + "）");
        }
        return bytes;
    }

    // ---------- 资源 ----------

    @Override
    public URL findResource(String name) {
        URL resolved = super.findResource(name);
        if (resolved != null) return resolved;
        if (archive == null || name == null || readEntry(archive, name) == null) return null;
        try {
            // jar: URL 对内层是 zip 的归档（.dex/.zip）同样可读。
            return new URL("jar", "", -1, archive.toUri().toURL() + "!/" + name);
        } catch (IOException error) {
            return null;
        }
    }

    @Override
    public InputStream getResourceAsStream(String name) {
        if (name == null) return null;
        ClassLoader parent = getParent();
        InputStream fromParent = parent == null ? null : parent.getResourceAsStream(name);
        if (fromParent != null) return fromParent;
        byte[] bytes = readEntry(archive, name);
        if (bytes != null) return new ByteArrayInputStream(bytes);
        return super.getResourceAsStream(name);
    }

    // ---------- 随包运行时库发现 ----------

    /**
     * 随包第三方运行时库：环境变量 {@code VODBOX_JARHOST_CLASSPATH}（覆盖）→
     * {@code <hostDirectory>/runtime/}（及其 {@code lib/}）。放进去即可用，无需用户手填环境变量。
     */
    public static List<Path> discoverLibraries(Path hostDirectory) {
        Set<Path> found = new LinkedHashSet<>();
        collectFromPathList(found, System.getenv(LIBRARY_CLASSPATH_ENVIRONMENT_VARIABLE));
        if (hostDirectory != null) {
            Path runtime = hostDirectory.resolve(BUNDLED_LIBRARY_DIRECTORY);
            collectJars(found, runtime);
            collectJars(found, runtime.resolve("lib"));
        }
        return new ArrayList<>(found);
    }

    // ---------- 底层读取 ----------

    private static String internalClass(String className) {
        return className.replace('.', '/') + ".class";
    }

    /** 读归档/目录里的一个条目；目录路径按文件系统读，zip 路径按 zip 条目读。不存在返回 {@code null}。 */
    private static byte[] readEntry(Path source, String entry) {
        if (source == null || entry == null) return null;
        if (Files.isDirectory(source)) {
            Path file = source.resolve(entry);
            try {
                return Files.isRegularFile(file) ? Files.readAllBytes(file) : null;
            } catch (IOException error) {
                return null;
            }
        }
        try (ZipFile zip = new ZipFile(source.toFile())) {
            ZipEntry found = zip.getEntry(entry);
            if (found == null || found.isDirectory()) return null;
            try (InputStream input = zip.getInputStream(found)) {
                return input.readAllBytes();
            }
        } catch (IOException error) {
            return null;
        }
    }

    private static URL[] urls(Path archive, List<Path> libraries) {
        List<URL> list = new ArrayList<>();
        if (archive != null) add(list, archive);
        if (libraries != null) for (Path library : libraries) add(list, library);
        return list.toArray(new URL[0]);
    }

    private static void add(List<URL> target, Path path) {
        try {
            target.add(path.toUri().toURL());
        } catch (java.net.MalformedURLException impossible) {
            // 本地文件路径不会产生非法 URL。
        }
    }

    private static void collectFromPathList(Set<Path> target, String value) {
        if (value == null || value.isBlank()) return;
        for (String part : value.split(java.io.File.pathSeparator)) {
            String trimmed = part.trim();
            if (trimmed.isEmpty()) continue;
            Path path = Path.of(trimmed);
            if (Files.isDirectory(path)) {
                collectJars(target, path);
            } else if (Files.isRegularFile(path)) {
                target.add(path.toAbsolutePath().normalize());
            }
        }
    }

    private static void collectJars(Set<Path> target, Path directory) {
        if (directory == null || !Files.isDirectory(directory)) return;
        try (Stream<Path> stream = Files.list(directory)) {
            stream.filter(path -> Files.isRegularFile(path) && path.getFileName().toString().endsWith(".jar"))
                    .map(path -> path.toAbsolutePath().normalize())
                    .sorted()
                    .forEach(target::add);
        } catch (IOException ignored) {
            // 目录不可读：跳过该来源（与 DexToolchain/JdbcSqliteEngine 的发现约定一致）。
        }
    }
}
