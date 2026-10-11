package dev.vodbox.jarhost.dex;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

import java.util.List;
import java.util.Set;

/**
 * DEX→JVM 字节码修补的**改写半边**（VodBox 自有实现，独立于 GPL 上游、不复制其源码）。
 *
 * <p>这是 {@link DexArchive#danglingTypes} 那份「检测半边」的**互补交付**：转换器（dex2jar）产出的
 * 字节码遵循 ART 的宽松约定，HotSpot 的**校验器/链接器**更严，于是同一种字节码在 ART 能跑、在普通
 * JVM 上却以 {@code VerifyError}/{@code IncompatibleClassChangeError} 失败。本类按**公开 JVM 规则**
 * 把这些调用点改写成两个 JVM 都接受的形态，全部改写都**逐方法体**进行（ASM，BSD-3-Clause，
 * 随 {@code dex-tools/} 提供）。
 *
 * <h2>覆盖的两处缺口</h2>
 *
 * <h3>1. 接口调用桥（{@code itf} 形态修补）——「接口静态方法」</h3>
 * JVMS §6.5：{@code invokestatic}/{@code invokespecial}/{@code invokevirtual} 若引用的方法属于**接口**，
 * 常量池项必须是 {@code CONSTANT_InterfaceMethodref}（ASM 的 {@code isInterface=true}）；反之类方法必须是
 * {@code Methodref}。DEX 的 {@code invoke-static} 对接口静态方法不带这个区分，转换器可能落成
 * {@code Methodref}，加载时抛 {@code IncompatibleClassChangeError: Method must be InterfaceMethodref constant}
 * （或反向的 {@code Found class …}）。本类据 {@link TypeIndex} 把 flag 修成 JVM 要求的形态；
 * {@code invokestatic}/{@code invokespecial} 落到接口时还需要 class 文件版本 ≥ 52（Java 8），
 * 因此同时把版本抬到该下限。
 *
 * <h3>2. 构造器祖先 owner 修补（{@code ConstructorRepair}）——「R8 用祖先构造器初始化对象」</h3>
 * JVMS §4.9.2 / §4.10.1.9：{@code invokespecial} 引用 {@code <init>} 时，未初始化实例的 {@code <init>}
 * 必须属于**当前类**或它的**直接父类**。R8 优化（构造器内联/类合并）会把 {@code super(C)} 的构造器内联后
 * 把调用点**上移**到更远的祖先 {@code X}，ART 接受，HotSpot 报
 * {@code VerifyError: Bad <init> method call}。本类在本类的 {@code <init>} 里，当 {@code X} 是当前类的
 * **严格祖先**且**不是直接父类**、并且索引确认**直接父类确实声明**了该描述符的 {@code <init>} 时，
 * 把 owner 改回**直接父类**——这正是 R8 改写前的原始调用路径，语义与改写前一致。
 *
 * <h2>刻意不做的事（边界，不夸大）</h2>
 * <ul>
 *   <li><b>不臆造构造器</b>：直接父类未声明该描述符时不改（R8 已内联并删除中间构造器的情形属于此，
 *       凭空补一个会改变初始化语义，故宁可不动）；</li>
 *   <li><b>不猜类型</b>：{@link TypeIndex} 未知的 owner 一律不动（缺信息按「不确定」处理）；</li>
 *   <li><b>占位类型注入（DanglingTypes 的合成半边）不在本类</b>：它需要生成新类而非改写单类，属下一阶段；</li>
 *   <li>本类只改 owner 与 flag，**不增删指令、不改栈形状**，因此**无需重算** {@code StackMapTable}
 *       （取 {@code ClassWriter(reader, 0)} 原样保留帧；版本 &lt; 50 的类本就没有帧），
 *       与 {@code guard.NativeBytecodeRewriter} 同一约定；</li>
 *   <li>不改写就**原样返回输入数组**（{@code PluginClassLoader} 的 {@code rewriter} 契约要求）。</li>
 * </ul>
 *
 * <p>典型接线：{@code PluginClassLoader} 的 {@code Function<byte[], byte[]> rewriter} 可传入
 * {@code new Compatibility(index)::rewrite}（索引由归档里已知类字节建立）；本类自身不读取归档。
 */
public final class Compatibility {

    private final TypeIndex index;

    /** 无索引：只做「自引用」修补（owner == 当前类时用当前类头判断接口形态）。 */
    public Compatibility() {
        this(TypeIndex.empty());
    }

