package dev.vodbox.jarhost.guard;

import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

/**
 * ARM64 原生 guard 的**真实调用链**（独立实现，只在运行时经反射使用公开 unidbg API）。
 *
 * <p>整条链路与插件**在同一个 Java 进程**内完成，VM 与已加载的原生库的生命周期由本对象持有：
 * <ol>
 *   <li>{@code AndroidEmulatorBuilder.for64Bit().setProcessName(..).setRootDir(..).addBackendFactory(new DynarmicFactory(false))}；
 *       后端显式取 <b>Dynarmic</b>（{@code fallbackUnicorn=false}，加载失败即抛，不静默回退 Unicorn）；</li>
 *   <li>{@code emulator.setTimeout(..)} + {@code memory.setLibraryResolver(new AndroidResolver(23))}；</li>
 *   <li>{@code vm = emulator.createDalvikVM()}；把 {@code ProxyClassFactory} 的类名映射接到**现有反射宿主**
 *       （本进程的插件 {@link ClassLoader}），并注入 {@link GuardJniHost} 作为 JNI 兜底；</li>
 *   <li>{@code AndroidModule} 注册（AAsset 等虚拟系统调用）；</li>
 *   <li>{@code vm.loadLibrary(file, true)} → {@code DalvikModule.callJNI_OnLoad(emulator)}：**真正执行 guest
 *       {@code JNI_OnLoad}**；</li>
 *   <li>{@link #invokeNative(String, String, Object...)}：调用 guest 导出的**实际 native 方法入口**。</li>
 * </ol>
 *
 * <p><b>绝不下载/加载 guest 的 {@code .so} 进宿主进程</b>：它只被 unidbg 以字节方式读入 guest 内存并模拟执行。
 * 只有 **Dynarmic 引擎**（宿主架构的 {@code libdynarmic}）会经 {@code native-lib-loader} 被加载——
 * 那是模拟引擎，不是插件库。
 *
 * <p><b>诚实边界</b>：{@code Emulated}（本对象成功 {@link #load}）<b>不等于</b>插件可播放；
 * 播放是否成功仍由宿主的 {@code playerContent} 等真实调用决定。任何失败都以
 * {@link NativeGuards.UnsupportedLibrary} 明确抛出（→ {@code unsupported_native_library}），
 * 绝不把「没做」当成「已模拟」。
 */
public final class DynarmicGuardBackend implements AutoCloseable, NativeCallSession {
    /** unidbg 把该值透传给引擎的 {@code emu_start} 超时；与 C# 侧 guard 限制（30s）一致。 */
    private static final long EMULATION_TIMEOUT_MICROS = 30_000_000L;
    /** guest 进程名（模拟 Android 应用的进程名，仅用于诊断）。 */
    private static final String GUEST_PROCESS_NAME = "com.fongmi.android.tv";

    private final UnidbgToolchain.Resolution resolution;
    private final Path workRoot;
    /** 插件归档（按名解析 ARM64 库用）；probe 模式下可为 null。 */
    private final Path archive;
    /** guard 抽取目录（{@link NativeGuards#prepareGuards} 的落点）。 */
    private final Path libraryDirectory;
    private Object emulator;
    private Object vm;
    /** guest 模块（{@code JNI_OnLoad} 所在）；导出符号回退用。 */
    private Object dalvikModule;
    private ClassLoader reflectionHost;
    private String loadedLibrary;
    private Path guestRoot;
    /** 外层 native 调用进行中时，guest 触发的嵌套装载被推迟到这里（模拟器不支持嵌套执行）。 */
    private final List<Path> deferred = new ArrayList<>();
    /** 已装载库的真实路径（去重，避免重复装载）。 */
    private final Set<String> loadedLibraries = new LinkedHashSet<>();
    /** 已装载库的叶名（按名去重：对齐 Android 按名装载语义，避免同一 guard 二次装载/二次 JNI_OnLoad）。 */
    private final Set<String> loadedLeaves = new LinkedHashSet<>();
    private int depth;

    public DynarmicGuardBackend(Path hostJarDirectory, Path workRoot) {
        this(hostJarDirectory, workRoot, null, null);
    }

