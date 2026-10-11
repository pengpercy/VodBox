package dev.vodbox.jarhost.guard;

import java.io.File;
import java.io.IOException;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.stream.Stream;

/**
 * 运行时发现 ARM64 模拟依赖（unidbg + Dynarmic 后端），并把它们装进一个独立类加载器。
 *
 * <p><b>本仓库不内置、不拷贝、也不二次分发</b>任何模拟引擎：宿主在编译期只依赖 JDK 标准库
 * （见 {@code build.sh}），所有 unidbg API 都在运行时通过反射调用。放不放进依赖目录由操作方决定。
 *
 * <h2>运行时发现路径（按顺序）</h2>
 * <ol>
 *   <li>环境变量 {@code VODBOX_UNIDBG_CLASSPATH}：以路径分隔符（{@code :}/{@code ;}）分隔的
 *       jar 或目录列表；目录会被展开为一层的 {@code *.jar}。</li>
 *   <li>环境变量 {@code VODBOX_UNIDBG_HOME}：unidbg 解压目录；取其自身与其 {@code lib/} 下的 {@code *.jar}。</li>
 *   <li>随包目录：宿主 JAR 同级的 {@code unidbg/}（及其 {@code lib/}）。</li>
 * </ol>
 *
 * <h2>依赖许可（事实陈述，不在本仓库内裁决）</h2>
 * <ul>
 *   <li><b>Dynarmic</b>（引擎）为 <b>0BSD</b>；<b>unidbg</b>/<b>unidbg-android</b>/<b>unidbg-dynarmic</b>
 *       （{@code com.github.zhkl0228}）为 <b>Apache-2.0</b>；{@code native-lib-loader} 为 BSD-2；
 *       发布 jar 内含 {@code osx_arm64/libdynarmic.dylib}（Mach-O arm64，导出 {@code JNI_OnLoad}）。</li>
 *   <li><b>摩擦点（必须保留的说明）</b>：{@code unidbg-api} 的 <b>compile 依赖</b>包含
 *       {@code com.github.zhkl0228:unicorn}（<b>GPL-2.0</b>）与 {@code com.github.zhkl0228:keystone}
 *       （<b>GPL-2.0</b>）。即使走 Dynarmic 路径，unicorn 的 Java 常量类仍在类加载路径上；
 *       本仓库**不随包捆绑**这些强 copyleft 组件、**不修改**任何许可证，只允许操作方在运行时
 *       显式配置依赖目录自行引入。</li>
 *   <li>Dynarmic 路径是否真的不实例化 {@code unicorn.Unicorn}（即不 dlopen GPL 原生库）——
 *       <b>由运行时验证</b>，本层不预判。</li>
 * </ul>
 */
public final class UnidbgToolchain {
    /** 显式指定 unidbg classpath（jar 或目录，路径分隔符分隔）。 */
    public static final String CLASSPATH_ENVIRONMENT_VARIABLE = "VODBOX_UNIDBG_CLASSPATH";
    /** unidbg 解压目录。 */
    public static final String HOME_ENVIRONMENT_VARIABLE = "VODBOX_UNIDBG_HOME";
    /** 随包目录名（相对宿主 JAR 所在目录）。 */
    public static final String BUNDLED_DIRECTORY = "unidbg";

    /** 许可说明：必须随任何「启用/报告」路径一起保留。 */
    public static final String LICENSE_NOTE =
            "unidbg-api/unidbg-android 的编译期传递依赖 unicorn/keystone 为 GPL-2.0；本仓库不随包分发、"
                    + "不修改其许可证，仅在运行时由操作方经 " + CLASSPATH_ENVIRONMENT_VARIABLE + " / "
                    + HOME_ENVIRONMENT_VARIABLE + " / 随包 " + BUNDLED_DIRECTORY + "/ 显式配置。"
                    + "Dynarmic 引擎为 0BSD，unidbg-* 模块为 Apache-2.0，native-lib-loader 为 BSD-2。";

    /** 真实调用链必需、逐个反射校验的 unidbg 类型（缺任一即不可用，并点名缺谁）。 */
    private static final String[] REQUIRED_TYPES = {
            "com.github.unidbg.Emulator",
            "com.github.unidbg.linux.android.AndroidEmulatorBuilder",
            "com.github.unidbg.AndroidEmulator",
            "com.github.unidbg.linux.android.AndroidResolver",
            "com.github.unidbg.linux.android.dvm.VM",
            "com.github.unidbg.linux.android.dvm.DalvikModule",
            "com.github.unidbg.linux.android.dvm.Jni",
            "com.github.unidbg.linux.android.dvm.DvmClassFactory",
            "com.github.unidbg.linux.android.dvm.jni.ProxyClassFactory",
            "com.github.unidbg.linux.android.dvm.jni.ProxyClassMapper",
            "com.github.unidbg.linux.android.dvm.jni.ProxyDvmObject",
            "com.github.unidbg.virtualmodule.android.AndroidModule",
            "com.github.unidbg.arm.backend.DynarmicFactory",
            "org.scijava.nativelib.NativeLoader",
    };

    private UnidbgToolchain() {}

