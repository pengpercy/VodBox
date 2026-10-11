package dev.vodbox.jarhost.dex;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;

/**
 * 只读的**类型索引**：从一组 JVM 类字节里收集「是不是接口 / 直接父类 / 声明的构造器描述符」，
 * 供 {@link Compatibility} 依据**公开 JVM 规则**判断调用形态（接口 vs 类）与构造器祖先关系。
 *
 * <p>这是 VodBox 自有实现（用 ASM 解析，ASM 为 BSD-3-Clause，随 {@code dex-tools/} 提供），
 * 不含任何上游/GPL 代码。索引是 {@link Compatibility} 做「有据可依」修补的前提：
 * 只有当索引**确认**某个 owner 是接口、或某个父类**确实声明**了对应构造器时，改写才会发生；
 * 索引缺信息时不做任何猜测。
 *
 * <p><b>边界（不夸大）</b>：本类只回答「已知类型是什么」，不解析运行时类路径，也不合并 DEX/DEX 之外
 * 的库；因此它描述的祖先链止于归档里**带到**的类。链走到未知类型即停止，对应查询按「不确定」处理。
 */
public final class TypeIndex {

    /** 已知类型的三态：确定是类、确定是接口、未知（索引里没有）。 */
    public enum Kind { CLASS, INTERFACE, UNKNOWN }

    /** 单个已知类型的事实。{@code superName} 为 {@code null} 表示 {@code java/lang/Object} 或无父类。 */
    private record TypeInfo(Kind kind, String superName, Set<String> constructors) {}

    private final Map<String, TypeInfo> types;

    private TypeIndex(Map<String, TypeInfo> types) {
        this.types = types;
    }

    /** 空索引：任何查询都返回「未知」，{@link Compatibility} 据此退化为「只做自引用修补」。 */
    public static TypeIndex empty() {
        return new TypeIndex(Map.of());
    }

    /**
     * 从一组类字节建立索引。不可解析的条目被**跳过**（不抛），这样单类损坏不会让整包索引失败。
     */
    public static TypeIndex of(Iterable<byte[]> classes) {
        Map<String, TypeInfo> collected = new LinkedHashMap<>();
        if (classes != null) {
            for (byte[] bytes : classes) {
                if (bytes == null || bytes.length == 0) continue;
                try {
                    ClassReader reader = new ClassReader(bytes);
                    Indexer indexer = new Indexer();
                    reader.accept(indexer, ClassReader.SKIP_CODE | ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
                    if (indexer.name != null) {
                        collected.put(indexer.name, new TypeInfo(indexer.kind, indexer.superName, indexer.constructors));
                    }
                } catch (RuntimeException unreadable) {
                    // 解析不了就跳过该条目：索引少一条 ≠ 猜测，查询会返回 UNKNOWN。
                }
            }
        }
        return new TypeIndex(collected);
    }

    /** 便捷重载：把若干类字节直接建索引（调用方负责读盘/读归档）。 */
    public static TypeIndex of(byte[]... classes) {
        return of(java.util.Arrays.asList(classes));
    }

    /** 类型形态；索引里没有该类型时返回 {@link Kind#UNKNOWN}（不确定，不猜）。 */
    public Kind kind(String internalName) {
        TypeInfo info = types.get(internalName);
        return info == null ? Kind.UNKNOWN : info.kind();
    }

    /** 直接父类内部名；索引里没有该类型时返回 {@code null}（无法判断）。 */
    public String superName(String internalName) {
        TypeInfo info = types.get(internalName);
        return info == null ? null : info.superName();
    }

    /** 某个已知类型是否**直接声明**了描述符为 {@code descriptor} 的 {@code <init>}（构造器不被继承）。 */
    public boolean declaresConstructor(String internalName, String descriptor) {
        TypeInfo info = types.get(internalName);
        return info != null && info.constructors().contains(descriptor);
    }

    /**
     * {@code candidate} 是否是 {@code type} 的**严格祖先**（沿 {@code superName} 链向上，不含 {@code type} 本身）。
     *
     * <p>链走到索引不认识的类型即停止并返回 {@code false}——**不确定按否处理**，绝不凭前缀/命名猜测继承关系。
     */
    public boolean isStrictAncestor(String candidate, String type) {
        if (candidate == null || type == null || candidate.equals(type)) return false;
        Set<String> visited = new LinkedHashSet<>();
        String current = superName(type);
        while (current != null && visited.add(current)) {
            if (current.equals(candidate)) return true;
            current = superName(current);
        }
        return false;
    }

    /** 索引里的类型数量（诊断/自测用）。 */
    public int size() {
        return types.size();
    }

    /** 单调解析一个类头：形态、父类、构造器描述符。 */
    private static final class Indexer extends ClassVisitor {
        String name;
        String superName;
        Kind kind = Kind.UNKNOWN;
        final Set<String> constructors = new LinkedHashSet<>();

        Indexer() {
            super(Opcodes.ASM9);
        }

        @Override
        public void visit(int version, int access, String name, String signature, String superName, String[] interfaces) {
            this.name = name;
            this.superName = superName;
            this.kind = (access & Opcodes.ACC_INTERFACE) != 0 ? Kind.INTERFACE : Kind.CLASS;
        }

        @Override
        public MethodVisitor visitMethod(int access, String name, String descriptor, String signature, String[] exceptions) {
            if ("<init>".equals(name)) constructors.add(descriptor);
            return null;
        }
    }
}