    /**
     * 会话级构造：额外接收插件归档与 guard 抽取目录，供 {@link #load}/{@link #loadLibrary} 按名解析库。
     * probe 场景可只传前两个参数（archive/libraryDirectory 为 null，按名解析将明确失败）。
     */
    public DynarmicGuardBackend(Path hostJarDirectory, Path workRoot, Path archive, Path libraryDirectory) {
        this.resolution = UnidbgToolchain.resolve(hostJarDirectory);
        this.workRoot = workRoot;
        this.archive = archive;
        this.libraryDirectory = libraryDirectory;
    }

    /** 后端标识（诊断用）。 */
    public String name() {
        return resolution.available() ? "unidbg-dynarmic" : "unidbg-unavailable";
    }

    /** 依赖是否就绪（反射校验通过）。false 时 {@link #load} 直接以明确原因失败。 */
    public boolean available() {
        return resolution.available();
    }

    /** 依赖不可用/许可的明确原因（永远可读，用于错误信封与诊断）。 */
    public String reason() {
        return resolution.available() ? resolution.note : resolution.unavailableReason;
    }

    /**
     * 真实初始化一个 guard 库：建 Dynarmic 后端 → DVM → 反射宿主 → {@code loadLibrary} → {@code JNI_OnLoad}。
     * 生命周期保持到 {@link #close()}；失败抛出带明确原因的 {@link NativeGuards.UnsupportedLibrary}。
     *
     * @param library        隔离目录里经 ARM64 ELF 校验过的 guard 库
     * @param pluginLoader   现有反射宿主（插件 {@link ClassLoader}）；null 时退化为宿主的类加载器
     */
    public Report load(Path library, ClassLoader pluginLoader) {
        String leaf = library == null ? "(unknown)" : String.valueOf(library.getFileName());
        if (!resolution.available()) throw new NativeGuards.UnsupportedLibrary(leaf, resolution.unavailableReason);
        if (library == null || !Files.isRegularFile(library)) {
            throw new NativeGuards.UnsupportedLibrary(leaf, "隔离目录里的 ARM64 guard 库不存在");
        }
        this.reflectionHost = pluginLoader != null ? pluginLoader : DynarmicGuardBackend.class.getClassLoader();
        long started = System.nanoTime();
        try {
            boolean onLoad;
            if (vm == null) {
                // 首次：建 Dynarmic 后端并真正执行 guest JNI_OnLoad（含 RegisterNatives）。
                onLoad = bootstrap(library);
            } else {
                // 已 bootstrap：**复用同一模拟器**，绝不重建 VM（重复 bootstrap 会丢掉已注册的 native/模块）。
                loadModule(library);
                onLoad = hasSymbol(dalvikModule);
                loadedLibrary = String.valueOf(library.getFileName());
            }
            rememberLoaded(library);
            long elapsed = (System.nanoTime() - started) / 1_000_000L;
            return new Report(leaf, onLoad, elapsed, resolution.note);
        } catch (NativeGuards.UnsupportedLibrary unsupported) {
            close();
            throw unsupported;
        } catch (Throwable error) {
            close();
            throw new NativeGuards.UnsupportedLibrary(leaf,
                    "ARM64 guard 在隔离模拟环境初始化失败：" + describe(error));
        }
    }

    // ---------- NativeCallSession：插件 native 方法的透明桥接入口 ----------

    /**
     * 调用 guest 导出的 native 方法入口（静态/实例、多类型参数/返回）。
     * 与插件里被 {@link NativeBytecodeRewriter} 去 native 化后的方法一一对应：{@code receiver == null} 即静态方法。
     *
     * <p>外层调用进行中时，guest 触发的嵌套装载会被推迟（模拟器不能嵌套执行），返回前再补跑。
     */
    @Override
    public Object invoke(String owner, String nameAndDescriptor, Object receiver, Object[] values) {
        if (vm == null) {
            throw new NativeGuards.UnsupportedLibrary(owner,
                    "尚未在模拟环境加载 guard 库，native 方法无法执行（后端不可用或未 load）");
        }
        loadDeferred();
        depth++;
        try {
            return callNative(owner, nameAndDescriptor, receiver, values == null ? new Object[0] : values);
        } finally {
            depth--;
            loadDeferred();
        }
    }

    /** 兼容旧调用名：静态方法 + 全参数。 */
    public Object invokeNative(String owner, String signature, Object... values) {
        return invoke(owner, signature, null, values);
    }