    /** 按约定的发现路径收集 unidbg jar；找不到返回空列表。 */
    public static List<Path> discover(Path hostJarDirectory) {
        Set<Path> found = new LinkedHashSet<>();
        collectFromPathList(found, System.getenv(CLASSPATH_ENVIRONMENT_VARIABLE));
        collectFromHome(found, System.getenv(HOME_ENVIRONMENT_VARIABLE));
        if (hostJarDirectory != null) collectFromHome(found, hostJarDirectory.resolve(BUNDLED_DIRECTORY).toString());
        return new ArrayList<>(found);
    }

    /** 是否配置了依赖目录（不代表可用）。 */
    public static boolean configured() {
        return !discover(null).isEmpty();
    }

    /**
     * 解析依赖并反射校验必需类型。<b>绝不抛异常</b>：不可用时返回带明确原因的 {@link Resolution}，
     * 由调用方决定如何报告——绝不把「没做」说成「已模拟」。
     */
    public static Resolution resolve(Path hostJarDirectory) {
        List<Path> classpath = discover(hostJarDirectory);
        if (classpath.isEmpty()) {
            return Resolution.unavailable(classpath, null,
                    "未配置 ARM64 模拟依赖目录：设置 " + CLASSPATH_ENVIRONMENT_VARIABLE + " 或 "
                            + HOME_ENVIRONMENT_VARIABLE + "，或随包放置 " + BUNDLED_DIRECTORY + "/ 目录。" + LICENSE_NOTE);
        }
        URLClassLoader loader = new URLClassLoader(toUrls(classpath), UnidbgToolchain.class.getClassLoader());
        List<String> missing = new ArrayList<>();
        for (String type : REQUIRED_TYPES) {
            try {
                Class.forName(type, false, loader);
            } catch (ClassNotFoundException | LinkageError absent) {
                missing.add(type);
            }
        }
        if (!missing.isEmpty()) {
            tryClose(loader);
            return Resolution.unavailable(classpath, null,
                    "已配置 unidbg 依赖（" + classpath.size() + " 个条目），但反射校验缺少必需类型："
                            + String.join("、", missing) + "。" + LICENSE_NOTE);
        }
        return Resolution.available(classpath, loader);
    }

    /** 依赖解析结果。{@link #available()} 为 true 才代表真实调用链可以接线。 */
    public static final class Resolution implements AutoCloseable {
        /** 收集到的依赖（jar 路径）。 */
        public final List<Path> classpath;
        /** 仅当可用时非 null。 */
        public final ClassLoader loader;
        /** 不可用时的明确原因；可用时为 null。 */
        public final String unavailableReason;
        /** 用于报告的简短说明（可用时也带许可说明）。 */
        public final String note;

        private Resolution(List<Path> classpath, ClassLoader loader, String reason, String note) {
            this.classpath = classpath;
            this.loader = loader;
            this.unavailableReason = reason;
            this.note = note;
        }

        static Resolution available(List<Path> classpath, ClassLoader loader) {
            return new Resolution(classpath, loader, null, "unidbg 已就绪（" + classpath.size() + " 个条目）。" + LICENSE_NOTE);
        }

        static Resolution unavailable(List<Path> classpath, ClassLoader loader, String reason) {
            return new Resolution(classpath, loader, reason, reason);
        }

        public boolean available() {
            return loader != null;
        }

        /** 从依赖类加载器里取类型；必需类型已在 {@link #resolve} 校验过。 */
        public Class<?> require(String name) throws ClassNotFoundException {
            if (loader == null) throw new ClassNotFoundException(name + "（unidbg 依赖不可用）");
            return Class.forName(name, false, loader);
        }

        @Override
        public void close() {
            if (loader instanceof URLClassLoader urlLoader) tryClose(urlLoader);
        }
    }

    private static void tryClose(URLClassLoader loader) {
        try {
            loader.close();
        } catch (IOException ignored) {
            // 关闭失败不影响判定。
        }
    }

    private static URL[] toUrls(List<Path> classpath) {
        List<URL> urls = new ArrayList<>();
        for (Path path : classpath) {
            try {
                urls.add(path.toUri().toURL());
            } catch (java.net.MalformedURLException impossible) {
                // 本地文件路径不会产生非法 URL。
            }
        }
        return urls.toArray(new URL[0]);
    }

    private static void collectFromPathList(Set<Path> target, String value) {
        if (value == null || value.isBlank()) return;
        for (String part : value.split(File.pathSeparator)) {
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

    private static void collectFromHome(Set<Path> target, String home) {
        if (home == null || home.isBlank()) return;
        Path root = Path.of(home.trim());
        if (!Files.isDirectory(root)) return;
        collectJars(target, root);
        collectJars(target, root.resolve("lib"));
    }

    /** 收集目录下一层的 *.jar（不递归，避免误收无关产物）。 */
    private static void collectJars(Set<Path> target, Path directory) {
        if (!Files.isDirectory(directory)) return;
        try (Stream<Path> stream = Files.list(directory)) {
            stream.filter(path -> Files.isRegularFile(path) && path.getFileName().toString().endsWith(".jar"))
                    .map(path -> path.toAbsolutePath().normalize())
                    .sorted()
                    .forEach(target::add);
        } catch (IOException ignored) {
            // 目录不可读：跳过该来源。
        }
    }
}
