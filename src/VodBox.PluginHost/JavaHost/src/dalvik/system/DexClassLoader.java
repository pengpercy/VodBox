package dalvik.system;

import java.io.File;
import java.io.IOException;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.ArrayList;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * Android {@code dalvik.system.DexClassLoader} 最小子集（VodBox 自有实现）。
 *
 * 语义边界（明确、不伪装）：本宿主运行在 JVM 上，只能加载**JVM 字节码**。
 * {@code dexPath} 每段可以是
 * <ul>
 *   <li>一个目录，或</li>
 *   <li>一个包含 {@code .class} 的 zip/jar（即 {@code classes.dex} 已被 DEX→JVM 转换后的产物）。</li>
 * </ul>
 * 其它形态（原生 {@code .dex}/{@code .apk} 且其中没有已转换的类）会抛
 * {@link UnsupportedOperationException}，由宿主归类为 {@code unsupported_android}——
 * **绝不**把未转换的 DEX 当作可加载。{@code optimizedDirectory}/{@code librarySearchPath}
 * 在本宿主无对应概念，取其一并保留但不使用；原生库加载另由 {@code unsupported_native_library} 分类。
 */
public class DexClassLoader extends ClassLoader {

    private final URLClassLoader delegate;

    public DexClassLoader(String dexPath, String optimizedDirectory, String librarySearchPath, ClassLoader parent) {
        super(parent);
        this.delegate = new dev.vodbox.jarhost.guard.NativeBridgingClassLoader(buildUrls(dexPath), parent,
                dev.vodbox.jarhost.guard.NativeRewriterFactory.rewriter());
        dev.vodbox.jarhost.guard.NativeMethodBridge.registerLoader(this);
    }

    @Override
    protected Class<?> findClass(String name) throws ClassNotFoundException {
        return delegate.loadClass(name);
    }

    @Override
    protected URL findResource(String name) {
        return delegate.findResource(name);
    }

    @Override
    protected java.util.Enumeration<URL> findResources(String name) throws IOException {
        return delegate.findResources(name);
    }

    @Override
    protected String findLibrary(String name) {
        // 本宿主不加载原生库：返回 null 让调用方走 UnsatisfiedLinkError 路径（由宿主明确分类）。
        return null;
    }

    private static URL[] buildUrls(String dexPath) {
        List<URL> urls = new ArrayList<>();
        if (dexPath != null) {
            for (String entry : dexPath.split(File.pathSeparator)) {
                if (entry.isEmpty()) continue;
                File file = new File(dev.vodbox.jarhost.guard.NativeMethodBridge.hostPath(entry));
                if (!isLoadableJar(file) && !file.isDirectory() && file.isFile()) {
                    java.nio.file.Path materialized = dev.vodbox.jarhost.guard.NativeMethodBridge.materialize(file.toPath());
                    if (materialized != null) file = materialized.toFile();
                }
                if (file.isDirectory() || isLoadableJar(file)) {
                    try {
                        urls.add(file.toURI().toURL());
                    } catch (java.net.MalformedURLException malformed) {
                        throw new IllegalArgumentException("DexClassLoader 路径非法：" + entry, malformed);
                    }
                } else {
                    throw new UnsupportedOperationException(
                            "DexClassLoader 需要已转换的 JVM 类/DEX→JVM 产物：" + entry);
                }
            }
        }
        if (urls.isEmpty()) {
            throw new UnsupportedOperationException("DexClassLoader 没有可加载的类路径");
        }
        return urls.toArray(new URL[0]);
    }

    private static boolean isLoadableJar(File file) {
        if (!file.isFile()) return false;
        try (ZipFile zip = new ZipFile(file)) {
            var entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                if (!entry.isDirectory() && entry.getName().endsWith(".class")) return true;
            }
            return false;
        } catch (IOException notAZip) {
            return false;
        }
    }
}