    private Object callNative(String owner, String nameAndDescriptor, Object receiver, Object[] values) {
        int open = nameAndDescriptor.indexOf('(');
        if (open < 0) throw new NativeGuards.UnsupportedLibrary(owner, "非法 JNI 签名（缺参数表）：" + nameAndDescriptor);
        String descriptor = nameAndDescriptor.substring(open);
        char returns = descriptor.charAt(descriptor.indexOf(')') + 1);
        try {
            Class<?> proxyObjectType = resolution.require("com.github.unidbg.linux.android.dvm.jni.ProxyDvmObject");
            Object dvmClass = Reflect.call(vm, "resolveClass", owner);
            boolean[] objectArguments = objectArguments(descriptor);
            Object[] arguments = new Object[values.length];
            for (int i = 0; i < values.length; i++) {
                arguments[i] = i < objectArguments.length && objectArguments[i]
                        ? GuardJniHost.ProxyDvmObjectBridge.create(proxyObjectType, vm, values[i])
                        : values[i];
            }
            Object dvmReceiver = receiver == null ? null : GuardJniHost.ProxyDvmObjectBridge.create(proxyObjectType, vm, receiver);
            Object result = callJniMethod(dvmClass, dvmReceiver, returns, nameAndDescriptor, arguments);
            return unwrapResult(result, returns, descriptor);
        } catch (NativeGuards.UnsupportedLibrary passthrough) {
            throw passthrough;
        } catch (Throwable error) {
            throw new NativeGuards.UnsupportedLibrary(owner,
                    "native 方法入口调用失败：" + nameAndDescriptor + "（" + describe(error) + "）");
        }
    }

    /**
     * 按返回类型选择 unidbg 的强类型入口（全部经反射；不存在时如实抛出，不静默降级到错误类型）。
     * JNI 注册由 guest 的 {@code JNI_OnLoad}（{@code RegisterNatives}）或导出符号完成——本条链路的
     * {@link #bootstrap} 已真正执行过它；若方法未注册，下面的调用会明确失败并被归类。
     */
    private Object callJniMethod(Object dvmClass, Object dvmReceiver, char returns, String signature, Object[] arguments) {
        String suffix;
        switch (returns) {
            case 'V': suffix = ""; break;
            case 'Z': suffix = "Boolean"; break;
            case 'J': suffix = "Long"; break;
            case 'B': case 'C': case 'S': case 'I': suffix = "Int"; break;
            case 'F': suffix = "Float"; break;
            case 'D': suffix = "Double"; break;
            case 'L': case '[': suffix = "Object"; break;
            default: throw new UnsupportedOperationException("无法识别的 JNI 返回类型：" + returns + "（" + signature + "）");
        }
        String methodName = (dvmReceiver == null ? "callStaticJniMethod" : "callJniMethod") + suffix;
        Object target = dvmReceiver == null ? dvmClass : dvmReceiver;
        return Reflect.call(target, methodName, emulator, signature, arguments);
    }

    /** 把 unidbg 的返回值解包成改写后方法体期望的对象形态（原始类型装箱、对象数组按描述符重建）。 */
    private Object unwrapResult(Object result, char returns, String descriptor) {
        if (result == null) return null;
        switch (returns) {
            case 'V': return null;
            case 'Z': case 'J': case 'F': case 'D': return result;
            case 'I': return Integer.valueOf(((Number) result).intValue());
            case 'B': return Byte.valueOf(((Number) result).byteValue());
            case 'S': return Short.valueOf(((Number) result).shortValue());
            case 'C': return Character.valueOf((char) ((Number) result).intValue());
            default: break;
        }
        Object value = Reflect.call(result, "getValue");
        if (returns == '[' && value instanceof Object[] elements && isDvmObjectArray(elements)) {
            return rebuildArray(elements, descriptor);
        }
        return value;
    }

    /** 按描述符的组件类型把 {@code DvmObject[]} 重建为真正的 Java 数组。 */
    private Object rebuildArray(Object[] elements, String descriptor) {
        try {
            Class<?> component = componentClass(descriptor.substring(1));
            Object array = java.lang.reflect.Array.newInstance(component, elements.length);
            for (int i = 0; i < elements.length; i++) {
                Object element = elements[i];
                java.lang.reflect.Array.set(array, i, element == null ? null : Reflect.call(element, "getValue"));
            }
            return array;
        } catch (ClassNotFoundException absent) {
            throw new IllegalStateException("数组组件类型不可加载：" + descriptor, absent);
        }
    }

