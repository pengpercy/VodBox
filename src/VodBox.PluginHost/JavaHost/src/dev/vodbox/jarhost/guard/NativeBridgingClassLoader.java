package dev.vodbox.jarhost.guard;

import java.io.IOException;
import java.io.InputStream;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.function.Function;

/**
 * 加载插件类的 {@link URLClassLoader}：在 {@code findClass} 阶段对插件类应用
 * {@link NativeBytecodeRewriter}（native 方法去 native 化 → 转发到 {@link NativeMethodBridge}）。
 *
 * <p>这是桥接的**类加载策略**：不改写磁盘上的归档，只在类被定义前改写其字节。父加载器优先的委派语义
 * 不变（宿主自带的 {@code android.*}/{@code dalvik.*} 与 {@code dev.vodbox.jarhost.*} 仍由父加载）。
 * {@code rewriter} 为 null（ASM 缺失）时退化为普通 {@link URLClassLoader}。
 */
public final class NativeBridgingClassLoader extends URLClassLoader {
    private final Function<byte[], byte[]> rewriter;

    public NativeBridgingClassLoader(URL[] urls, ClassLoader parent, Function<byte[], byte[]> rewriter) {
        super(urls, parent);
        this.rewriter = rewriter;
    }

    @Override
    protected Class<?> findClass(String name) throws ClassNotFoundException {
        if (rewriter != null) {
            String resource = name.replace('.', '/') + ".class";
            URL location = findResource(resource);
            if (location != null) {
                byte[] original;
                try (InputStream input = location.openStream()) {
                    original = input.readAllBytes();
                } catch (IOException error) {
                    throw new ClassNotFoundException(name + "（读取类字节失败：" + error + "）", error);
                }
                byte[] transformed = rewriter.apply(original);
                return defineClass(name, transformed, 0, transformed.length);
            }
        }
        return super.findClass(name);
    }
}
