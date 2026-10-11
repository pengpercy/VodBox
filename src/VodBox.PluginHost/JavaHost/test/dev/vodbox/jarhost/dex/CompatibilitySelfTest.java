package dev.vodbox.jarhost.dex;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * {@link Compatibility} 的**离线行为自测**（VodBox 自有实现）。
 *
 * <p>目标：证明「构造器祖先 owner 修补」与「接口静态调用桥」这两处改写**真的**把
 * {@link CompatibilityFixture} 造出的病态字节码改成 JVM 接受的形态——不是只读字节、不是只看产物，
 * 而是把修补后的类**真正定义并运行**。
 *
 * <p>做法（全部离线，不触网、不加载任何原生库、不跑 dex2jar）：
 * <ol>
 *   <li>用 {@link CompatibilityFixture} 的 ASM 夹具取得真实病态字节；</li>
 *   <li>用夹具全量建 {@link TypeIndex}，构造 {@link Compatibility}；</li>
 *   <li>断言 {@code Sub.<init>} 的 owner 由祖先 {@code Base} 修回直接父类 {@code Mid}；</li>
 *   <li>断言 {@code Caller} 对 {@code Iface.answer()} 的 {@code invokestatic} 变成接口形态且版本 ≥ 52；</li>
 *   <li>把修补后的类定义出来并**真正运行** {@code new Sub().tag()} 与 {@code Caller.call()}；</li>
 *   <li>断言无需修补的类（{@code Base}）**原样返回**（同一数组引用，不误伤）。</li>
 * </ol>
 *
 * <p><b>诚实边界</b>：本自测证明的是「这两处修补的**逻辑与产物**正确」；它**不是**真实 DEX 转换测试
 * （那见 {@code docs/DEX-ACCEPTANCE.md}），也**不**覆盖占位类型注入（DanglingTypes 的合成半边，未交付）。
 *
 * 用法：{@code java -cp <classes>:<dex-tools/*> dev.vodbox.jarhost.dex.CompatibilitySelfTest}
 * 退出码：0 全过；1 有断言失败。
 */
public final class CompatibilitySelfTest {

    private static int passed;
    private static final List<String> failures = new ArrayList<>();

    private CompatibilitySelfTest() {}

    public static void main(String[] args) throws Exception {
        Map<String, byte[]> classes = CompatibilityFixture.all();
        TypeIndex index = TypeIndex.of(classes.values());
        Compatibility compatibility = new Compatibility(index);

        check("索引含全部夹具类型", index.size() == classes.size());
        check("索引识别 Iface 为接口", index.kind(CompatibilityFixture.IFACE) == TypeIndex.Kind.INTERFACE);
        check("索引识别 Mid 为 Sub 的直接父类",
                CompatibilityFixture.MID.equals(index.superName(CompatibilityFixture.SUB)));
        check("索引识别 Base 为 Sub 的严格祖先",
                index.isStrictAncestor(CompatibilityFixture.BASE, CompatibilityFixture.SUB));
        check("索引确认 Mid 声明 ()V 构造器",
                index.declaresConstructor(CompatibilityFixture.MID, "()V"));

        constructorAncestorRepair(classes, compatibility);
        interfaceStaticBridge(classes, compatibility);
        untouchedWhenClean(classes, compatibility);
        repairedClassesActuallyRun(classes, compatibility);

        System.out.println("CompatibilitySelfTest: " + passed + " 项通过, " + failures.size() + " 项失败");
        for (String failure : failures) System.out.println("  FAIL " + failure);
        if (!failures.isEmpty()) System.exit(1);
    }

    // ---------- 断言分组 ----------

    private static void constructorAncestorRepair(Map<String, byte[]> classes, Compatibility compatibility) {
        byte[] original = classes.get(CompatibilityFixture.SUB);
        InheritedCall before = findConstructorCall(original, "<init>", "()V");
        check("夹具确为病态：Sub.<init> 指向祖先 Base", before != null
                && CompatibilityFixture.BASE.equals(before.owner()) && !before.isInterface());

        byte[] repaired = compatibility.rewrite(original);
        check("构造器祖先修补：产物是新数组（确有改写）", repaired != original);
        InheritedCall after = findConstructorCall(repaired, "<init>", "()V");
        check("构造器祖先修补：Sub.<init> owner 修回直接父类 Mid", after != null
                && CompatibilityFixture.MID.equals(after.owner()));
        check("构造器祖先修补：owner 不是自我引用、不是原始祖先",
                after != null && !CompatibilityFixture.SUB.equals(after.owner())
                        && !CompatibilityFixture.BASE.equals(after.owner()));
    }

    private static void interfaceStaticBridge(Map<String, byte[]> classes, Compatibility compatibility) {
        byte[] original = classes.get(CompatibilityFixture.CALLER);
        InheritedCall before = findStaticCall(original, CompatibilityFixture.IFACE, "answer", "()I");
        check("夹具确为病态：Caller 以类形态（itf=false）调用 Iface.answer", before != null && !before.isInterface());
        check("夹具确为病态：Caller 原始 class 版本 < 52", classVersion(original) < Opcodes.V1_8);

        byte[] repaired = compatibility.rewrite(original);
        InheritedCall after = findStaticCall(repaired, CompatibilityFixture.IFACE, "answer", "()I");
        check("接口静态调用桥：itf 修成接口形态", after != null && after.isInterface());
        check("接口静态调用桥：class 版本抬到 ≥ 52（Java 8）", classVersion(repaired) >= Opcodes.V1_8);
    }

    private static void untouchedWhenClean(Map<String, byte[]> classes, Compatibility compatibility) {
        byte[] base = classes.get(CompatibilityFixture.BASE);
        check("无需修补的类原样返回（同一数组引用）", compatibility.rewrite(base) == base);

        byte[] mid = classes.get(CompatibilityFixture.MID);
        check("合法的 super() 调用不改动", compatibility.rewrite(mid) == mid);
    }

    private static void repairedClassesActuallyRun(Map<String, byte[]> classes, Compatibility compatibility) throws Exception {
        Map<String, byte[]> repaired = new java.util.LinkedHashMap<>();
        for (Map.Entry<String, byte[]> entry : classes.entrySet()) {
            repaired.put(entry.getKey(), compatibility.rewrite(entry.getValue()));
        }
        FixtureLoader loader = new FixtureLoader(repaired, CompatibilitySelfTest.class.getClassLoader());

        Class<?> sub = loader.loadClass(CompatibilityFixture.SUB.replace('/', '.'));
        Object instance = sub.getDeclaredConstructor().newInstance();
        Object tag = sub.getMethod("tag").invoke(instance);
        check("修补后的 Sub 能定义并实例化（继承链 Base→Mid→Sub 走通）", Integer.valueOf(1).equals(tag));

        Class<?> caller = loader.loadClass(CompatibilityFixture.CALLER.replace('/', '.'));
        Object answer = caller.getMethod("call").invoke(null);
        check("修补后的 Caller.call() 真正调用接口静态方法并返回 7", Integer.valueOf(7).equals(answer));
    }

    // ---------- 字节码检查工具 ----------

    private record InheritedCall(String owner, String name, String descriptor, boolean isInterface) {}

    private static InheritedCall findConstructorCall(byte[] bytes, String method, String descriptor) {
        List<InheritedCall> calls = new ArrayList<>();
        reader(bytes).accept(new ClassVisitor(Opcodes.ASM9) {
            @Override
            public MethodVisitor visitMethod(int access, String name, String desc, String signature, String[] exceptions) {
                if (!name.equals(method) || !desc.equals(descriptor)) return null;
                return new MethodVisitor(Opcodes.ASM9) {
                    @Override
                    public void visitMethodInsn(int opcode, String owner, String name2, String descriptor2, boolean isInterface) {
                        if (opcode == Opcodes.INVOKESPECIAL && "<init>".equals(name2)) {
                            calls.add(new InheritedCall(owner, name2, descriptor2, isInterface));
                        }
                    }
                };
            }
        }, 0);
        return calls.isEmpty() ? null : calls.get(0);
    }

    private static InheritedCall findStaticCall(byte[] bytes, String owner, String method, String descriptor) {
        List<InheritedCall> calls = new ArrayList<>();
        reader(bytes).accept(new ClassVisitor(Opcodes.ASM9) {
            @Override
            public MethodVisitor visitMethod(int access, String name, String desc, String signature, String[] exceptions) {
                return new MethodVisitor(Opcodes.ASM9) {
                    @Override
                    public void visitMethodInsn(int opcode, String opcodeOwner, String name2, String descriptor2, boolean isInterface) {
                        if (opcode == Opcodes.INVOKESTATIC && owner.equals(opcodeOwner)
                                && method.equals(name2) && descriptor.equals(descriptor2)) {
                            calls.add(new InheritedCall(opcodeOwner, name2, descriptor2, isInterface));
                        }
                    }
                };
            }
        }, 0);
        return calls.isEmpty() ? null : calls.get(0);
    }

    private static int classVersion(byte[] bytes) {
        return reader(bytes).readShort(6);
    }

    private static ClassReader reader(byte[] bytes) {
        try {
            return new ClassReader(bytes);
        } catch (RuntimeException malformed) {
            throw new IllegalStateException("夹具/产物不是合法 class 文件", malformed);
        }
    }

    // ---------- 类加载工具 ----------

    /** 用给定字节定义夹具类（同名不委派父加载器，确保跑的是修补产物）。 */
    private static final class FixtureLoader extends ClassLoader {
        private final Map<String, byte[]> classes;

        FixtureLoader(Map<String, byte[]> classes, ClassLoader parent) {
            super(parent);
            this.classes = classes;
        }

        @Override
        protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
            Class<?> existing = findLoadedClass(name);
            if (existing != null) {
                if (resolve) resolveClass(existing);
                return existing;
            }
            String internal = name.replace('.', '/');
            byte[] bytes = classes.get(internal);
            if (bytes == null) return super.loadClass(name, resolve);
            Class<?> defined = defineClass(name, bytes, 0, bytes.length);
            if (resolve) resolveClass(defined);
            return defined;
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