    private Class<?> componentClass(String descriptor) throws ClassNotFoundException {
        if (descriptor.startsWith("L") && descriptor.endsWith(";")) {
            return Class.forName(descriptor.substring(1, descriptor.length() - 1).replace('/', '.'), false, reflectionHost);
        }
        switch (descriptor.charAt(0)) {
            case 'Z': return boolean.class;
            case 'B': return byte.class;
            case 'C': return char.class;
            case 'S': return short.class;
            case 'I': return int.class;
            case 'J': return long.class;
            case 'F': return float.class;
            case 'D': return double.class;
            case '[': return java.lang.reflect.Array.newInstance(componentClass(descriptor.substring(1)), 0).getClass();
            default: throw new ClassNotFoundException(descriptor);
        }
    }

    private static boolean isDvmObjectArray(Object[] elements) {
        for (Object element : elements) {
            if (element == null) continue;
            for (Class<?> type = element.getClass(); type != null; type = type.getSuperclass()) {
                if ("com.github.unidbg.linux.android.dvm.DvmObject".equals(type.getName())) return true;
            }
            return false;
        }
        return false;
    }

    /** 解析参数表，标记每个参数是否为对象/数组类型（对象/数组需经 {@code ProxyDvmObject.createObject} 包装）。 */
    private static boolean[] objectArguments(String descriptor) {
        List<Boolean> sorts = new ArrayList<>();
        int index = descriptor.indexOf('(') + 1;
        while (index < descriptor.length() && descriptor.charAt(index) != ')') {
            char current = descriptor.charAt(index);
            boolean object = false;
            while (current == '[') {
                object = true;
                index++;
                current = descriptor.charAt(index);
            }
            if (current == 'L') {
                object = true;
                index = descriptor.indexOf(';', index) + 1;
            } else {
                index++;
            }
            sorts.add(object);
        }
        boolean[] result = new boolean[sorts.size()];
        for (int i = 0; i < result.length; i++) result[i] = sorts.get(i);
        return result;
    }

    // ---------- 库装载（guest 只进模拟器，绝不进宿主进程） ----------

    /** 装载归档里某个具体路径的 ARM64 库；路径已失效时按叶名回退到归档解析。 */
    @Override
    public void load(String path) {
        Path file = resolveLoadPath(path);
        loadFile(file);
    }

    /** 按库名装载归档里的 ARM64 库。 */
    @Override
    public void loadLibrary(String name) {
        if (archive == null || libraryDirectory == null) {
            throw new NativeGuards.UnsupportedLibrary(String.valueOf(name), "未提供插件归档，无法按名解析原生库");
        }
        try {
            Path resolved = NativeGuards.resolve(archive, name, libraryDirectory);
            if (resolved == null) throw new NativeGuards.UnsupportedLibrary(String.valueOf(name), "归档里找不到该 ARM64 库");
            loadFile(resolved);
        } catch (NativeGuards.UnsupportedLibrary unsupported) {
            throw unsupported;
        } catch (Throwable error) {
            throw new NativeGuards.UnsupportedLibrary(String.valueOf(name), "按名装载插件原生库失败：" + describe(error));
        }
    }

    private Path resolveLoadPath(String path) {
        if (path == null || path.isBlank()) throw new NativeGuards.UnsupportedLibrary("(unknown)", "装载路径为空");
        Path direct;
        try {
            direct = Path.of(path);
        } catch (RuntimeException malformed) {
            throw new NativeGuards.UnsupportedLibrary("(unknown)", "装载路径非法：" + path);
        }
        if (Files.isRegularFile(direct)) return direct;
        String host = hostPath(path);
        if (!host.equals(path)) {
            Path mapped = Path.of(host);
            if (Files.isRegularFile(mapped)) return mapped;
        }
        String leaf = direct.getFileName() == null ? "" : direct.getFileName().toString();
        if (archive != null && libraryDirectory != null && !leaf.isEmpty()) {
            String name = leaf.endsWith(".so") ? leaf.substring(0, leaf.length() - 3) : leaf;
            try {
                Path resolved = NativeGuards.resolve(archive, name, libraryDirectory);
                if (resolved != null) return resolved;
            } catch (java.io.IOException error) {
                throw new NativeGuards.UnsupportedLibrary(leaf, "抽取原生库失败：" + error.getMessage());
            }
        }
        throw new NativeGuards.UnsupportedLibrary(leaf.isEmpty() ? "(unknown)" : leaf,
                "隔离目录里找不到该 ARM64 库：" + path);
    }