    /** 带类型索引：可修补跨类的接口形态与祖先构造器（推荐；索引由归档类字节建立）。 */
    public Compatibility(TypeIndex index) {
        this.index = index == null ? TypeIndex.empty() : index;
    }

    /**
     * 修补**单个类**的字节码。不改写即返回**原始数组**（引用相等），便于下游判断是否命中。
     *
     * @param classBytes 转换器产出的一个 {@code .class} 字节
     * @return 修补后的字节；无需修补时返回入参本身
     */
    public byte[] rewrite(byte[] classBytes) {
        if (classBytes == null || classBytes.length == 0) return classBytes;
        ClassReader reader;
        try {
            reader = new ClassReader(classBytes);
        } catch (RuntimeException malformed) {
            // 读不动就原样返回：绝不产出半成品（下游按原样加载，失败分类交给既有链路）。
            return classBytes;
        }
        // 预扫描：哪些方法的字节里出现了「外部 new」（new T，T != 当前类）。这类方法里，一条 owner 为
        // 祖先的 <init> 调用无法确定接收者是不是 this，保守起见不做祖先 owner 修补。
        Set<String> foreignNewMethods = collectForeignNewMethods(reader, reader.getClassName());

        Decision probe = new Decision();
        reader.accept(new Rewriter(null, index, foreignNewMethods, null, probe), 0);
        if (!probe.changed) return classBytes;

        int version = probe.needsV1_8 ? Math.max(probe.version, Opcodes.V1_8) : probe.version;
        ClassWriter writer = new ClassWriter(reader, 0);
        reader.accept(new Rewriter(writer, index, foreignNewMethods, version, new Decision()), 0);
        return writer.toByteArray();
    }

    // ---------- 预扫描 ----------

