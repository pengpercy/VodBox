package dev.vodbox.jarhost.guard;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import java.util.zip.ZipOutputStream;

/**
 * 把插件字节码里的 {@code native} 方法改写为「转发到 {@link NativeMethodBridge}」的普通方法。
 *
 * <p>这是**透明桥接**的核心：插件依旧按原样声明并调用自己的 native 方法，改写只是把调用点接到同进程的
 * ARM64 模拟后端。覆盖：
 * <ul>
 *   <li><b>静态 / 实例</b>：实例方法把 {@code this} 作为 receiver 传入；静态方法传 null；</li>
 *   <li><b>多类型参数 / 返回</b>：8 种原始类型 + 对象 + 数组，原始类型自动装箱/拆箱，对象/数组按描述符 CHECKCAST；</li>
 *   <li><b>native 装载入口</b>：把 {@code java/lang/System.load}/{@code loadLibrary}（{@code (Ljava/lang/String;)V}）
 *       改写到 {@link NativeMethodBridge#load}/{@link NativeMethodBridge#loadLibrary}，让库进模拟器而非宿主进程。</li>
 * </ul>
 *
 * <p><b>边界（明确、不夸大）</b>：不含 native 方法的类原样返回（不做任何重排，避免误伤）；native 构造器
 * 无法桥接，改写为抛 {@link UnsupportedOperationException}（由宿主归类为 {@code unsupported_native_library}），
 * <b>绝不</b>静默生成半成品。
 *
 * <p>依赖：ASM（{@code org.objectweb.asm}，BSD-3-Clause）——仅在运行时经 {@link NativeRewriterFactory}
 * 反射发现，ASM 缺失时本类根本不会被加载，桥接整体降级为「不启用」而不是崩溃。
 */
public final class NativeBytecodeRewriter {
    /** 桥接门面的内部类名。 */
    private static final String BRIDGE_OWNER = "dev/vodbox/jarhost/guard/NativeMethodBridge";
    private static final String INVOKE = "invoke";
    private static final String INVOKE_DESCRIPTOR =
            "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Object;[Ljava/lang/Object;)Ljava/lang/Object;";
    /** 生成体的栈上界：owner/sig/receiver + Object[] + 逐参数装箱，取下宽上界（允许大于实际）。 */
    private static final int GENERATED_MAX_STACK = 12;

    private static final String JAVA_SYSTEM = "java/lang/System";

