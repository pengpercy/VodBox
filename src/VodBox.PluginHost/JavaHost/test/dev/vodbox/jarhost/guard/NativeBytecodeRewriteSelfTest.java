package dev.vodbox.jarhost.guard;

import java.io.IOException;
import java.io.InputStream;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * {@link NativeBytecodeRewriter} 的**离线行为自测**（VodBox 自有实现）。
 *
 * <p>目标：验证「把插件字节码里的 {@code native} 方法去 native 化为转发到
 * {@link NativeMethodBridge}」这一改写**真的按各参数/返回形态工作**，并且**改写后的类能运行**
 * （不是只读字节、不是只看改写产物）。
 *
 * <p>做法：
 * <ol>
 *   <li>读取 {@link NativeRewriteFixture} 的字节（真实 {@code javac} 产物，含真实 static/instance
 *       native 方法与 {@code System.load} 调用点）；</li>
 *   <li>用 {@link NativeBytecodeRewriter} 改写；</li>
 *   <li>用**记录型** {@link NativeCallSession} 安装到桥接门面（{@link NativeMethodBridge}），
 *       再把改写后的类定义出来并**真正 invoke** 每个方法；</li>
 *   <li>断言门面收到的 owner/签名/接收者/参数与返回拆箱/转型都正确。</li>
 * </ol>
 *
 * <p><b>诚实边界</b>：本自测用**记录型后端**，只证明「改写 + 桥接转发 + 返回值装配」这一层正确；
 * 它**不是**真实的 ARM64 模拟测试（那需要 unidbg + Dynarmic，见 {@link DynarmicGuardBackend}）。
 * 这里也不下载/加载任何原生库。
 *
 * 用法：{@code java -cp <classes>:<dex-tools/*> dev.vodbox.jarhost.guard.NativeBytecodeRewriteSelfTest}
 * 退出码：0 全过；1 有断言失败。
 */
public final class NativeBytecodeRewriteSelfTest {
    /** 夹具的二进制类名。 */
    private static final String FIXTURE = "dev.vodbox.jarhost.guard.NativeRewriteFixture";
    /** 夹具的内部类名（改写后方法体里传给门面的 owner）。 */
    private static final String FIXTURE_INTERNAL = "dev/vodbox/jarhost/guard/NativeRewriteFixture";

    private static int passed;
    private static final List<String> failures = new ArrayList<>();

    private NativeBytecodeRewriteSelfTest() {}

    public static void main(String[] args) throws Exception {
        Recorder recorder = new Recorder();
        // 记录型后端：只记录门面收到的调用并返回预设值，不执行任何 guest 代码。
        NativeMethodBridge.install(recorder, NativeBytecodeRewriteSelfTest.class.getClassLoader());
        try {
            Class<?> type = defineFixture(new NativeBytecodeRewriter().rewrite(fixtureBytes()), FIXTURE);
            staticVoidAndPrimitives(type, recorder);
            objectAndArrayReturns(type, recorder);
            instanceMethods(type, recorder);
            loadRewrites(type, recorder);
            unchangedWhenNoNative();
            jarRoundTrip(recorder);
        } finally {
            NativeMethodBridge.reset();
        }

        System.out.println("NativeBytecodeRewriteSelfTest: " + passed + " 项通过, " + failures.size() + " 项失败");
        for (String failure : failures) System.out.println("  FAIL " + failure);
        if (!failures.isEmpty()) System.exit(1);
    }

    // ---------- 断言分组 ----------

    private static void staticVoidAndPrimitives(Class<?> type, Recorder recorder) throws Exception {
        // void 返回：门面返回 null，方法体应 POP 后 RETURN。
        Object voidResult = type.getMethod("staticVoid", int.class, String.class).invoke(null, 7, "x");
        Call call = recorder.last();
        check("static void：未返回值", voidResult == null);
        check("static void：owner/签名/静态", FIXTURE_INTERNAL.equals(call.owner)
                && "staticVoid(ILjava/lang/String;)V".equals(call.signature) && call.receiver == null);
        check("static void：参数已装箱", Arrays.equals(call.arguments, new Object[]{7, "x"}));

        check("static boolean 返回拆箱", Boolean.TRUE.equals(
                type.getMethod("staticBoolean", int.class).invoke(null, 5)));
        check("static byte 返回拆箱", Byte.valueOf((byte) 7).equals(
                type.getMethod("staticByte", long.class).invoke(null, 9L)));
        check("static char 返回拆箱", Character.valueOf('Z').equals(
                type.getMethod("staticChar", String.class).invoke(null, "h")));
        check("static short 返回拆箱", Short.valueOf((short) 9).equals(
                type.getMethod("staticShort", int.class).invoke(null, 1)));
        check("static int 返回拆箱", Integer.valueOf(42).equals(
                type.getMethod("staticInt", int.class, int.class).invoke(null, 3, 4)));
        check("static long 返回拆箱", Long.valueOf(1234567890123L).equals(
                type.getMethod("staticLong", int.class, long.class).invoke(null, 1, 2L)));
        check("static float 返回拆箱", Float.valueOf(1.5f).equals(
                type.getMethod("staticFloat", double.class).invoke(null, 2.0d)));
        check("static double 返回拆箱", Double.valueOf(2.25d).equals(
                type.getMethod("staticDouble", float.class).invoke(null, 1.0f)));
        check("static int 参数按位装箱", Arrays.equals(
                recorder.calls.stream().filter(c -> c.signature.equals("staticInt(II)I")).findFirst().orElseThrow().arguments, new Object[]{3, 4}));
    }

    private static void objectAndArrayReturns(Class<?> type, Recorder recorder) throws Exception {
        check("static String 返回转型", "hello".equals(
                type.getMethod("staticString", String.class, int.class).invoke(null, "v", 2)));

        byte[] data = new byte[]{1, 2};
        String[] names = new String[]{"a"};
        Object arrayResult = type.getMethod("staticIntArray", byte[].class, String[].class).invoke(null, data, names);
        check("static int[] 返回转型", arrayResult instanceof int[] && Arrays.equals((int[]) arrayResult, new int[]{1, 2, 3}));
        Call arrayCall = recorder.last();
        check("static 数组参数原样传入（对象/数组不装箱）", arrayCall.arguments[0] == data && arrayCall.arguments[1] == names);

        check("static Object 返回", "obj".equals(
                type.getMethod("staticObject", Object.class, Object.class).invoke(null, "a", "b")));
    }

    private static void instanceMethods(Class<?> type, Recorder recorder) throws Exception {
        Object instance = type.getDeclaredConstructor().newInstance();

        Object intResult = type.getMethod("instanceInt", String.class, boolean.class).invoke(instance, "s", true);
        Call intCall = recorder.last();
        check("instance int 返回拆箱", Integer.valueOf(7).equals(intResult));
        check("instance 方法 receiver 为接收者实例", intCall.receiver == instance);
        check("instance 方法签名与参数", "instanceInt(Ljava/lang/String;Z)I".equals(intCall.signature)
                && Arrays.equals(intCall.arguments, new Object[]{"s", Boolean.TRUE}));

        check("instance String 返回", "inst".equals(
                type.getMethod("instanceString", char.class, double.class, long.class).invoke(instance, 'c', 1.0d, 2L)));

        int[] values = new int[]{9};
        Object longArray = type.getMethod("instanceLongArray", int[].class).invoke(instance, (Object) values);
        check("instance long[] 返回转型", longArray instanceof long[]
                && Arrays.equals((long[]) longArray, new long[]{4L, 5L})
                && recorder.last().arguments[0] == values);
    }

    private static void loadRewrites(Class<?> type, Recorder recorder) throws Exception {
        type.getMethod("loadNative", String.class, String.class).invoke(null, "/tmp/libguard.so", "guard");
        check("System.load 被改写到桥接门面", recorder.loads.equals(List.of("/tmp/libguard.so")));
        check("System.loadLibrary 被改写到桥接门面", recorder.loadLibraries.equals(List.of("guard")));
    }

    private static void unchangedWhenNoNative() {
        byte[] original = resourceBytes("/dev/vodbox/jarhost/guard/NativeCallSession.class");
        byte[] rewritten = new NativeBytecodeRewriter().rewrite(original);
        check("不含 native 的类原样返回（不误伤）", rewritten == original);
    }

    private static void jarRoundTrip(Recorder recorder) throws Exception {
        Path directory = Files.createTempDirectory("vodbox-native-rewrite");
        try {
            Path jar = directory.resolve("fixture.jar");
            try (ZipOutputStream output = new ZipOutputStream(Files.newOutputStream(jar))) {
                output.putNextEntry(new ZipEntry(FIXTURE_INTERNAL + ".class"));
                output.write(fixtureBytes());
                output.closeEntry();
            }
            check("hasNativeMethods 识别归档里的 native", NativeBytecodeRewriter.hasNativeMethods(jar));

            NativeBytecodeRewriter rewriter = new NativeBytecodeRewriter();
            Path rewrittenJar = directory.resolve("fixture-rewritten.jar");
            check("rewriteJar 对含 native 的归档写出产物", rewriter.rewriteJar(jar, rewrittenJar) && Files.isRegularFile(rewrittenJar));

            Class<?> type = loadFromJar(rewrittenJar, FIXTURE);
            Object result = type.getMethod("staticInt", int.class, int.class).invoke(null, 8, 9);
            // 改写后的 jar 里的类**真正运行**：门面收到调用并按返回类型拆箱。
            check("改写后的 jar 里的类可运行（staticInt 走桥接）", Integer.valueOf(42).equals(result)
                    && "staticInt(II)I".equals(recorder.last().signature));
        } finally {
            deleteRecursively(directory);
        }
    }

    /** 清理自测产生的临时目录（失败不影响判定）。 */
    private static void deleteRecursively(Path directory) {
        try (var stream = Files.walk(directory)) {
            stream.sorted(java.util.Comparator.reverseOrder()).forEach(path -> {
                try {
                    Files.deleteIfExists(path);
                } catch (IOException ignored) {
                    // 清理失败不影响判定。
                }
            });
        } catch (IOException ignored) {
            // 清理失败不影响判定。
        }
    }

    // ---------- 定义/加载改写后的类 ----------

    /** 用改写后的字节在独立类加载器里定义夹具类（同名类不委派父加载器，避免命中未改写版本）。 */
    private static Class<?> defineFixture(byte[] rewritten, String name) throws Exception {
        ClassLoader loader = new ClassLoader(NativeBytecodeRewriteSelfTest.class.getClassLoader()) {
            @Override
            protected Class<?> loadClass(String target, boolean resolve) throws ClassNotFoundException {
                if (!target.equals(name)) return super.loadClass(target, resolve);
                Class<?> existing = findLoadedClass(name);
                if (existing == null) existing = defineClass(name, rewritten, 0, rewritten.length);
                if (resolve) resolveClass(existing);
                return existing;
            }
        };
        return loader.loadClass(name);
    }

    /** 从改写后的 JAR 里加载夹具类（同名类不委派父加载器，确保读的是改写产物）。 */
    private static Class<?> loadFromJar(Path jar, String name) throws Exception {
        URLClassLoader loader = new URLClassLoader(new URL[]{jar.toUri().toURL()},
                NativeBytecodeRewriteSelfTest.class.getClassLoader()) {
            @Override
            protected Class<?> loadClass(String target, boolean resolve) throws ClassNotFoundException {
                if (!target.equals(name)) return super.loadClass(target, resolve);
                Class<?> existing = findLoadedClass(name);
                if (existing == null) existing = findClass(name);
                if (resolve) resolveClass(existing);
                return existing;
            }
        };
        return loader.loadClass(name);
    }

    private static byte[] fixtureBytes() {
        return resourceBytes("/" + FIXTURE.replace('.', '/') + ".class");
    }

    private static byte[] resourceBytes(String resource) {
        try (InputStream input = NativeBytecodeRewriteSelfTest.class.getResourceAsStream(resource)) {
            if (input == null) throw new IllegalStateException("找不到类字节资源：" + resource);
            return input.readAllBytes();
        } catch (IOException error) {
            throw new IllegalStateException("读取类字节失败：" + resource, error);
        }
    }

    // ---------- 记录型后端 ----------

    /** 门面一次调用的记录。 */
    private static final class Call {
        final String owner;
        final String signature;
        final Object receiver;
        final Object[] arguments;

        Call(String owner, String signature, Object receiver, Object[] arguments) {
            this.owner = owner;
            this.signature = signature;
            this.receiver = receiver;
            this.arguments = arguments;
        }
    }

    /** 记录型会话：记录每次桥接调用并按签名返回预设值（不执行 guest 代码，也绝不联网/加载原生库）。 */
    private static final class Recorder implements NativeCallSession {
        final List<Call> calls = new ArrayList<>();
        final List<String> loads = new ArrayList<>();
        final List<String> loadLibraries = new ArrayList<>();
        final Map<String, Object> returns = new LinkedHashMap<>();

        Recorder() {
            returns.put("staticBoolean(I)Z", Boolean.TRUE);
            returns.put("staticByte(J)B", (byte) 7);
            returns.put("staticChar(Ljava/lang/String;)C", 'Z');
            returns.put("staticShort(I)S", (short) 9);
            returns.put("staticInt(II)I", 42);
            returns.put("staticLong(IJ)J", 1234567890123L);
            returns.put("staticFloat(D)F", 1.5f);
            returns.put("staticDouble(F)D", 2.25d);
            returns.put("staticString(Ljava/lang/String;I)Ljava/lang/String;", "hello");
            returns.put("staticIntArray([B[Ljava/lang/String;)[I", new int[]{1, 2, 3});
            returns.put("staticObject(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;", "obj");
            returns.put("instanceInt(Ljava/lang/String;Z)I", 7);
            returns.put("instanceString(CDJ)Ljava/lang/String;", "inst");
            returns.put("instanceLongArray([I)[J", new long[]{4L, 5L});
        }

        Call last() {
            return calls.get(calls.size() - 1);
        }

        @Override
        public Object invoke(String owner, String nameAndDescriptor, Object receiver, Object[] arguments) {
            calls.add(new Call(owner, nameAndDescriptor, receiver, arguments));
            return returns.get(nameAndDescriptor);
        }

        @Override
        public void load(String path) {
            loads.add(path);
        }

        @Override
        public void loadLibrary(String name) {
            loadLibraries.add(name);
        }

        @Override
        public String hostPath(String path) {
            return path;
        }

        @Override
        public boolean available() {
            return true;
        }

        @Override
        public String describe() {
            return "recording-backend";
        }
    }

    // ---------- 断言工具 ----------

    private static void check(String name, boolean condition) {
        if (condition) {
            passed++;
            System.out.println("  ok   " + name);
        } else {
            failures.add(name);
        }
    }
}
