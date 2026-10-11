package dev.vodbox.jarhost;

import android.app.Application;
import android.os.Environment;
import dev.vodbox.jarhost.android.DesktopContext;
import dev.vodbox.jarhost.dex.DexArchive;
import dev.vodbox.jarhost.dex.DexCache;
import dev.vodbox.jarhost.dex.DexCodes;
import dev.vodbox.jarhost.dex.DexConverter;
import dev.vodbox.jarhost.dex.DexException;
import dev.vodbox.jarhost.dex.DexToolchain;
import dev.vodbox.jarhost.guard.DynarmicGuardBackend;
import dev.vodbox.jarhost.guard.FtyGuardLoader;
import dev.vodbox.jarhost.guard.NativeBridgingClassLoader;
import dev.vodbox.jarhost.guard.NativeDexMaterializer;
import dev.vodbox.jarhost.guard.NativeGuards;
import dev.vodbox.jarhost.guard.NativeBridgingClassLoader;
import dev.vodbox.jarhost.guard.NativeRewriterFactory;
import dev.vodbox.jarhost.guard.NativeMethodBridge;
import dev.vodbox.jarhost.guard.NativeDexMaterializer;
import dev.vodbox.jarhost.guard.FtyGuardLoader;
import dev.vodbox.jarhost.guard.NativeMethodBridge;
import dev.vodbox.jarhost.guard.NativeRewriterFactory;
import dev.vodbox.jarhost.guard.UnidbgToolchain;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Function;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * VodBox 的最小外部 JAR 插件宿主（独立实现，不包含任何上游源码或未声明依赖）。
 *
 * 协议：以 {@code --serve <request.json>} 启动；就绪后写 {@code <cache>/ready.json}；
 * 之后从 stdin 逐行读取 {@code {"id":..,"params":{..}}}，把响应原子写到
 * {@code <cache>/responses/<id>.json}。信封字段：result / error / errorCode / ...（见 C# 侧协议）。
 *
 * 能力边界（明确、不夸大）：
 * - 执行**普通 Java 字节码 JAR**：包含 {@code com/github/catvod/spider/*.class}。
 * - 执行**普通/多 DEX 的 CatVod 归档**：把 {@code classes.dex} / {@code classes2.dex} … 经
 *   DEX→JVM 转换后再加载（转换引擎在运行时发现，见 {@link DexToolchain}）。
 * - 转换成功后插件若仍引用**未实现**的 {@code android.*}/{@code dalvik.*}（如 UI/WebView/系统服务/
 *   SQLite/okhttp）或未实现原生库，一律在加载/链接阶段以 {@code unsupported_android} /
 *   {@code unsupported_native_library} 明确禁用——**绝不**把缺失类伪装成成功。
 * - 已实现一个**明确的 Android API 最小子集**（见 {@code android.*}/{@code dalvik.*} 包与其
 *   {@code dev.vodbox.jarhost.android} 实现）：Context/Application/ContextWrapper、文件型
 *   SharedPreferences、AssetManager、Base64、Log、LruCache、AtomicFile、Uri、Build/Environment/
 *   SystemClock、Handler/Looper/Message/Bundle、VMRuntime、DexClassLoader、PackageManager 模型。
 *   系统服务（{@code getSystemService}）**不实现**——调用即抛 {@link UnsupportedOperationException}，
 *   归类为 {@code unsupported_android}。
 * - 转换过程自身的失败单独分类：{@code dex_converter_unavailable} / {@code dex_corrupt} /
 *   {@code dex_unsupported_version} / {@code dex_conversion_failed} / {@code dex_conversion_locked}。
 * - 通过反射调用 spider 方法，因此**不在编译期依赖**任何 CatVod 类；插件 JAR 自带的框架类由它自己提供。
 */
public final class Host {
    private Host() {}

    public static void main(String[] args) {
        // 插件会随意打印；stdout 让给诊断，协议只走文件。
        System.setOut(System.err);
        if (args.length == 2 && "--convert".equals(args[0])) {
            System.exit(convert(Path.of(args[1])));
            return;
        }
        if (args.length == 2 && "--probe-native".equals(args[0])) {
            System.exit(probeNative(Path.of(args[1])));
            return;
        }
        if (args.length != 2 || !"--serve".equals(args[0])) {
            System.err.println("usage: Host (--serve|--convert|--probe-native) <request.json>");
            System.exit(2);
        }
        Path requestPath = Path.of(args[1]);
        Map<String, Object> input;
        try {
            input = Json.parseObject(Files.readString(requestPath, StandardCharsets.UTF_8));
        } catch (IOException error) {
            System.err.println("host: cannot read request: " + error);
            System.exit(2);
            return;
        }
        String cacheValue = string(input, "cache");
        Path cache;
        Path profile;
        try {
            cache = Path.of(cacheValue.isEmpty() ? requestPath.getParent().toString() : cacheValue);
            Files.createDirectories(cache.resolve("responses"));
            String profileValue = string(input, "profile");
            profile = profileValue.isEmpty() ? cache.resolve("profile") : Path.of(profileValue);
            Files.createDirectories(profile);
        } catch (IOException error) {
            System.err.println("host: cannot prepare cache: " + error);
            System.exit(2);
            return;
        }
        Session session;
        try {
            session = open(input, cache, profile);
        } catch (Throwable error) {
            try {
                write(cache.resolve("ready.json"), failure(error));
            } catch (IOException writeError) {
                System.err.println("host: cannot write startup failure: " + writeError);
            }
            System.exit(1);
            return;
        }
        try {
            Map<String, Object> ready = new LinkedHashMap<>();
            ready.put("ready", Boolean.TRUE);
            write(cache.resolve("ready.json"), ready);
        } catch (IOException error) {
            System.err.println("host: cannot write ready: " + error);
            System.exit(1);
            return;
        }
        serve(session, cache);
    }

    // ---------- 转换子命令（独立进程，供桌面端在进程外做缓存/锁/原子提交） ----------

    /**
     * {@code --convert <request.json>}：把 {@code input} 处的 DEX 归档转换为 {@code output} 处的 JVM JAR，
     * 并把结果信封写到 {@code result}（缺省为 request 同目录的 {@code convert.json}）。
     * 成功写 {@code {"ok":true,"dexFiles":..,"dexVersion":..,"classCount":..}}，失败写分类错误信封。
     */
    private static int convert(Path requestPath) {
        Path resultPath = requestPath.resolveSibling("convert.json");
        Map<String, Object> input = new LinkedHashMap<>();
        try {
            input = Json.parseObject(Files.readString(requestPath, StandardCharsets.UTF_8));
            if (!string(input, "result").isEmpty()) resultPath = Path.of(string(input, "result"));
        } catch (IOException error) {
            System.err.println("host: cannot read convert request: " + error);
            return 2;
        }
        Map<String, Object> response;
        boolean ok = false;
        try {
            Path archive = Path.of(string(input, "input"));
            Path output = Path.of(string(input, "output"));
            if (!Files.isRegularFile(archive)) throw new DexException(DexCodes.CORRUPT, "转换输入不存在：" + archive);
            List<Path> classpath = DexToolchain.discover(hostDirectory());
            DexConverter.Result converted = DexConverter.convert(archive, output, classpath);
            response = new LinkedHashMap<>();
            response.put("ok", Boolean.TRUE);
            response.put("dexFiles", converted.dexFileCount());
            response.put("dexVersion", converted.dexVersion());
            response.put("classCount", converted.classCount());
            ok = true;
        } catch (Throwable error) {
            error.printStackTrace(System.err);
            response = failure(error);
        }
        try {
            write(resultPath, response);
        } catch (IOException error) {
            System.err.println("host: cannot write convert result: " + error);
            return 2;
        }
        return ok ? 0 : 1;
    }

    // ---------- 会话开启 ----------

    private static Session open(Map<String, Object> input, Path cache, Path profile) throws Exception {
        String jarPath = string(input, "jar");
        if (jarPath.isEmpty()) throw new IllegalStateException("request.json 缺少 jar");
        Path archive = Path.of(jarPath);
        if (!Files.isRegularFile(archive)) throw new IllegalStateException("插件 JAR 不存在：" + jarPath);
        // DEX 归档：先在宿主缓存里转换为 JVM JAR 再加载（转换失败按 dex_* 分类明确抛出）。
        // 桌面端通常在进程外已转换并传入普通 JAR，此分支保证宿主对直接喂入的 DEX 归档也自足可用。
        Path jar = archive;
        // DEX 由插件类加载器逐类转换，保留原归档资源。

        // 按**内容**（不是库名/扩展名）定位 FTY（饭太硬）加密 loader 形态：只有命中才走 bootstrap Init 的真实链路。
        FtyGuardLoader.Layout layout = FtyGuardLoader.inspect(archive);

        // 插件类加载器：归档里若有 native 方法，改用「透明桥接」类加载器（去 native 化 → 转发同进程模拟器）。
        URLClassLoader loader = createPluginLoader(jar, cache);
        DynarmicGuardBackend guardBackend = null;
        boolean handedOff = false;
        try {
            // Android API 最小子集：装配 Application(Context) 供插件 init 使用；系统服务/UI/WebView 不实现。
            Object context = createAndroidContext(loader, profile, archive);
            // 原生 guard：抽取 + ARM64 ELF 校验（安全边界不动），再在**同一进程**内用 Dynarmic 真实初始化
            // （loadLibrary + JNI_OnLoad）。无依赖/失败 → unsupported_native_library；绝不伪报可播。
            Map<String, Path> guards = prepareGuards(archive, profile);
            guardBackend = createGuardBackend(guards, profile, archive);
            if (guardBackend != null) {
                // 门面必须在执行 guest JNI_OnLoad **之前**就绪：guest 在 JNI_OnLoad/RegisterNatives 期间
                // 就可能回调宿主（native 方法入口 / 库装载），此时桥接必须已可用，否则回调会被判为「未接线」。
                installNativeSession(guardBackend, archive, profile, loader);
                loadGuards(guardBackend, guards, loader);
            }
            String spiderClass = string(input, "spider");
            if (spiderClass.isEmpty()) spiderClass = deriveSpiderClass(string(input, "api"));
            if (spiderClass.isEmpty()) throw new IllegalStateException("无法确定 spider 类名");
            // spider 获取：FTY 走 bootstrap Init 的真实链路（Init.init → loader → Init.getSpider）；
            // 其它归档直接加载并实例化壳类。两条路径的失败都**可诊断**（不吞、不静默返回半成品）。
            Object spider = layout.isFty()
                    ? ftySpider(loader, context, spiderClass)
                    : directSpider(loader, spiderClass);
            Class<?> type = spider.getClass();
            // 强制解析方法签名：插件若依赖**未实现**的 android.*，这里会抛 NoClassDefFoundError -> 明确禁用。
            type.getMethods();
            Session session = new Session(type, spider, loader, guardBackend);
            // init 可选；需要 Context 的插件由上面的 Android 最小子集承载。其失败必须**可诊断**，不再吞掉。
            Method init = findMethod(type, "init", 2);
            if (init != null) invokeMethod(spider, init, context, string(input, "ext"));
            handedOff = true;
            return session;
        } finally {
            // open 在任何一步失败：释放已建立的模拟后端与插件类加载器，并摘掉桥接门面，避免跨会话泄漏。
            if (!handedOff) closeQuietly(guardBackend, loader);
        }
    }

    /** FTY bootstrap 里承载 Init/loader/getSpider 的锚点类（DVM 点分名）。 */
    static final String FTY_INIT_CLASS = "com.github.catvod.spider.Init";

    /**
     * FTY（饭太硬）加密 loader 的**真实调用顺序**（据 bootstrap 字节码定位，不停留在库名猜测）：
     * <ol>
     *   <li>{@code Init.init(Context)}：内部调 {@code DexNative.getLoader(Context)} 取回解密后的
     *       {@code dalvik.system.DexClassLoader}（native {@code getLoader} 在模拟器里真实执行）；</li>
     *   <li>{@code Init.getSpider(String)}：内部调 {@code DexNative.getSpider(loader, name)} 返回解密后的
     *       {@code com.github.catvod.crawler.Spider}。</li>
     * </ol>
     * 任一步失败都以带**步骤名**的异常抛出（上层据此归类为 native/缺类等），绝不静默返回半成品。
     * 包可见：供离线自测（{@code HostFtyBootstrapSelfTest}）用假 Init/spider 驱动同一顺序。
     */
    static Object ftySpider(ClassLoader loader, Object context, String spiderClass) throws Exception {
        Class<?> initType;
        try {
            initType = loader.loadClass(FTY_INIT_CLASS);
        } catch (ClassNotFoundException missing) {
            throw new MissingSpiderException(FTY_INIT_CLASS + "（FTY bootstrap 缺少 Init：" + missing + "）");
        }
        // 1) bootstrap Init 初始化（loader）：Init.init(Context)。
        Method init = findMethod(initType, "init", 1);
        if (init == null) {
            throw new FtyBootstrapException("loader(Init.init)", new NoSuchMethodException(FTY_INIT_CLASS + ".init(Context) 未实现"));
        }
        try {
            invokeMethod(null, init, context);
        } catch (Exception | Error failure) {
            throw new FtyBootstrapException("loader(Init.init)", failure);
        }
        // 2) loader → getSpider：Init.getSpider(String)。
        Method getSpider = findMethod(initType, "getSpider", 1);
        if (getSpider == null) {
            throw new FtyBootstrapException("getSpider(Init.getSpider)", new NoSuchMethodException(FTY_INIT_CLASS + ".getSpider(String) 未实现"));
        }
        Object spider;
        try {
            spider = invokeMethod(null, getSpider, spiderClass);
        } catch (Exception | Error failure) {
            throw new FtyBootstrapException("getSpider(Init.getSpider)", failure);
        }
        if (spider == null) {
            throw new FtyBootstrapException("getSpider(Init.getSpider)", new IllegalStateException("Init.getSpider(" + spiderClass + ") 返回空对象"));
        }
        return spider;
    }

    /** 直接加载并实例化 spider 壳类（非 FTY 的普通字节码插件）。 */
    private static Object directSpider(ClassLoader loader, String spiderClass) throws Exception {
        Class<?> type;
        try {
            type = loader.loadClass(spiderClass);
        } catch (ClassNotFoundException missing) {
            throw new MissingSpiderException(spiderClass + "（" + missing + "）");
        }
        // 强制解析方法签名：插件若依赖**未实现**的 android.*，这里会抛 NoClassDefFoundError -> 明确禁用。
        type.getMethods();
        return type.getDeclaredConstructor().newInstance();
    }

    /** 反射调用并展开 {@link InvocationTargetException}：让真正的失败原因（native/缺类/插件异常）原样上抛。 */
    private static Object invokeMethod(Object receiver, Method method, Object... arguments) throws Exception {
        try {
            return method.invoke(receiver, arguments);
        } catch (InvocationTargetException wrapped) {
            Throwable cause = wrapped.getCause();
            if (cause instanceof Error error) throw error;
            if (cause instanceof Exception exception) throw exception;
            throw new IllegalStateException(cause);
        }
    }

    /** 插件包名（本宿主身份，非真实 Android 应用）。 */
    private static final String PLUGIN_PACKAGE_NAME = "dev.vodbox.plugin";

    /**
     * 原生 guard 的**真实前置**：抽取并校验归档里的 ARM64 guard 库（{@code assets/*[-_]v8.so} 约定，
     * 安全边界不动），再在**本进程**内用 Dynarmic 后端真实初始化（{@code loadLibrary} + {@code JNI_OnLoad}），
     * 返回的 {@link DynarmicGuardBackend} 由会话持有，VM/原生生命周期与插件同进程。
     *
     * <p>依赖缺失或初始化失败 → {@link NativeGuards.UnsupportedLibrary} → {@code unsupported_native_library}
     * （含 {@code library}），绝不伪报。抽取产物只落在会话 profile 的隔离目录里，**绝不**被 dlopen 进宿主进程。
     *
     * <p><b>注意</b>：这里成功只代表 guard 在模拟环境里装载并跑过 {@code JNI_OnLoad}，<b>不代表插件可播放</b>；
     * 播放成功与否仍由 {@code detailContent}/{@code playerContent} 的真实返回决定（见 {@link Session}）。
     *
     * <p>普通 guard 与 FTY（饭太硬）走同一条接线；FTY 加密 loader 的**取回 spider** 链路（bootstrap Init →
     * loader → getSpider）由 {@link #ftySpider(ClassLoader, Object, String)} 接上，失败按步骤可诊断。
     */
    private static URLClassLoader createPluginLoader(Path jar, Path cache) throws Exception {
        return dev.vodbox.jarhost.dex.PluginClassLoader.bundled(jar,
                new dev.vodbox.jarhost.dex.DexCache(cache, "vodbox-lazy-v1", 512L * 1024 * 1024),
                dev.vodbox.jarhost.dex.DexToolchain.discover(hostDirectory()),
                NativeRewriterFactory.rewriter(), Host.class.getClassLoader(), hostDirectory());
    }

    private static Map<String, Path> prepareGuards(Path archive, Path profile) throws IOException {
        return NativeGuards.prepareGuards(archive, profile.resolve("native"));
    }

    private static void installNativeSession(DynarmicGuardBackend backend, Path archive, Path profile, ClassLoader loader) {
        NativeMethodBridge.install(backend, loader);
        NativeMethodBridge.installMaterializer(new NativeDexMaterializer(hostDirectory(), profile.resolve("materialized")));
        System.err.println("host: " + FtyGuardLoader.inspect(archive).describe());
    }

    /** 建出 guard 模拟后端（无 guard 时为 null）。依赖不可用时**先释放后端**再明确失败，不留下半开资源。 */
    private static DynarmicGuardBackend createGuardBackend(Map<String, Path> guards, Path profile, Path archive) {
        if (guards.isEmpty()) return null;
        DynarmicGuardBackend backend = new DynarmicGuardBackend(hostDirectory(), profile.resolve("native"), archive, profile.resolve("native"));
        if (!backend.available()) {
            String first = guards.keySet().iterator().next();
            String reason = backend.reason();
            backend.close();
            throw new NativeGuards.UnsupportedLibrary(first, reason);
        }
        return backend;
    }

    /**
     * 用已建好的后端逐个装载 guard（{@code loadLibrary} + {@code JNI_OnLoad}）。
     * 调用方（{@code open}）负责在后端初始化失败时释放它；本方法只负责把失败转成明确分类。
     */
    private static void loadGuards(DynarmicGuardBackend backend, Map<String, Path> guards, ClassLoader loader) {
        try {
            for (Map.Entry<String, Path> guard : guards.entrySet()) {
                boolean fty = NativeGuards.isFtyGuard(guard.getKey());
                System.err.println("host: 初始化 " + (fty ? "FTY" : "普通") + " guard " + guard.getKey());
                DynarmicGuardBackend.Report report = backend.load(guard.getValue(), loader);
                System.err.println("host: " + report.summary());
            }
        } catch (NativeGuards.UnsupportedLibrary unsupported) {
            throw unsupported;
        } catch (Throwable error) {
            String first = guards.keySet().iterator().next();
            throw new NativeGuards.UnsupportedLibrary(first, "ARM64 guard 初始化异常：" + error);
        }
    }

    /** open 失败时的资源清理：先摘掉桥接门面，再关模拟后端与插件类加载器（可重复调用、不抛）。 */
    private static void closeQuietly(DynarmicGuardBackend backend, URLClassLoader loader) {
        NativeMethodBridge.reset();
        if (backend != null) {
            try {
                backend.close();
            } catch (Throwable error) {
                System.err.println("host: 释放 ARM64 模拟后端失败（忽略）：" + error);
            }
        }
        if (loader != null) {
            try {
                loader.close();
            } catch (IOException error) {
                System.err.println("host: 关闭插件类加载器失败（忽略）：" + error);
            }
        }
    }

    // ---------- 原生依赖探针（--probe-native） ----------

    /**
     * {@code --probe-native <request.json>}：**可验证的真实接口调用**——不使用任何固定结论，
     * 而是运行时发现 unidbg 依赖、反射校验必需类型，并在可用时真实构造一次 Dynarmic 后端
     * （触发 {@code libdynarmic} 原生加载与 {@code nativeInitialize}）。
     *
     * <p>把结果信封写到 {@code result}（缺省为 request 同目录的 {@code probe-native.json}），字段：
     * {@code available} / {@code backend} / {@code reason} / {@code licenseNote} / {@code classpath} /
     * {@code loadedLibrary}（可选，按 request 的 {@code library} 真跑一次 load+JNI_OnLoad）。退出码：
     * 0 表示依赖可用（探针真的跑过），1 表示不可用（原因如实报告，**不是**永远 Unavailable）。
     */
    private static int probeNative(Path requestPath) {
        Path resultPath = requestPath.resolveSibling("probe-native.json");
        Map<String, Object> input = new LinkedHashMap<>();
        try {
            input = Json.parseObject(Files.readString(requestPath, StandardCharsets.UTF_8));
            if (!string(input, "result").isEmpty()) resultPath = Path.of(string(input, "result"));
        } catch (IOException error) {
            System.err.println("host: cannot read probe request: " + error);
        }
        String profileValue = string(input, "profile");
        Path workRoot = profileValue.isEmpty() ? requestPath.getParent().resolve("native") : Path.of(profileValue).resolve("native");

        DynarmicGuardBackend backend = new DynarmicGuardBackend(hostDirectory(), workRoot);
        Map<String, Object> response = new LinkedHashMap<>();
        response.put("backend", backend.name());
        response.put("available", backend.available());
        response.put("licenseNote", UnidbgToolchain.LICENSE_NOTE);
        response.put("classpath", UnidbgToolchain.discover(hostDirectory()).stream().map(Path::toString).toList());
        if (!backend.available()) {
            response.put("reason", backend.reason());
            response.put("loadedLibrary", "skipped（依赖不可用）");
            response.put("uncovered", UNCOVERED_CAPABILITIES);
            try {
                write(resultPath, response);
            } catch (IOException error) {
                System.err.println("host: cannot write probe result: " + error);
                return 2;
            }
            return 1;
        }
        response.put("reason", backend.reason());
        String libraryValue = string(input, "library");
        if (!libraryValue.isEmpty()) {
            try {
                DynarmicGuardBackend.Report report = backend.load(Path.of(libraryValue), Host.class.getClassLoader());
                response.put("loadedLibrary", report.summary());
            } catch (Throwable error) {
                response.put("loadedLibrary", "failed: " + error);
            }
        } else {
            response.put("loadedLibrary", "skipped（request 未提供 library 路径）");
        }
        response.put("uncovered", UNCOVERED_CAPABILITIES);
        try {
            backend.close();
        } catch (RuntimeException ignored) {
            // 探针收尾，失败不影响结论。
        }
        try {
            write(resultPath, response);
        } catch (IOException error) {
            System.err.println("host: cannot write probe result: " + error);
            return 2;
        }
        return 0;
    }

    /** 本层在原生模拟上**未覆盖**的能力（诚实报告，不臆造支持）。 */
    private static final List<String> UNCOVERED_CAPABILITIES = List.of(
            "原生 guard 的真实执行依赖运行时配置的 unidbg + Dynarmic 依赖；未配置时含 guard 的归档一律 unsupported_native_library",
            "FTY 解密产物（原生 .dex）须可经 DEX→JVM 物化；转换器缺失或产物不可加载时仍明确失败，绝不当作可加载",
            "win-arm64：发布 jar 无原生二进制（osx-arm64/osx-x64/linux-x64/linux-arm64/win-x64 之外）",
            "Unicorn/Keystone 为 GPL-2.0，本仓库不随包捆绑、不修改许可证");

    /**
     * 装配 Android 最小子集的 {@link Application}：目录、外部存储根、资源根都由本会话 profile 派生，
     * 并把插件 JAR 内 {@code assets/**} 解包到资源根。任何未实现的系统服务调用都会被
     * {@link DesktopContext#getSystemService(String)} 明确拒绝。
     */
    private static Object createAndroidContext(ClassLoader loader, Path profile, Path archive) throws IOException {
        Files.createDirectories(profile);
        Path external = profile.resolve("external");
        Path assets = profile.resolve("assets");
        extractAssets(archive, assets);
        Environment.configure(profile.resolve("data").toFile(), external.toFile());
        DesktopContext desktop = new DesktopContext(
                loader, PLUGIN_PACKAGE_NAME, profile.toFile(), external.toFile(), assets.toFile(),
                archive.toAbsolutePath().toString(), "");
        Application application = new Application();
        application.attachBaseContext(desktop);
        return application;
    }

    /** 把插件 JAR 里的 {@code assets/**} 解包到 {@code assetsRoot}（有总量上限，失败只告警不阻断）。 */
    private static void extractAssets(Path archive, Path assetsRoot) {
        if (archive == null || !Files.isRegularFile(archive)) return;
        final long maxTotal = 32L * 1024 * 1024;
        long total = 0;
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            var entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                if (entry.isDirectory()) continue;
                String name = entry.getName();
                if (!name.startsWith("assets/")) continue;
                String relative = name.substring("assets/".length());
                if (relative.isEmpty()) continue;
                Path target = assetsRoot.resolve(relative).normalize();
                if (!target.startsWith(assetsRoot)) continue;
                long size = Math.max(0L, entry.getSize());
                if (total + size > maxTotal) {
                    System.err.println("host: 插件 assets 超过上限，跳过 " + relative);
                    continue;
                }
                total += size;
                Files.createDirectories(target.getParent());
                try (InputStream input = zip.getInputStream(entry)) {
                    Files.copy(input, target, StandardCopyOption.REPLACE_EXISTING);
                }
            }
        } catch (IOException error) {
            System.err.println("host: 解包插件 assets 失败（忽略）：" + error);
        }
    }

    /** serve 模式下把 DEX 归档转换为可加载 JAR；转换事实写进 stderr 便于诊断。 */
    private static Path convertForServe(Path archive, Path cache) throws DexException {
        DexCache dexCache = new DexCache(cache.resolve("converted"), hostVersion(), 60_000);
        dexCache.sweepTemporaries(10 * 60_000);
        Path converted = dexCache.resolve(archive, DexToolchain.discover(hostDirectory()));
        System.err.println("host: converted DEX archive " + archive.getFileName() + " -> " + converted.getFileName());
        return converted;
    }

    private static String deriveSpiderClass(String api) {
        if (api.isEmpty()) return "";
        String name = api.startsWith("csp_") ? api.substring(4) : api;
        return name.isEmpty() ? "" : "com.github.catvod.spider." + name;
    }

    /** 宿主自身 JAR 所在目录（用于发现随包转换器）。以目录形式运行时返回其目录。 */
    private static Path hostDirectory() {
        Path jar = hostJar();
        if (jar == null) return null;
        return Files.isDirectory(jar) ? jar : jar.getParent();
    }

    private static Path hostJar() {
        try {
            java.security.CodeSource source = Host.class.getProtectionDomain().getCodeSource();
            if (source != null && source.getLocation() != null) return Path.of(source.getLocation().toURI());
        } catch (Exception ignored) {
            // 无代码源（极少见）：退回 unknown。
        }
        return null;
    }

    /** 宿主版本：宿主 JAR 的内容哈希前 16 位；供转换缓存失效。取不到时返回 "unknown"。 */
    private static String hostVersion() {
        Path jar = hostJar();
        if (jar == null || !Files.isRegularFile(jar)) return "unknown";
        try {
            return DexCache.sha256(jar).substring(0, 16);
        } catch (DexException error) {
            return "unknown";
        }
    }

    // ---------- 服务循环 ----------

    private static void serve(Session session, Path cache) {
        Path responses = cache.resolve("responses");
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) {
                Map<String, Object> response;
                String id = "";
                try {
                    Map<String, Object> command = Json.parseObject(line);
                    id = string(command, "id");
                    if (!isValidId(id)) continue;
                    @SuppressWarnings("unchecked")
                    Map<String, Object> params = command.get("params") instanceof Map ? (Map<String, Object>) command.get("params") : new LinkedHashMap<>();
                    response = map("result", session.request(params));
                } catch (Throwable error) {
                    response = failure(error);
                }
                try {
                    write(responses.resolve(id + ".json"), response);
                } catch (IOException error) {
                    System.err.println("host: cannot write response: " + error);
                }
            }
        } catch (IOException error) {
            System.err.println("host: stdin closed: " + error);
        }
        // stdin 关闭：先释放 ARM64 模拟后端（VM/原生库生命周期），再退出。
        session.close();
        // 插件可能留有非守护线程，直接 halt。
        Runtime.getRuntime().halt(0);
    }

    private static boolean isValidId(String id) {
        if (id.isEmpty() || id.length() > 80) return false;
        for (int i = 0; i < id.length(); i++) {
            char c = id.charAt(i);
            if (!Character.isLetterOrDigit(c) && c != '-') return false;
        }
        return true;
    }

    // ---------- 一次请求 ----------

    private static final class Session {
        private final Class<?> type;
        private final Object spider;
        @SuppressWarnings("unused")
        private final URLClassLoader loader;
        /** 本会话持有的 ARM64 模拟后端（无 guard 时为 null）；VM/原生库的生命周期与插件同进程。 */
        private final DynarmicGuardBackend guardBackend;
        private String detailId = "";

        Session(Class<?> type, Object spider, URLClassLoader loader, DynarmicGuardBackend guardBackend) {
            this.type = type;
            this.spider = spider;
            this.loader = loader;
            this.guardBackend = guardBackend;
        }

        /** 释放本会话的资源：桥接门面 + 模拟后端（若已装载）+ 插件类加载器。 */
        void close() {
            NativeMethodBridge.reset();
            if (guardBackend != null) guardBackend.close();
            try {
                loader.close();
            } catch (IOException error) {
                System.err.println("host: 关闭插件类加载器失败（忽略）：" + error);
            }
        }

        String request(Map<String, Object> params) throws Exception {
            if (has(params, "play")) {
                String required = string(params, "detailID");
                if (!required.isEmpty() && !required.equals(detailId)) {
                    detailContent(required);
                }
                String result = playerContent(string(params, "flag"), string(params, "play"));
                return requireContent(result, "playerContent");
            }
            if (has(params, "ids")) return detailContent(string(params, "ids"));
            if (has(params, "wd")) return searchContent(string(params, "wd"), string(params, "pg"));
            if (has(params, "t")) return categoryContent(string(params, "t"), string(params, "pg"), string(params, "extend"));
            String home = homeContent();
            String videos = homeVideoContent();
            if (videos != null && !videos.isEmpty()) return mergeVideos(home, videos);
            return home;
        }

        private String detailContent(String ids) throws Exception {
            Object result = call("detailContent", 1, new ArrayList<>(List.of(ids)));
            detailId = ids;
            return requireContent(asString(result), "detailContent");
        }

        private String homeContent() throws Exception {
            Object result = call("homeContent", 1, Boolean.TRUE);
            return orEmpty(asString(result));
        }

        private String homeVideoContent() {
            try {
                Method method = findMethod(type, "homeVideoContent", 0);
                if (method == null) return null;
                return asString(method.invoke(spider));
            } catch (Throwable ignored) {
                return null;
            }
        }

        private String categoryContent(String tid, String pg, String extend) throws Exception {
            Map<String, Object> filters = new LinkedHashMap<>();
            if (!extend.isEmpty()) {
                Object parsed = Json.parse(extend);
                if (parsed instanceof Map) {
                    @SuppressWarnings("unchecked")
                    Map<String, Object> values = (Map<String, Object>) parsed;
                    filters.putAll(values);
                }
            }
            Object result = call("categoryContent", 4, tid, pg.isEmpty() ? "1" : pg, Boolean.TRUE, filters);
            return orEmpty(asString(result));
        }

        private String searchContent(String key, String pg) throws Exception {
            Method three = findMethod(type, "searchContent", 3);
            Object result = three != null
                    ? three.invoke(spider, key, Boolean.FALSE, pg.isEmpty() ? "1" : pg)
                    : call("searchContent", 2, key, Boolean.FALSE);
            return orEmpty(asString(result));
        }

        private String playerContent(String flag, String play) throws Exception {
            Object result = call("playerContent", 3, flag, play, new ArrayList<String>());
            return requireContent(asString(result), "playerContent");
        }

        private Object call(String name, int arity, Object... args) throws Exception {
            Method method = findMethod(type, name, arity);
            if (method == null) throw new NoSuchMethodException(name + "/" + arity + " 未实现");
            try {
                return method.invoke(spider, args);
            } catch (InvocationTargetException wrapped) {
                Throwable cause = wrapped.getCause();
                if (cause instanceof Exception) throw (Exception) cause;
                throw (Error) cause;
            }
        }

        /** 首页/分类/搜索允许空结果（搜索专用源合法返回空），以 "{}" 表示。 */
        private static String orEmpty(String value) {
            return value == null || value.isBlank() ? "{}" : value;
        }

        /** 详情/播放不允许空结果：空响应必须显式失败，不能伪装成可播放。 */
        private static String requireContent(String value, String operation) throws EmptyContentException {
            if (value == null || value.isBlank()) throw new EmptyContentException(operation + " 未返回内容");
            return value;
        }

        /** home 与 homeVideoContent 的建议列表合并：推荐列表存在时覆盖 union。 */
        private static String mergeVideos(String home, String videos) {
            try {
                Map<String, Object> base = Json.parseObject(home);
                Map<String, Object> extra = Json.parseObject(videos);
                Object list = extra.get("list");
                if (list instanceof List && !((List<?>) list).isEmpty()) base.put("list", list);
                return Json.stringify(base);
            } catch (RuntimeException ignored) {
                return home;
            }
        }
    }

    // ---------- 反射工具 ----------

    private static Method findMethod(Class<?> type, String name, int arity) {
        for (Method method : type.getMethods()) {
            if (method.getName().equals(name) && method.getParameterCount() == arity) return method;
        }
        return null;
    }

    private static String asString(Object value) {
        return value == null ? null : String.valueOf(value);
    }

    private static boolean has(Map<String, Object> params, String key) {
        return params.containsKey(key) && params.get(key) != null;
    }

    private static String string(Map<String, Object> map, String key) {
        Object value = map.get(key);
        return value == null ? "" : String.valueOf(value);
    }

    private static Map<String, Object> map(String key, Object value) {
        Map<String, Object> result = new LinkedHashMap<>();
        result.put(key, value);
        return result;
    }

    // ---------- 失败分类 ----------

    private static Map<String, Object> failure(Throwable error) {
        error.printStackTrace(System.err);
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("error", describe(error));
        result.put("trace", trace(error));
        if (error instanceof DexException dex) {
            result.put("errorCode", dex.code());
        } else if (error instanceof EmptyContentException) {
            result.put("errorCode", "source_empty");
        } else if (unsupportedLibrary(error) != null) {
            NativeGuards.UnsupportedLibrary unsupported = unsupportedLibrary(error);
            // ARM64 原生 guard 无法在隔离环境模拟（缺依赖 / 非 ARM64 / 初始化失败）：明确禁用，不伪装可播。
            result.put("errorCode", "unsupported_native_library");
            result.put("library", unsupported.library);
        } else if (error instanceof MissingSpiderException) {
            result.put("errorCode", "plugin_class_missing");
            result.put("missingClass", error.getMessage());
        } else {
            Throwable cause = rootCause(error);
            if (cause instanceof UnsatisfiedLinkError) {
                result.put("errorCode", "unsupported_native_library");
                return result;
            }
            // 缺类/缺方法/缺字段等链接错误若指向 Android 框架，一律是 unsupported_android
            // （插件引用了本宿主未实现的 android.*/androidx.*/dalvik.* 成员）。
            String reference = androidFrameworkReference(cause);
            if (reference != null) {
                result.put("errorCode", "unsupported_android");
                result.put("missingClass", reference);
            } else if (cause instanceof NoClassDefFoundError || cause instanceof ClassNotFoundException) {
                result.put("errorCode", "class_conversion");
                String missing = missingClass(cause);
                if (missing != null) result.put("missingClass", missing);
            } else if (cause instanceof UnsupportedOperationException) {
                // 本宿主对未实现的系统服务/能力主动抛出的明确拒绝。
                result.put("errorCode", "unsupported_android");
            } else {
                result.put("errorCode", "plugin_crash");
            }
        }
        return result;
    }

    /** 匹配报文里出现的 Android 框架类型引用（ClassNotFound/NoSuchMethod/NoSuchField 等消息）。 */
    private static final Pattern ANDROID_REFERENCE = Pattern.compile("(?:androidx?|dalvik)\\.[A-Za-z0-9_.$]+");

    /**
     * 从链接错误的报文里提取 Android 框架的所属类型；没有则返回 null。
     * 例：{@code android/content/Context} → {@code android.content.Context}；
     * {@code android.content.Context.getResources()} → {@code android.content.Context}。
     */
    private static String androidFrameworkReference(Throwable error) {
        String message = error == null ? null : error.getMessage();
        if (message == null) return null;
        Matcher matcher = ANDROID_REFERENCE.matcher(message.replace('/', '.'));
        if (!matcher.find()) return null;
        String reference = matcher.group();
        int paren = reference.indexOf('(');
        if (paren >= 0) reference = reference.substring(0, paren);
        int lastDot = reference.lastIndexOf('.');
        if (lastDot > 0 && Character.isLowerCase(reference.charAt(lastDot + 1))) {
            return reference.substring(0, lastDot);
        }
        return reference;
    }

    /** 在异常链上找 {@link NativeGuards.UnsupportedLibrary}（可能被反射/桥接包装）。 */
    private static NativeGuards.UnsupportedLibrary unsupportedLibrary(Throwable error) {
        java.util.Set<Throwable> seen = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
        for (Throwable current = error; current != null && seen.add(current); current = current.getCause()) {
            if (current instanceof NativeGuards.UnsupportedLibrary unsupported) return unsupported;
        }
        return null;
    }

    private static String describe(Throwable error) {
        Throwable cause = rootCause(error);
        return cause == error ? error.toString() : error + ": " + cause;
    }

    private static Throwable rootCause(Throwable error) {
        Throwable current = error;
        while (current.getCause() != null && current.getCause() != current) current = current.getCause();
        return current;
    }

    private static String missingClass(Throwable error) {
        if (error == null || error.getMessage() == null) return null;
        if (error instanceof NoClassDefFoundError || error instanceof ClassNotFoundException) {
            return error.getMessage().replace('/', '.').split("\\s+")[0];
        }
        return null;
    }

    private static String trace(Throwable error) {
        StringBuilder builder = new StringBuilder();
        for (StackTraceElement frame : error.getStackTrace()) {
            if (frame.getClassName().startsWith("jdk.internal.reflect.") || frame.getClassName().startsWith("java.lang.reflect.")) continue;
            if (builder.length() > 4000) { builder.append("\n\t..."); break; }
            builder.append("\tat ").append(frame).append('\n');
        }
        return builder.toString();
    }

    // ---------- 原子写出 ----------

    private static void write(Path file, Map<String, Object> response) throws IOException {
        Path temporary = file.resolveSibling(file.getFileName() + ".tmp");
        Files.writeString(temporary, Json.stringify(response), StandardCharsets.UTF_8);
        try {
            Files.move(temporary, file, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException atomicUnsupported) {
            Files.move(temporary, file, StandardCopyOption.REPLACE_EXISTING);
        }
    }

    private static final class MissingSpiderException extends Exception {
        MissingSpiderException(String message) { super(message); }
    }

    /** FTY bootstrap 的某一步（loader/getSpider）失败：携带步骤名便于诊断，原因保留在 cause 链供上层分类。 */
    private static final class FtyBootstrapException extends Exception {
        FtyBootstrapException(String step, Throwable cause) {
            super("FTY bootstrap 失败（" + step + "）：" + cause, cause);
        }
    }

    private static final class EmptyContentException extends Exception {
        EmptyContentException(String message) { super(message); }
    }
}