    private void loadFile(Path file) {
        String leaf = file.getFileName() == null ? "(unknown)" : file.getFileName().toString();
        if (!resolution.available()) throw new NativeGuards.UnsupportedLibrary(leaf, resolution.unavailableReason);
        if (!Files.isRegularFile(file)) throw new NativeGuards.UnsupportedLibrary(leaf, "要装载的 ARM64 库不存在");
        try {
            String canonical = file.toRealPath().toString();
            // 同一库（按真实路径或叶名）只装载一次：FTY 的 guest 会先自解包 guard 再 System.load，
            // 那与本层预装载是同一库；按名去重避免二次装载/二次 JNI_OnLoad（重复 RegisterNatives）。
            if (loadedLibraries.contains(canonical) || loadedLeaves.contains(leaf)) return;
            NativeGuards.validateArm64(readPrefix(file), leaf);
            if (vm == null) {
                bootstrap(file);
                loadedLibraries.add(canonical);
                loadedLeaves.add(leaf);
                return;
            }
            if (depth > 0) {
                defer(file, canonical, leaf);
                return;
            }
            loadModule(file);
            loadedLibraries.add(canonical);
            loadedLeaves.add(leaf);
        } catch (NativeGuards.UnsupportedLibrary unsupported) {
            throw unsupported;
        } catch (Throwable error) {
            throw new NativeGuards.UnsupportedLibrary(leaf, "ARM64 库装载失败：" + describe(error));
        }
    }

    private void loadModule(Path file) throws Exception {
        Object module = Reflect.call(vm, "loadLibrary", file.toFile(), Boolean.TRUE);
        dalvikModule = module;
        Reflect.call(module, "callJNI_OnLoad", emulator);   // 真正执行 guest JNI_OnLoad（含 RegisterNatives）
    }

    /** guest 调 Java 又触发装载：复制后推迟到外层调用返回时补跑（模拟器不能嵌套执行）。 */
    private void defer(Path file, String canonical, String leaf) throws IOException {
        Path directory = workRoot.resolve("native-deferred");
        Files.createDirectories(directory);
        Path copy = directory.resolve(String.valueOf(file.getFileName()));
        Files.copy(file, copy, StandardCopyOption.REPLACE_EXISTING);
        deferred.add(copy);
        loadedLibraries.add(canonical);
        loadedLeaves.add(leaf);
        System.err.println("host: 延迟装载 ARM64 库（native 调用进行中）：" + file.getFileName());
    }

    /** 记录已装载库（按真实路径去重）；取不到真实路径时退化为不记录，后续按叶名去重仍生效。 */
    private void rememberLoaded(Path file) {
        try {
            loadedLibraries.add(file.toRealPath().toString());
        } catch (IOException unreadable) {
            // 忽略：路径不可解析时仅跳过去重记录。
        }
        if (file.getFileName() != null) loadedLeaves.add(file.getFileName().toString());
    }

    private void loadDeferred() {
        while (depth == 0 && !deferred.isEmpty()) {
            Path file = deferred.remove(0);
            try {
                loadModule(file);
                System.err.println("host: 延迟装载完成 " + file.getFileName());
            } catch (Throwable error) {
                // 不在此处抛出（会掩盖原始调用结果）；如实打印，后续对该库方法的调用会明确失败。
                System.err.println("host: 延迟装载失败 " + file.getFileName() + "：" + describe(error));
            }
        }
    }

    private static byte[] readPrefix(Path file) throws IOException {
        try (var stream = Files.newInputStream(file)) {
            return stream.readNBytes(20);
        }
    }

    /** guest 写的路径 → 宿主可读路径；无映射时原样返回。 */
    @Override
    public String hostPath(String path) {
        if (path == null || guestRoot == null) return path;
        try {
            if (Files.exists(Path.of(path))) return path;
        } catch (RuntimeException malformed) {
            return path;
        }
        if (!path.startsWith("/")) return path;
        Path mapped = guestRoot.resolve(path.substring(1));
        return Files.exists(mapped) ? mapped.toString() : path;
    }

