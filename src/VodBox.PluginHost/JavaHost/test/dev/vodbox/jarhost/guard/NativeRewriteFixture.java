package dev.vodbox.jarhost.guard;

/**
 * 字节码改写自测的**夹具**（VodBox 自有，不是任何第三方插件代码）。
 *
 * <p>它刻意声明覆盖各形态的 {@code native} 方法：静态/实例、{@code void}、8 种原始类型、
 * 对象、原始类型数组、对象数组、多类型参数组合；另含一个**非 native** 方法调用
 * {@code System.load}/{@code System.loadLibrary}，用于验证这两处调用被改写到桥接门面。
 *
 * <p>本夹具**只**用于离线自测（{@code JavaHost/test.sh} 里的 {@link NativeBytecodeRewriteSelfTest}）：
 * 自测读取本类的字节、经 {@link NativeBytecodeRewriter} 去 native 化后再定义并运行，
 * 校验改写后的方法体真的按「静态/实例 + 各参数/返回」把调用转发给桥接门面。它不随宿主打包、
 * 不被插件加载。
 */
public final class NativeRewriteFixture {

    // ---------- 静态 native：void / 8 种原始类型 / 对象 / 数组 ----------

    public static native void staticVoid(int first, String second);

    public static native boolean staticBoolean(int value);

    public static native byte staticByte(long value);

    public static native char staticChar(String value);

    public static native short staticShort(int value);

    public static native int staticInt(int first, int second);

    public static native long staticLong(int first, long second);

    public static native float staticFloat(double value);

    public static native double staticDouble(float value);

    public static native String staticString(String value, int count);

    public static native int[] staticIntArray(byte[] data, String[] names);

    public static native Object staticObject(Object first, Object second);

    // ---------- 实例 native：多类型参数/返回 + 接收者 ----------

    public native int instanceInt(String value, boolean flag);

    public native String instanceString(char c, double d, long l);

    public native long[] instanceLongArray(int[] values);

    // ---------- 非 native：验证 System.load / System.loadLibrary 被改写到桥接门面 ----------

    public static void loadNative(String path, String name) {
        System.load(path);
        System.loadLibrary(name);
    }

    public String describe() {
        return "native-rewrite-fixture";
    }
}