    /** 收集「含外部 {@code new}」的方法键（{@code name + descriptor}）。 */
    private static Set<String> collectForeignNewMethods(ClassReader reader, String thisClass) {
        Set<String> methods = new java.util.LinkedHashSet<>();
        reader.accept(new ClassVisitor(Opcodes.ASM9) {
            @Override
            public MethodVisitor visitMethod(int access, String name, String descriptor, String signature, String[] exceptions) {
                String key = name + descriptor;
                return new MethodVisitor(Opcodes.ASM9) {
                    @Override
                    public void visitTypeInsn(int opcode, String type) {
                        if (opcode == Opcodes.NEW && !type.equals(thisClass)) methods.add(key);
                    }
                };
            }
        }, ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
        return methods;
    }

    // ---------- 决策累计 ----------

    /** 一次遍历累计出的改写决策（class 文件版本、是否需要抬版本、是否真的改了东西）。 */
    private static final class Decision {
        int version = Opcodes.V1_8;
        boolean needsV1_8;
        boolean changed;
    }

    // ---------- 改写器 ----------

    /**
     * 单遍改写器，同时承担「只决策」与「真写出」两种角色：
     * {@code writer == null} 时只走 {@link MethodVisitor} 决策、不落盘；否则把改写后的指令写进 writer。
     */
    private static final class Rewriter extends ClassVisitor {
        private final TypeIndex index;
        private final Set<String> foreignNewMethods;
        private final Integer versionOverride;
        private final Decision decision;

        private String thisClass;
        private String superClass;
        private boolean thisIsInterface;
        private boolean currentIsInit;
        private boolean currentForeignNew;

        Rewriter(ClassVisitor writer, TypeIndex index, Set<String> foreignNewMethods,
                 Integer versionOverride, Decision decision) {
            super(Opcodes.ASM9, writer);
            this.index = index;
            this.foreignNewMethods = foreignNewMethods;
            this.versionOverride = versionOverride;
            this.decision = decision;
        }

        @Override
        public void visit(int version, int access, String name, String signature, String superName, String[] interfaces) {
            this.thisClass = name;
            this.superClass = superName;
            this.thisIsInterface = (access & Opcodes.ACC_INTERFACE) != 0;
            decision.version = version;
            super.visit(versionOverride != null ? versionOverride : version, access, name, signature, superName, interfaces);
        }

        @Override
        public MethodVisitor visitMethod(int access, String name, String descriptor, String signature, String[] exceptions) {
            this.currentIsInit = "<init>".equals(name);
            this.currentForeignNew = foreignNewMethods.contains(name + descriptor);
            MethodVisitor downstream = super.visitMethod(access, name, descriptor, signature, exceptions);
            return new MethodVisitor(Opcodes.ASM9, downstream) {
                @Override
                public void visitMethodInsn(int opcode, String owner, String name2, String descriptor2, boolean isInterface) {
                    Repair repair = decide(opcode, owner, name2, descriptor2, isInterface);
                    if (repair != null) {
                        decision.changed = true;
                        if (repair.interfaceStaticRequiresV1_8()) decision.needsV1_8 = true;
                        super.visitMethodInsn(opcode, repair.owner(), name2, descriptor2, repair.isInterface());
                    } else {
                        super.visitMethodInsn(opcode, owner, name2, descriptor2, isInterface);
                    }
                }
            };
        }

        /** 一次修补决策；无需改动时返回 {@code null}。 */
        private Repair decide(int opcode, String owner, String name, String descriptor, boolean isInterface) {
            String newOwner = owner;
            boolean newIsInterface = isInterface;
            boolean touched = false;
            boolean interfaceStaticRequiresV1_8 = false;

            // (1) 接口调用桥：把 flag 修成 JVM 要求的形态（只在 owner 形态已知时）。
            Boolean desired = desiredInterface(opcode, owner);
            if (desired != null && desired != isInterface) {
                newIsInterface = desired;
                touched = true;
                if (desired && (opcode == Opcodes.INVOKESTATIC || opcode == Opcodes.INVOKESPECIAL)) {
                    interfaceStaticRequiresV1_8 = true;
                }
            }

            // (2) 构造器祖先 owner 修补：只在本类的 <init> 里、且不含外部 new 时。
            if (currentIsInit && !currentForeignNew
                    && opcode == Opcodes.INVOKESPECIAL && "<init>".equals(name)) {
                String repaired = directSuperclassConstructor(owner, descriptor);
                if (repaired != null) {
                    newOwner = repaired;
                    newIsInterface = false;
                    touched = true;
                }
            }

            if (!touched) return null;
            return new Repair(newOwner, newIsInterface, interfaceStaticRequiresV1_8);
        }

        /**
         * 调用点应有的 {@code itf} 形态：接口 → true，类 → false，未知 → {@code null}（不动）。
         * {@code invokeinterface} 不在此处翻转（ASM 约定其 itf 恒 true）。
         */
        private Boolean desiredInterface(int opcode, String owner) {
            if (opcode != Opcodes.INVOKESTATIC && opcode != Opcodes.INVOKESPECIAL && opcode != Opcodes.INVOKEVIRTUAL) {
                return null;
            }
            TypeIndex.Kind kind = index.kind(owner);
            if (kind == TypeIndex.Kind.UNKNOWN && owner.equals(thisClass)) {
                kind = thisIsInterface ? TypeIndex.Kind.INTERFACE : TypeIndex.Kind.CLASS;
            }
            if (kind == TypeIndex.Kind.INTERFACE) return Boolean.TRUE;
            if (kind == TypeIndex.Kind.CLASS) return Boolean.FALSE;
            return null;
        }

        /**
         * 返回应改成的 owner（直接父类），或 {@code null} 表示不改。
         *
         * <p>条件：{@code X} 是当前类的**严格祖先**、不是直接父类，且索引确认直接父类**声明**了该构造器。
         * 后一条保证改后一定可链接；缺信息（索引没有直接父类的构造器）即不改，绝不臆造。
         */
        private String directSuperclassConstructor(String owner, String descriptor) {
            if (superClass == null) return null;
            if (owner.equals(superClass)) return null;
            if (!index.isStrictAncestor(owner, thisClass)) return null;
            if (!index.declaresConstructor(superClass, descriptor)) return null;
            return superClass;
        }
    }

    /** 一次修补：新的 owner 与 itf 形态，外加「是否需要抬到 class 版本 52」。 */
    private record Repair(String owner, boolean isInterface, boolean interfaceStaticRequiresV1_8) {}

    // ---------- 归档级便捷入口 ----------

    /**
     * 对一组类字节建立索引后逐类改写（就地替换）。
     *
     * <p>索引需要**先看全量**再逐类改，因此本方法先建索引再改写；返回的列表与输入**等长同序**。
     * 注意：本方法只在**同一批**类之间建立索引；跨批（例如惰性逐类加载）无法看到完整祖先链，
     * 此时应显式构造 {@link TypeIndex} 并复用同一个 {@link Compatibility}。
     */
    public static List<byte[]> rewriteAll(List<byte[]> classes) {
        TypeIndex index = TypeIndex.of(classes);
        Compatibility compatibility = new Compatibility(index);
        List<byte[]> result = new java.util.ArrayList<>(classes.size());
        for (byte[] bytes : classes) result.add(compatibility.rewrite(bytes));
        return result;
    }
}