    /** 诊断描述（绝不能读成「已模拟成功」）。 */
    @Override
    public String describe() {
        if (!resolution.available()) return "unidbg 依赖不可用：" + resolution.unavailableReason;
        if (loadedLibrary == null) return "unidbg 已就绪，尚未装载 guest 库";
        return "已装载 " + loadedLibrary + "（Dynarmic，JNI_OnLoad 已执行）";
    }

    // ---------- 真实调用链 ----------

    /** 返回 guest 是否导出了 {@code JNI_OnLoad}（并已真正执行）。抛出即失败。 */
    private boolean bootstrap(Path library) throws Exception {
        Class<?> builderType = resolution.require("com.github.unidbg.linux.android.AndroidEmulatorBuilder");
        File guestRoot = workRoot.resolve("android-guest").toFile();
        Files.createDirectories(guestRoot.toPath());
        this.guestRoot = guestRoot.toPath();

        Object builder = Reflect.callStatic(builderType, "for64Bit");
        builder = Reflect.call(builder, "setProcessName", GUEST_PROCESS_NAME);
        builder = Reflect.call(builder, "setRootDir", guestRoot);
        Class<?> dynarmicFactoryType = resolution.require("com.github.unidbg.arm.backend.DynarmicFactory");
        // false = 不静默回退 Unicorn：Dynarmic 原生加载失败会立刻抛，便于显式失败而非悄悄换引擎。
        Object dynarmicFactory = Reflect.construct(dynarmicFactoryType, Boolean.FALSE);
        builder = Reflect.call(builder, "addBackendFactory", dynarmicFactory);
        emulator = Reflect.call(builder, "build");
        Reflect.call(emulator, "setTimeout", EMULATION_TIMEOUT_MICROS);

        Object memory = Reflect.call(emulator, "getMemory");
        Class<?> resolverType = resolution.require("com.github.unidbg.linux.android.AndroidResolver");
        Reflect.call(memory, "setLibraryResolver", Reflect.construct(resolverType, Integer.valueOf(23)));

        vm = Reflect.call(emulator, "createDalvikVM");
        installReflectionHost();

        Class<?> androidModuleType = resolution.require("com.github.unidbg.virtualmodule.android.AndroidModule");
        Reflect.call(Reflect.construct(androidModuleType, emulator, vm), "register", memory);

        Object dalvikModule = Reflect.call(vm, "loadLibrary", library.toFile(), Boolean.TRUE);
        boolean onLoad = hasSymbol(dalvikModule);
        Reflect.call(dalvikModule, "callJNI_OnLoad", emulator);   // 真正执行 guest JNI_OnLoad（含 RegisterNatives）
        this.dalvikModule = dalvikModule;
        loadedLibrary = String.valueOf(library.getFileName());
        System.err.println("VODBOX_GUARD_LOADED " + loadedLibrary + " jniOnLoad=" + onLoad
                + (onLoad ? "" : "（未导出 JNI_OnLoad，仅装载）"));
        return onLoad;
    }

