package dev.vodbox.jarhost.guard;

import java.nio.file.Path;

/**
 * 插件 JNI 的**透明桥接门面**（本仓库自有实现，不复制上游源码）。
 *
 * <p>插件类里声明的 {@code native} 方法会被 {@link NativeBytecodeRewriter} 去 native 化，方法体变成对
 * {@link #invoke} 的调用；{@link NativeBridgingClassLoader} 在 {@code findClass} 阶段应用该改写。
 * 于是「插件调用自己的 native 方法」被透明转发到 {@link #session} 持有的 {@link NativeCallSession}
 * （同进程内的 ARM64 模拟后端），而**不是**抛 {@code UnsatisfiedLinkError}。
 *
 * <p>同理，插件里的 {@code java/lang/System.load}/{@code loadLibrary} 调用被改写到
 * {@link #load}/{@link #loadLibrary}，由模拟后端在 guest 里装载归档里的 ARM64 库——
 * <b>宿主 JVM 永不装载插件归档里的 .so</b>。
 *
 * <p><b>诚实边界</b>：本类不制造任何「已模拟」的假象。没有已就绪的后端时，任何桥接调用都以
 * {@link NativeGuards.UnsupportedLibrary} 明确失败（宿主归类为 {@code unsupported_native_library}）。
 * 后端存在但某次调用失败时，原样抛出后端给出的明确原因。
 */
public final class NativeMethodBridge {
    private NativeMethodBridge() {}

    /**
     * 运行时物化钩子：把插件运行时产出的原生材料（例如 FTY guard 解密出的 {@code .dex}）转成
     * JVM 类加载器可读的路径。未安装时返回 null——调用方必须保持「未转换即明确拒绝」的语义，
     * 绝不把未转换的 DEX 当作可加载。
     */
    @FunctionalInterface
    public interface DexMaterializer {
        /** 返回可加载的 JVM 产物路径；无法转换时返回 null（调用方据此明确失败）。 */
        Path materialize(Path nativeArtifact);
    }

    private static volatile NativeCallSession session;
    private static volatile DexMaterializer materializer;
    private static volatile ClassLoader reflectionHost;

    /** 由宿主在插件会话建立后接线；{@code host} 是插件类加载器（供 JNI 反射宿主使用）。 */
    public static synchronized void install(NativeCallSession value, ClassLoader host) {
        session = value;
        reflectionHost = host;
    }

    /** 安装运行时物化钩子（由宿主用其 DEX→JVM 能力实现）。 */
    public static synchronized void installMaterializer(DexMaterializer value) {
        materializer = value;
    }

    /** 会话结束时清空接线（可重复调用）。 */
    public static synchronized void reset() {
        session = null;
        materializer = null;
        reflectionHost = null;
    }

    public static boolean installed() {
        return session != null;
    }

    /** 插件类加载器（JNI 反射宿主）；未接线时为 null。 */
    public static ClassLoader reflectionHost() {
        return reflectionHost;
    }

    public static void registerLoader(ClassLoader loader) {
        if (session != null && loader != null) reflectionHost = loader;
    }

    public static String describe() {
        NativeCallSession current = session;
        return current == null ? "未接线（无 ARM64 模拟后端）" : current.describe();
    }

    // ---------- 由改写后的 native 方法调用 ----------

    /**
     * 桥接入口：{@code owner} 是 DVM 类名（内部点分，如 {@code com.github.catvod.spider.DexNative}），
     * {@code nameAndDescriptor} 是「方法名 + JNI 描述符」，{@code receiver} 为 null 表示静态方法。
     */
    public static Object invoke(String owner, String nameAndDescriptor, Object receiver, Object[] arguments) {
        NativeCallSession current = session;
        if (current == null) {
            throw unavailable(owner, "本进程内没有已就绪的 ARM64 模拟后端；插件声明的 native 方法无法执行");
        }
        return current.invoke(owner, nameAndDescriptor, receiver, arguments);
    }

    /** 由改写后的 {@code java/lang/System.load(String)} 调用。 */
    public static void load(String path) {
        NativeCallSession current = session;
        if (current == null) {
            throw unavailable(String.valueOf(path), "尚未接线原生后端，无法装载插件原生库");
        }
        current.load(path);
    }

    /** 由改写后的 {@code java/lang/System.loadLibrary(String)} 调用。 */
    public static void loadLibrary(String name) {
        NativeCallSession current = session;
        if (current == null) {
            throw unavailable(String.valueOf(name), "尚未接线原生后端，无法按名装载插件原生库");
        }
        current.loadLibrary(name);
    }

    /** guest 写的路径 → 宿主可读路径；无后端时原样返回（不臆造映射）。 */
    public static String hostPath(String path) {
        NativeCallSession current = session;
        return current == null ? path : current.hostPath(path);
    }

    // ---------- 运行时物化（FTY 解密产物等） ----------

    /** 是否已安装运行时物化钩子（宿主是否具备 DEX→JVM 能力）。 */
    public static boolean materializerInstalled() {
        return materializer != null;
    }

    /**
     * 把运行时原生材料转成可加载的 JVM 产物路径；未安装钩子、材料为空或转换失败时返回 null。
     * 调用方**必须**在返回 null 时明确失败，不得把未转换产物当作可加载。
     */
    public static Path materialize(Path artifact) {
        DexMaterializer current = materializer;
        if (current == null || artifact == null) return null;
        return current.materialize(artifact);
    }

    private static NativeGuards.UnsupportedLibrary unavailable(String library, String reason) {
        return new NativeGuards.UnsupportedLibrary(library == null || library.isEmpty() ? "(unknown)" : library, reason);
    }
}
