package dev.vodbox.jarhost.guard;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.file.Path;
import java.util.function.Function;

/**
 * 对 **ASM 改写器**的反射工厂：本类**不引用**任何 ASM 类型，因此即使宿主没有把 ASM 放进运行时类路径，
 * 本类也能安全加载——只是在 {@link #available()} 处如实返回 false，桥接整体降级为「不启用」，
 * 而不是让整个宿主因缺类而崩溃。
 *
 * <p>真正的改写实现 {@link NativeBytecodeRewriter} 只在 ASM 就绪时经反射实例化；这样 ASM 的缺失是
 * 一个**可判定的降级点**，不是不可控的 {@code NoClassDefFoundError}。
 */
public final class NativeRewriterFactory {
    /** ASM 的最小必要类型（决定是否启用字节码改写）。 */
    private static final String ASM_PROBE = "org.objectweb.asm.ClassReader";
    /** 改写实现类（引用 ASM，只在 {@link #available()} 为 true 时加载）。 */
    private static final String IMPLEMENTATION = "dev.vodbox.jarhost.guard.NativeBytecodeRewriter";

    private NativeRewriterFactory() {}

    /** ASM 与改写实现都可加载时返回 true。 */
    public static boolean available() {
        ClassLoader loader = NativeRewriterFactory.class.getClassLoader();
        try {
            Class.forName(ASM_PROBE, false, loader);
        } catch (Throwable absent) {
            return false;
        }
        try {
            Class.forName(IMPLEMENTATION, true, loader);
            return true;
        } catch (Throwable absent) {
            return false;
        }
    }

    /**
     * 返回逐类改写函数；ASM 缺失或实现不可用时返回 null（宿主据此退回普通类加载）。
     * 返回的函数对「不含 native 方法」的类原样返回（见 {@link NativeBytecodeRewriter#rewrite}）。
     */
    public static Function<byte[], byte[]> rewriter() {
        ClassLoader loader = NativeRewriterFactory.class.getClassLoader();
        try {
            Class<?> type = Class.forName(IMPLEMENTATION, true, loader);
            Object instance = type.getDeclaredConstructor().newInstance();
            Method rewrite = type.getMethod("rewrite", byte[].class);
            return classBytes -> {
                try {
                    return (byte[]) rewrite.invoke(instance, (Object) classBytes);
                } catch (InvocationTargetException wrapped) {
                    Throwable cause = wrapped.getCause();
                    if (cause instanceof RuntimeException runtime) throw runtime;
                    throw new IllegalStateException("字节码改写失败：" + cause, cause);
                } catch (ReflectiveOperationException error) {
                    throw new IllegalStateException("字节码改写失败：" + error, error);
                }
            };
        } catch (Throwable unavailable) {
            return null;
        }
    }

    /**
     * 插件归档里是否声明了 native 方法（决定是否需要启用桥接类加载器）。ASM 缺失时返回 false
     * （此时桥接不启用，宿主按原有路径明确失败，绝不臆造支持）。
     */
    public static boolean requiresBridging(Path archive) {
        if (archive == null) return false;
        ClassLoader loader = NativeRewriterFactory.class.getClassLoader();
        try {
            Class<?> type = Class.forName(IMPLEMENTATION, true, loader);
            Method probe = type.getMethod("hasNativeMethods", Path.class);
            return Boolean.TRUE.equals(probe.invoke(null, archive));
        } catch (Throwable unavailable) {
            return false;
        }
    }
}