    /**
     * 改写单个类；不含 native 方法时返回**原始字节**（不做重排）。
     * 含 native 方法时用 {@code ClassWriter(reader, 0)} + 方法字节复制，未改动的成员原样保留。
     */
    public byte[] rewrite(byte[] classBytes) {
        ClassReader reader = new ClassReader(classBytes);
        boolean malformedObject = new String(classBytes, java.nio.charset.StandardCharsets.ISO_8859_1).contains("java/util/Object");
        if (malformedObject) {
            ClassWriter repaired = new ClassWriter(0);
            reader.accept(new org.objectweb.asm.commons.ClassRemapper(repaired, new org.objectweb.asm.commons.Remapper() {
                @Override public String map(String name) {
                    return "java/util/Object".equals(name) ? "java/lang/Object" : name;
                }
            }), 0);
            classBytes = repaired.toByteArray();
            reader = new ClassReader(classBytes);
        }
        if (!declaresNativeMethod(reader)) return classBytes;
        final String bridgeClassName = reader.getClassName();
        final ClassWriter writer = new ClassWriter(reader, 0);
        ClassVisitor visitor = new ClassVisitor(Opcodes.ASM9, writer) {
            @Override
            public MethodVisitor visitMethod(int access, String name, String descriptor, String signature, String[] exceptions) {
                if ((access & Opcodes.ACC_NATIVE) != 0) {
                    MethodVisitor target = super.visitMethod(access & ~Opcodes.ACC_NATIVE, name, descriptor, signature, exceptions);
                    emitBridgeBody(target, bridgeClassName, name, descriptor, (access & Opcodes.ACC_STATIC) != 0);
                    // 返回 null：告诉 ClassReader 该方法已由本层自行生成，不要再读原（native）字节码。
                    return null;
                }
                MethodVisitor downstream = super.visitMethod(access, name, descriptor, signature, exceptions);
                if (downstream == null) return null;
                return new MethodVisitor(Opcodes.ASM9, downstream) {
                    @Override
                    public void visitMethodInsn(int opcode, String owner, String name2, String descriptor2, boolean isInterface) {
                        if (opcode == Opcodes.INVOKESTATIC
                                && JAVA_SYSTEM.equals(owner)
                                && ("load".equals(name2) || "loadLibrary".equals(name2))
                                && "(Ljava/lang/String;)V".equals(descriptor2)) {
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, BRIDGE_OWNER, name2, descriptor2, false);
                            return;
                        }
                        super.visitMethodInsn(opcode, owner, name2, descriptor2, isInterface);
                    }
                };
            }
        };
        reader.accept(visitor, 0);
        return writer.toByteArray();
    }

    /** 归档里是否至少有一个类声明了 native 方法（判定是否需要启用桥接类加载器）。 */
    public static boolean hasNativeMethods(Path archive) throws IOException {
        if (archive == null || !Files.isRegularFile(archive)) return false;
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            var entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                if (entry.isDirectory() || !entry.getName().endsWith(".class")) continue;
                byte[] bytes;
                try (InputStream input = zip.getInputStream(entry)) {
                    bytes = input.readAllBytes();
                }
                if (declaresNativeMethod(new ClassReader(bytes))) return true;
            }
        }
        return false;
    }

    /** 把归档整体改写到一个新 JAR（先临时文件再原子改名）；归档不含 native 方法时返回 false、不写出。 */
    public boolean rewriteJar(Path archive, Path output) throws IOException {
        if (!hasNativeMethods(archive)) return false;
        Path directory = output.toAbsolutePath().getParent();
        if (directory != null) Files.createDirectories(directory);
        Path temporary = Files.createTempFile(directory, "native-bridge-", ".jar");
        try {
            try (ZipFile input = new ZipFile(archive.toFile());
                 ZipOutputStream outputStream = new ZipOutputStream(Files.newOutputStream(temporary))) {
                var entries = input.entries();
                while (entries.hasMoreElements()) {
                    ZipEntry entry = entries.nextElement();
                    if (entry.isDirectory()) continue;
                    byte[] bytes;
                    try (InputStream stream = input.getInputStream(entry)) {
                        bytes = stream.readAllBytes();
                    }
                    if (entry.getName().endsWith(".class")) bytes = rewrite(bytes);
                    outputStream.putNextEntry(new ZipEntry(entry.getName()));
                    outputStream.write(bytes);
                    outputStream.closeEntry();
                }
            }
            Files.move(temporary, output, StandardCopyOption.REPLACE_EXISTING);
            return true;
        } finally {
            Files.deleteIfExists(temporary);
        }
    }

    // ---------- 生成桥接方法体 ----------

    private static boolean declaresNativeMethod(ClassReader reader) {
        final boolean[] found = {false};
        reader.accept(new ClassVisitor(Opcodes.ASM9) {
            @Override
            public MethodVisitor visitMethod(int access, String name, String descriptor, String signature, String[] exceptions) {
                if ((access & Opcodes.ACC_NATIVE) != 0) found[0] = true;
                return null;
            }
        }, ClassReader.SKIP_CODE | ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
        return found[0];
    }

    /**
     * 生成方法体：{@code NativeMethodBridge.invoke(owner, "name+descriptor", receiverOrNull, new Object[]{boxed...})}
     * 并把返回值拆箱/转型。生成体是**直线代码**（无分支），因此无需 StackMapTable（JVM 按隐式初始帧处理）。
     */
    private static void emitBridgeBody(MethodVisitor method, String owner, String name, String descriptor, boolean isStatic) {
        method.visitCode();
        if ("<init>".equals(name) || "<clinit>".equals(name)) {
            method.visitTypeInsn(Opcodes.NEW, "java/lang/UnsupportedOperationException");
            method.visitInsn(Opcodes.DUP);
            method.visitLdcInsn("插件声明了 native 构造器/静态初始化，本宿主无法桥接：" + owner + "." + name);
            method.visitMethodInsn(Opcodes.INVOKESPECIAL, "java/lang/UnsupportedOperationException",
                    "<init>", "(Ljava/lang/String;)V", false);
            method.visitInsn(Opcodes.ATHROW);
            method.visitMaxs(3, Math.max(1, (isStatic ? 0 : 1) + argumentSlots(descriptor)));
            method.visitEnd();
            return;
        }
        method.visitLdcInsn(owner);
        method.visitLdcInsn(name + descriptor);
        if (isStatic) {
            method.visitInsn(Opcodes.ACONST_NULL);
        } else {
            method.visitVarInsn(Opcodes.ALOAD, 0);
        }
        Type[] arguments = Type.getArgumentTypes(descriptor);
        pushInt(method, arguments.length);
        method.visitTypeInsn(Opcodes.ANEWARRAY, "java/lang/Object");
        int slot = isStatic ? 0 : 1;
        for (int index = 0; index < arguments.length; index++) {
            Type type = arguments[index];
            method.visitInsn(Opcodes.DUP);
            pushInt(method, index);
            method.visitVarInsn(type.getOpcode(Opcodes.ILOAD), slot);
            if (type.getSort() < Type.ARRAY) box(method, type);
            method.visitInsn(Opcodes.AASTORE);
            slot += type.getSize();
        }
        method.visitMethodInsn(Opcodes.INVOKESTATIC, BRIDGE_OWNER, INVOKE, INVOKE_DESCRIPTOR, false);
        emitReturn(method, Type.getReturnType(descriptor));
        method.visitMaxs(GENERATED_MAX_STACK, Math.max(1, slot));
        method.visitEnd();
    }

    private static void emitReturn(MethodVisitor method, Type result) {
        switch (result.getSort()) {
            case Type.VOID:
                method.visitInsn(Opcodes.POP);
                method.visitInsn(Opcodes.RETURN);
                return;
            case Type.OBJECT:
            case Type.ARRAY:
                method.visitTypeInsn(Opcodes.CHECKCAST, result.getInternalName());
                method.visitInsn(Opcodes.ARETURN);
                return;
            default:
                Type wrapper = wrapperType(result);
                method.visitTypeInsn(Opcodes.CHECKCAST, wrapper.getInternalName());
                method.visitMethodInsn(Opcodes.INVOKEVIRTUAL, wrapper.getInternalName(),
                        result.getClassName() + "Value", "()" + result.getDescriptor(), false);
                method.visitInsn(result.getOpcode(Opcodes.IRETURN));
        }
    }

    private static void box(MethodVisitor method, Type primitive) {
        Type wrapper = wrapperType(primitive);
        method.visitMethodInsn(Opcodes.INVOKESTATIC, wrapper.getInternalName(), "valueOf",
                "(" + primitive.getDescriptor() + ")L" + wrapper.getInternalName() + ";", false);
    }

    private static void pushInt(MethodVisitor method, int value) {
        method.visitLdcInsn(Integer.valueOf(value));
    }

    private static int argumentSlots(String descriptor) {
        int slots = 0;
        for (Type type : Type.getArgumentTypes(descriptor)) slots += type.getSize();
        return slots;
    }

    private static Type wrapperType(Type primitive) {
        switch (primitive.getSort()) {
            case Type.BOOLEAN: return Type.getType(Boolean.class);
            case Type.BYTE: return Type.getType(Byte.class);
            case Type.CHAR: return Type.getType(Character.class);
            case Type.SHORT: return Type.getType(Short.class);
            case Type.INT: return Type.getType(Integer.class);
            case Type.LONG: return Type.getType(Long.class);
            case Type.FLOAT: return Type.getType(Float.class);
            case Type.DOUBLE: return Type.getType(Double.class);
            default: throw new IllegalArgumentException("非原始类型，无法装箱：" + primitive);
        }
    }
}
