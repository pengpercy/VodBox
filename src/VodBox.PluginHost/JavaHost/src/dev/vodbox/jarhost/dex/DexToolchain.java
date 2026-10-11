package dev.vodbox.jarhost.dex;

import java.io.File;
import java.io.IOException;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
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
 * DEX→JVM 转换器定位与调用。
 *
 * <p>本仓库**不内置也不拷贝**任何 DEX 转换实现；转换引擎在运行时发现，通过反射调用，
 * 因此编译器/构建脚本只需 JDK 标准库（见 {@code build.sh}），宿主与转换引擎在源码层面完全解耦。
 *
 * <h2>运行时发现路径（按顺序）</h2>
 * <ol>
 *   <li>环境变量 {@code VODBOX_DEX2JAR_CLASSPATH}：以路径分隔符（{@code :}/{@code ;}）分隔的
 *       jar 或目录列表；目录会被展开为一层的 {@code *.jar}。</li>
 *   <li>环境变量 {@code DEX2JAR_HOME}：dex-tools 解压目录；取其自身与其 {@code lib/} 下的 {@code *.jar}。</li>
 *   <li>随包目录：宿主 JAR 同级的 {@code dex-tools/}（及其 {@code lib/}）。</li>
 * </ol>
 *
 * <h2>依赖许可</h2>
 * <ul>
 *   <li><b>dex2jar</b>（{@code github.com/pxb1988/dex2jar}，库 {@code dex-reader} / {@code d2j-dex2jar}）
 *       为 <b>Apache License 2.0</b>，与 Apache-2.0/兼容许可的项目可共存；本仓库以**运行时可选依赖**
 *       方式使用，不复制其源码、不做二次分发打包。</li>
 *   <li>未发现上述任一引擎时，返回 {@link DexCodes#CONVERTER_UNAVAILABLE} 并**明确失败**，
 *       绝不把未转换的 DEX 标记为可加载。</li>
 * </ul>
 */
public final class DexToolchain {
    /** 显式指定转换器 classpath（jar 或目录，路径分隔符分隔）。 */
    public static final String CLASSPATH_ENVIRONMENT_VARIABLE = "VODBOX_DEX2JAR_CLASSPATH";
    /** dex-tools 解压目录。 */
    public static final String HOME_ENVIRONMENT_VARIABLE = "DEX2JAR_HOME";
    /** 随包目录名（相对宿主 JAR 所在目录）。 */
    public static final String BUNDLED_DIRECTORY = "dex-tools";

    private static final String DEX2JAR_CLASS = "com.googlecode.d2j.dex.Dex2jar";

    private DexToolchain() {}

    /** 按约定的发现路径收集转换器 jar；找不到返回空列表。 */
    public static List<Path> discover(Path hostJarDirectory) {
        Set<Path> found = new LinkedHashSet<>();
        collectFromPathList(found, System.getenv(CLASSPATH_ENVIRONMENT_VARIABLE));
        collectFromHome(found, System.getenv(HOME_ENVIRONMENT_VARIABLE));
        if (hostJarDirectory != null) collectFromBundled(found, hostJarDirectory.resolve(BUNDLED_DIRECTORY));
        return new ArrayList<>(found);
    }

    /** 转换器是否可用（至少发现一个 jar 且能加载到 dex2jar 类）。 */
    public static boolean available(Path hostJarDirectory) {
        List<Path> classpath = discover(hostJarDirectory);
        if (classpath.isEmpty()) return false;
        ClassLoader loader = new URLClassLoader(toUrls(classpath), DexToolchain.class.getClassLoader());
        try {
            Class.forName(DEX2JAR_CLASS, false, loader);
            return true;
        } catch (ClassNotFoundException | LinkageError missing) {
            return false;
        }
    }

    /**
     * 把归档转换为 JVM 字节码 JAR。{@code classpath} 为 {@link #discover} 的发现结果。
     * 未发现引擎时抛 {@link DexCodes#CONVERTER_UNAVAILABLE}；引擎运行失败抛 {@link DexCodes#CONVERSION_FAILED}。
     */
    public static void convert(Path archive, Path outputJar, List<Path> classpath) throws DexException {
        convert(archive, outputJar, classpath, null);
    }

    /**
     * **惰性路径**：只把归档里的**单个类**转换为 JVM 字节码 JAR（dex2jar 的 {@code onlyClass}）。
     *
     * <p>为什么要惰性：整包转换对首屏不可接受——上游实测一个 4.4 MB 插件冷转换 178 个类耗时 217 s。
     * 逐类转换让「用到才转」，配合逐类内容寻址缓存，把冷启动成本摊到实际被请求的类上。
     *
     * <p>本方法只负责「调用引擎 + 校验产物含该类」；跨进程锁 / 原子提交 / 缓存由 {@link DexCache} 负责。
     * 转换产物**不含**该类的父类/接口（JVM 运行时按需从别处解析），这与 ART 的行为一致。
     *
     * @param className 全限定类名（点分隔，如 {@code com.github.catvod.spider.AppRJ}）
     */
    public static void convertClass(Path archive, String className, Path outputJar, List<Path> classpath) throws DexException {
        if (className == null || className.isBlank()) {
            throw new DexException(DexCodes.CONVERSION_FAILED, "惰性转换需要类名。");
        }
        convert(archive, outputJar, classpath, className.replace('.', '/'));
    }

    private static void convert(Path archive, Path outputJar, List<Path> classpath, String onlyClass) throws DexException {
        if (classpath == null || classpath.isEmpty()) {
            throw new DexException(DexCodes.CONVERTER_UNAVAILABLE,
                    "未发现 DEX→JVM 转换器（设置 " + CLASSPATH_ENVIRONMENT_VARIABLE + " 或 " + HOME_ENVIRONMENT_VARIABLE
                            + "，或随包放置 " + BUNDLED_DIRECTORY + "/）。");
        }
        ClassLoader loader = new URLClassLoader(toUrls(classpath), DexToolchain.class.getClassLoader());
        Class<?> engine;
        try {
            engine = Class.forName(DEX2JAR_CLASS, true, loader);
        } catch (ClassNotFoundException | LinkageError unavailable) {
            throw new DexException(DexCodes.CONVERTER_UNAVAILABLE, "转换器缺少入口类 " + DEX2JAR_CLASS + "：" + unavailable, unavailable);
        }
        try {
            Files.createDirectories(outputJar.toAbsolutePath().getParent());
            invoke(engine, archive, outputJar, onlyClass);
        } catch (DexException error) {
            throw error;
        } catch (InvocationTargetException wrapped) {
            Throwable cause = wrapped.getCause() == null ? wrapped : wrapped.getCause();
            throw new DexException(DexCodes.CONVERSION_FAILED, "DEX 转换失败：" + cause, cause);
        } catch (ReflectiveOperationException | RuntimeException | IOException error) {
            throw new DexException(DexCodes.CONVERSION_FAILED, "DEX 转换失败：" + error, error);
        }
    }

    /**
     * 反射调用 dex2jar 的流式 API：{@code Dex2jar.from(path)} →（可选调优）→ {@code to(output)}。
     * 各可选步骤存在才调用，以兼容不同 dex2jar 版本；{@code from}/{@code to} 任一缺失即视为引擎不可用。
     *
     * @param onlyClass 非 null 时施加 {@code onlyClass(...)}（惰性单类转换）；引擎不支持则明确失败，不静默退化为整包。
     */
    private static void invoke(Class<?> engine, Path archive, Path outputJar, String onlyClass) throws ReflectiveOperationException, IOException, DexException {
        Method from = findFrom(engine);
        if (from == null) throw new DexException(DexCodes.CONVERTER_UNAVAILABLE, "转换器 " + engine.getName() + " 缺少 from(...) 入口。");
        Object stage = from.invoke(null, from.getParameterTypes()[0] == Path.class ? archive : archive.toFile());
        if (onlyClass != null) {
            Method limit = findInstance(stage.getClass(), "onlyClass", String.class);
            if (limit != null) {
                stage = limit.invoke(stage, onlyClass);
            } else {
                ClassLoader apiLoader = engine.getClassLoader();
                Class<?> readerApi = Class.forName("com.googlecode.d2j.reader.BaseDexFileReader", true, apiLoader);
                Class<?> visitorApi = Class.forName("com.googlecode.d2j.visitors.DexFileVisitor", true, apiLoader);
                Object reader = engine.getMethod("getReader").invoke(stage);
                @SuppressWarnings("unchecked")
                List<String> names = (List<String>) readerApi.getMethod("getClassNames").invoke(reader);
                String descriptor = "L" + onlyClass.replace('.', '/') + ";";
                int selected = names.indexOf(descriptor);
                if (selected < 0) throw new DexException(DexCodes.CONVERSION_FAILED, "DEX 未声明目标类：" + descriptor);
                int index = selected;
                Object filtered = java.lang.reflect.Proxy.newProxyInstance(apiLoader, new Class<?>[]{readerApi}, (proxy, method, args) -> {
                    if (method.getName().equals("getClassNames")) return List.of(descriptor);
                    if (method.getName().equals("accept")) {
                        int flags = args.length == 3 ? (Integer) args[2] : args.length == 2 ? (Integer) args[1] : 0;
                        visitorApi.getMethod("visitDexFileVersion", int.class).invoke(args[0], readerApi.getMethod("getDexVersion").invoke(reader));
                        readerApi.getMethod("accept", visitorApi, int.class, int.class).invoke(reader, args[0], index, flags);
                        return null;
                    }
                    return method.invoke(reader, args);
                });
                stage = engine.getMethod("from", readerApi).invoke(null, filtered);
            }
        }
        stage = tryInvoke(stage, "skipDebug", true);
        stage = tryInvoke(stage, "optimizeSynchronized", true);
        stage = tryInvoke(stage, "topoLogicalSort");
        stage = tryInvoke(stage, "computeFrames", true);
        stage = tryInvoke(stage, "reUseReg", false);
        if (stage == null) throw new DexException(DexCodes.CONVERSION_FAILED, "转换器初始化返回 null。");
        Method to = findTo(stage.getClass());
        if (to == null) throw new DexException(DexCodes.CONVERTER_UNAVAILABLE, "转换器缺少 to(...) 入口。");
        try {
            to.invoke(stage, to.getParameterTypes()[0] == Path.class ? outputJar : outputJar.toFile());
        } catch (InvocationTargetException wrapped) {
            Throwable cause = wrapped.getCause() == null ? wrapped : wrapped.getCause();
            if (cause instanceof IOException io) throw io;
            if (cause instanceof RuntimeException runtime) throw runtime;
            throw new DexException(DexCodes.CONVERSION_FAILED, "转换器执行失败：" + cause, cause);
        }
    }

    private static Method findFrom(Class<?> engine) {
        return findStatic(engine, "from", Path.class) != null ? findStatic(engine, "from", Path.class) : findStatic(engine, "from", File.class);
    }

    private static Method findTo(Class<?> stage) {
        return findInstance(stage, "to", Path.class) != null ? findInstance(stage, "to", Path.class) : findInstance(stage, "to", File.class);
    }

    private static Method findStatic(Class<?> type, String name, Class<?> parameter) {
        try {
            return type.getMethod(name, parameter);
        } catch (NoSuchMethodException missing) {
            return null;
        }
    }

    private static Method findInstance(Class<?> type, String name, Class<?> parameter) {
        try {
            return type.getMethod(name, parameter);
        } catch (NoSuchMethodException missing) {
            return null;
        }
    }

    /** 尝试调用可选的无参/单参流式方法；不存在或调用失败时原样返回当前阶段对象（不中断转换）。 */
    private static Object tryInvoke(Object stage, String name) {
        return tryInvoke(stage, name, null);
    }

    private static Object tryInvoke(Object stage, String name, Object argument) {
        if (stage == null) return null;
        try {
            Class<?> type = stage.getClass();
            Method method = argument == null ? type.getMethod(name) : type.getMethod(name, argument.getClass() == Boolean.class ? boolean.class : argument.getClass());
            Object result = argument == null ? method.invoke(stage) : method.invoke(stage, argument);
            return result == null ? stage : result;
        } catch (ReflectiveOperationException | RuntimeException ignored) {
            return stage;
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

    private static void collectFromBundled(Set<Path> target, Path directory) {
        if (!Files.isDirectory(directory)) return;
        collectJars(target, directory);
        collectJars(target, directory.resolve("lib"));
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
