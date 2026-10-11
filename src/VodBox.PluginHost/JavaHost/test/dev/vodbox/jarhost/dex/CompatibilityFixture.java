package dev.vodbox.jarhost.dex;

import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * {@link Compatibility} 的**真实 ASM 夹具**（VodBox 自有，不是任何第三方插件代码）。
 *
 * <p>它用 ASM **真正构造**出「ART 能跑、HotSpot 校验器拒绝」的病态字节码，供离线自测
 * {@link CompatibilitySelfTest} 断言修补真的生效。之所以要用 ASM 生成而不是 {@code javac}：
 * {@code javac} 不会产出下列非法形态，只有 DEX 转换器 / R8 优化才会。
 *
 * <h2>夹具里的两条病态链</h2>
 *
 * <h3>A. 构造器祖先 owner（{@code Sub.<init>} 调 {@code Base.<init>}，跳过 {@code Mid}）</h3>
 * {@code Base < Mid < Sub}，但 {@code Sub.<init>()V} 直接 {@code invokespecial Base.<init>()V}——
 * 即 R8 内联 {@code Mid.<init>} 后的形态。HotSpot 会在定义 {@code Sub} 时抛
 * {@code VerifyError: Bad <init> method call}。{@link Compatibility} 应把 owner 修回 {@code Mid}。
 * 注意：{@code Base} 与 {@code Mid} 都保留了构造器（因此修补一定可链接）。
 *
 * <h3>B. 接口静态调用缺 {@code itf} 形态（{@code Caller} 调 {@code Iface.answer()}）</h3>
 * {@code Iface} 是接口，含静态方法 {@code static int answer()}；{@code Caller.call()} 用
 * {@code INVOKESTATIC owner=Iface itf=false}（常量池落成 {@code Methodref}）调用它。HotSpot 抛
 * {@code IncompatibleClassChangeError: Method must be InterfaceMethodref constant}。
 * {@link Compatibility} 应把 flag 修成接口形态，并把 class 版本抬到 ≥ 52。
 *
 * <p>本夹具**只**用于离线自测（{@code JavaHost/test.sh}），不随宿主打包、不被插件加载。
 */
public final class CompatibilityFixture {

    public static final String BASE = "dev/vodbox/jarhost/dex/fixture/Base";
    public static final String MID = "dev/vodbox/jarhost/dex/fixture/Mid";
    public static final String SUB = "dev/vodbox/jarhost/dex/fixture/Sub";
    public static final String IFACE = "dev/vodbox/jarhost/dex/fixture/Iface";
    public static final String CALLER = "dev/vodbox/jarhost/dex/fixture/Caller";

    private CompatibilityFixture() {}

    /** 全部夹具类：内部名 → 字节。顺序固定，便于断言。 */
    public static Map<String, byte[]> all() {
        Map<String, byte[]> classes = new LinkedHashMap<>();
        classes.put(BASE, base());
        classes.put(MID, mid());
        classes.put(SUB, sub());
        classes.put(IFACE, iface());
        classes.put(CALLER, caller());
        return classes;
    }

    /** {@code class Base { Base(){} int tag(){ return 1; } }} */
    public static byte[] base() {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS);
        writer.visit(Opcodes.V1_8, Opcodes.ACC_PUBLIC | Opcodes.ACC_SUPER, BASE, null, "java/lang/Object", null);
        defaultConstructor(writer, "java/lang/Object");
        MethodVisitor tag = writer.visitMethod(Opcodes.ACC_PUBLIC, "tag", "()I", null, null);
        tag.visitCode();
        tag.visitInsn(Opcodes.ICONST_1);
        tag.visitInsn(Opcodes.IRETURN);
        tag.visitMaxs(0, 0);
        tag.visitEnd();
        writer.visitEnd();
        return writer.toByteArray();
    }

    /** {@code class Mid extends Base { Mid(){ super(); } }} —— 合法的直接父类链。 */
    public static byte[] mid() {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS);
        writer.visit(Opcodes.V1_8, Opcodes.ACC_PUBLIC | Opcodes.ACC_SUPER, MID, null, BASE, null);
        defaultConstructor(writer, BASE);
        writer.visitEnd();
        return writer.toByteArray();
    }

    /**
     * {@code class Sub extends Mid}，但 {@code Sub.<init>()V} 直接调 {@code Base.<init>()V}
     * （祖先 owner，跳过直接父类 {@code Mid}）——病态 A。
     */
    public static byte[] sub() {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS);
        writer.visit(Opcodes.V1_8, Opcodes.ACC_PUBLIC | Opcodes.ACC_SUPER, SUB, null, MID, null);
        defaultConstructor(writer, BASE); // 病态：owner 是祖先而非直接父类 Mid
        writer.visitEnd();
        return writer.toByteArray();
    }

    /**
     * {@code interface Iface { static int answer() { return 7; } }} —— 需要 class 版本 ≥ 52 的接口静态方法。
     */
    public static byte[] iface() {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS);
        writer.visit(Opcodes.V1_8, Opcodes.ACC_PUBLIC | Opcodes.ACC_ABSTRACT | Opcodes.ACC_INTERFACE,
                IFACE, null, "java/lang/Object", null);
        MethodVisitor answer = writer.visitMethod(
                Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC, "answer", "()I", null, null);
        answer.visitCode();
        answer.visitIntInsn(Opcodes.BIPUSH, 7);
        answer.visitInsn(Opcodes.IRETURN);
        answer.visitMaxs(0, 0);
        answer.visitEnd();
        writer.visitEnd();
        return writer.toByteArray();
    }

    /**
     * {@code class Caller { static int call(){ return Iface.answer(); } }}，其中对 {@code Iface.answer()} 的
     * {@code invokestatic} 以 {@code itf=false}（类形态）落盘——病态 B。用 V1_6 (50) 版本，
     * 以便验证修补把版本抬到 ≥ 52。
     */
    public static byte[] caller() {
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS);
        writer.visit(Opcodes.V1_6, Opcodes.ACC_PUBLIC | Opcodes.ACC_SUPER, CALLER, null, "java/lang/Object", null);
        defaultConstructor(writer, "java/lang/Object");
        MethodVisitor call = writer.visitMethod(Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC, "call", "()I", null, null);
        call.visitCode();
        // 病态 B：接口静态方法被当作类方法调用（itf=false）。
        call.visitMethodInsn(Opcodes.INVOKESTATIC, IFACE, "answer", "()I", false);
        call.visitInsn(Opcodes.IRETURN);
        call.visitMaxs(0, 0);
        call.visitEnd();
        writer.visitEnd();
        return writer.toByteArray();
    }

    /** 普通 {@code <init>()V}：{@code aload_0; invokespecial super.<init>()V; return}。 */
    private static void defaultConstructor(ClassWriter writer, String superName) {
        MethodVisitor init = writer.visitMethod(Opcodes.ACC_PUBLIC, "<init>", "()V", null, null);
        init.visitCode();
        init.visitVarInsn(Opcodes.ALOAD, 0);
        init.visitMethodInsn(Opcodes.INVOKESPECIAL, superName, "<init>", "()V", false);
        init.visitInsn(Opcodes.RETURN);
        init.visitMaxs(0, 0);
        init.visitEnd();
    }
}
