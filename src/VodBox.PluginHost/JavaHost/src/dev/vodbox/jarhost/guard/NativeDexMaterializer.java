package dev.vodbox.jarhost.guard;

import dev.vodbox.jarhost.dex.DexArchive;
import dev.vodbox.jarhost.dex.DexCache;
import dev.vodbox.jarhost.dex.DexConverter;
import dev.vodbox.jarhost.dex.DexToolchain;

import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * FTY 这类「运行时解密出原生 DEX」场景的**物化**实现：把插件运行时产出的 {@code .dex}（或已打包的归档、
 * 或含 {@code *.dex} 的目录）经 {@link DexConverter} 转成可被 JVM 类加载器读取的产物，返回其路径。
 *
 * <p>这是 {@link NativeMethodBridge#materialize} 的默认后端——宿主在原生会话建立时装上它。
 * <b>不伪造成功</b>：DEX→JVM 引擎缺失、结构损坏、版本不支持、或产物不含 {@code .class} 时一律返回 null，
 * 调用方（如 {@code dalvik.system.DexClassLoader}）据此保持「未转换即明确拒绝」的语义。
 *
 * <p>入参归一化：
 * <ul>
 *   <li>已是 zip/jar（{@code PK\\x03\\x04}）→ 直接交给转换器（按 {@code classes*.dex} 约定读取）；</li>
 *   <li>裸 {@code .dex}（magic {@code dex\\n}）→ 先包一层 {@code classes.dex} 的 zip 再转换；</li>
 *   <li>目录（内容 {@code *.dex}）→ 打包 {@code classes.dex}/{@code classes2.dex}… 后转换；</li>
 *   <li>其它 → 返回 null（明确失败，不做猜测）。</li>
 * </ul>
 */
public final class NativeDexMaterializer implements NativeMethodBridge.DexMaterializer {
    private final Path hostJarDirectory;
    private final Path outputDirectory;

    public NativeDexMaterializer(Path hostJarDirectory, Path outputDirectory) {
        this.hostJarDirectory = hostJarDirectory;
        this.outputDirectory = outputDirectory;
    }

    @Override
    public Path materialize(Path artifact) {
        if (artifact == null) return null;
        try {
            if (Files.isDirectory(artifact)) return convert(artifact, packageDirectory(artifact));
            if (!Files.isRegularFile(artifact)) return null;
            if (isZip(artifact)) return convert(artifact, artifact);
            if (isDex(artifact)) return convert(artifact, wrapDex(artifact));
            System.err.println("host: 运行时物化跳过（不是 zip/dex/目录）：" + artifact);
            return null;
        } catch (Throwable error) {
            System.err.println("host: 运行时物化失败（" + artifact + "）：" + error);
            return null;
        }
    }

    private Path convert(Path original, Path archive) {
        try {
            Files.createDirectories(outputDirectory);
            Path output = outputDirectory.resolve(DexCache.sha256(archive) + ".jar");
            if (DexArchive.isLoadableJar(output)) return output;   // 仅当产物确实可加载才算命中
            List<Path> classpath = DexToolchain.discover(hostJarDirectory);
            DexConverter.Result result = DexConverter.convert(archive, output, classpath);
            if (!DexArchive.isLoadableJar(output)) {
                System.err.println("host: 运行时 DEX→JVM 产物不含 .class，视为失败：" + output);
                return null;
            }
            System.err.println("host: 运行时 DEX→JVM 成功 " + original.getFileName()
                    + " -> " + output.getFileName() + "（" + result.classCount() + " 类，" + result.dexFileCount() + " dex）");
            return output;
        } catch (Throwable error) {
            System.err.println("host: 运行时 DEX→JVM 失败（" + original + "）：" + error);
            return null;
        }
    }

    /** 裸 {@code .dex} → 只含 {@code classes.dex} 的 zip。 */
    private Path wrapDex(Path dex) throws IOException, dev.vodbox.jarhost.dex.DexException {
        Files.createDirectories(outputDirectory);
        Path wrapped = outputDirectory.resolve(DexCache.sha256(dex) + ".dex.zip");
        if (Files.isRegularFile(wrapped)) return wrapped;
        Path temporary = Files.createTempFile(outputDirectory, "native-dex-", ".zip");
        try (ZipOutputStream output = new ZipOutputStream(Files.newOutputStream(temporary))) {
            output.putNextEntry(new ZipEntry("classes.dex"));
            Files.copy(dex, output);
            output.closeEntry();
        }
        Files.move(temporary, wrapped, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        return wrapped;
    }

    /** 目录里的 {@code *.dex} → 按 Android 约定重排为 {@code classes.dex}/{@code classesN.dex} 的 zip。 */
    private Path packageDirectory(Path directory) throws IOException {
        Files.createDirectories(outputDirectory);
        String key = Integer.toHexString(directory.toAbsolutePath().normalize().toString().hashCode());
        Path archive = outputDirectory.resolve(key + ".dir.zip");
        if (Files.isRegularFile(archive)) return archive;
        Path temporary = Files.createTempFile(outputDirectory, "native-dex-dir-", ".zip");
        try (ZipOutputStream output = new ZipOutputStream(Files.newOutputStream(temporary))) {
            int index = 0;
            try (var stream = Files.list(directory)) {
                for (Path entry : stream.sorted().toList()) {
                    if (!Files.isRegularFile(entry) || !entry.getFileName().toString().endsWith(".dex")) continue;
                    index++;
                    String name = index == 1 ? "classes.dex" : "classes" + index + ".dex";
                    output.putNextEntry(new ZipEntry(name));
                    Files.copy(entry, output);
                    output.closeEntry();
                }
            }
            if (index == 0) throw new IOException("目录里没有 .dex：" + directory);
        }
        Files.move(temporary, archive, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        return archive;
    }

    /** 前 4 字节是否为 zip 本地文件头。 */
    static boolean isZip(Path path) {
        byte[] header = prefix(path, 4);
        return header.length == 4 && header[0] == 'P' && header[1] == 'K' && (header[2] == 3 || header[2] == 5 || header[2] == 7);
    }

    /** 前 8 字节是否为 DEX（{@code dex\n0NN\0}）。 */
    static boolean isDex(Path path) {
        byte[] header = prefix(path, 8);
        return header.length == 8
                && header[0] == 'd' && header[1] == 'e' && header[2] == 'x' && header[3] == '\n'
                && header[7] == 0;
    }

    private static byte[] prefix(Path path, int length) {
        try (InputStream input = Files.newInputStream(path)) {
            return input.readNBytes(length);
        } catch (IOException unreadable) {
            return new byte[0];
        }
    }
}