    /** 把 unidbg 的类名映射/ JNI 兜底接到现有反射宿主（插件类加载器 + 宿主的 android.* 子集）。 */
    private void installReflectionHost() {
        Class<?> jniType = require("com.github.unidbg.linux.android.dvm.Jni");
        Class<?> mapperType = require("com.github.unidbg.linux.android.dvm.jni.ProxyClassMapper");
        Class<?> proxyObjectType = require("com.github.unidbg.linux.android.dvm.jni.ProxyDvmObject");
        Class<?> factoryType = require("com.github.unidbg.linux.android.dvm.jni.ProxyClassFactory");
        GuardJniHost host = new GuardJniHost(reflectionHost, jniType, mapperType, proxyObjectType);
        Object classFactory = Reflect.construct(factoryType, reflectionHost);
        Reflect.call(classFactory, "configClassNameMapper", host.classMapper());
        Reflect.call(classFactory, "setFallbackJni", host.jni());
        Reflect.call(vm, "setDvmClassFactory", classFactory);
        Reflect.call(vm, "resolveClass", "java/lang/Object");
        Reflect.call(vm, "resolveClass", "java/lang/Class");
        final Object delegateFactory = classFactory;
        Class<?> classFactoryApi = require("com.github.unidbg.linux.android.dvm.DvmClassFactory");
        Object hierarchyFactory = java.lang.reflect.Proxy.newProxyInstance(classFactoryApi.getClassLoader(),
                new Class<?>[]{classFactoryApi}, (proxy, method, args) -> {
            if (method.getName().equals("createClass") && args[2] == null) {
                try {
                    Class<?> hostClass = Class.forName(((String) args[1]).replace('/', '.'), false, NativeMethodBridge.reflectionHost() == null ? reflectionHost : NativeMethodBridge.reflectionHost());
                    if (hostClass.getSuperclass() != null)
                        args[2] = Reflect.call(vm, "resolveClass", hostClass.getSuperclass().getName().replace('.', '/'));
                } catch (ClassNotFoundException unavailable) { /* guest-only 类型保留原定义 */ }
            }
            try { return method.invoke(delegateFactory, args); }
            catch (java.lang.reflect.InvocationTargetException wrapped) { throw wrapped.getCause(); }
        });
        Reflect.call(vm, "setDvmClassFactory", hierarchyFactory);
        Reflect.call(vm, "setJni", host.jni());
        Reflect.call(vm, "setVerbose", Boolean.getBoolean("vodbox.traceJni"));
    }

    /** guest 模块是否导出 {@code JNI_OnLoad} 符号。 */
    private boolean hasSymbol(Object dalvikModule) {
        try {
            Object module = Reflect.call(dalvikModule, "getModule");
            Object symbol = Reflect.call(module, "findSymbolByName", "JNI_OnLoad", Boolean.FALSE);
            return symbol != null;
        } catch (RuntimeException missing) {
            return false;
        }
    }

    private Class<?> require(String name) {
        try {
            return resolution.require(name);
        } catch (ClassNotFoundException absent) {
            throw new IllegalStateException("缺少 unidbg 类型 " + name, absent);
        }
    }

    // ---------- 生命周期 ----------

    /** 释放模拟器与依赖类加载器；同一进程内保持的生命周期在此结束。可重复调用。 */
    @Override
    public void close() {
        Object current = emulator;
        emulator = null;
        vm = null;
        dalvikModule = null;
        loadedLibrary = null;
        guestRoot = null;
        deferred.clear();
        loadedLibraries.clear();
        loadedLeaves.clear();
        depth = 0;
        if (current != null) {
            try {
                Reflect.call(current, "close");
            } catch (Throwable error) {
                System.err.println("host: 释放 ARM64 模拟后端失败（忽略）：" + error);
            }
        }
        resolution.close();
    }

    /** 当前是否已加载 guest 库且未释放。 */
    public boolean loaded() {
        return vm != null && loadedLibrary != null;
    }

    // ---------- 报告 ----------

    /** 一次真实初始化的结果（用于诚实报告，不作为「可播放」的依据）。 */
    public static final class Report {
        /** 已加载的 guard 库叶名。 */
        public final String library;
        /** guest 是否导出了 {@code JNI_OnLoad}（并已执行）。 */
        public final boolean jniOnLoad;
        /** 初始化耗时（毫秒）。 */
        public final long elapsedMillis;
        /** 依赖与许可说明（必须随报告一起保留）。 */
        public final String note;

        Report(String library, boolean jniOnLoad, long elapsedMillis, String note) {
            this.library = library;
            this.jniOnLoad = jniOnLoad;
            this.elapsedMillis = elapsedMillis;
            this.note = note;
        }

        public String summary() {
            return "已在同一进程内的 Dynarmic 模拟环境加载 ARM64 guard：" + library
                    + "（JNI_OnLoad=" + (jniOnLoad ? "已执行" : "未导出") + "，" + elapsedMillis + "ms）";
        }
    }

    /** 展开异常链（有界），便于错误信封里的原因可读。 */
    private static String describe(Throwable error) {
        if (error == null) return "unknown";
        List<String> chain = new ArrayList<>();
        java.util.Set<Throwable> seen = java.util.Collections.newSetFromMap(new java.util.IdentityHashMap<>());
        for (Throwable cause = error; cause != null && seen.add(cause) && chain.size() < 6; cause = cause.getCause()) {
            chain.add(cause.toString());
        }
        return String.join(" <- ", chain);
    }
}
