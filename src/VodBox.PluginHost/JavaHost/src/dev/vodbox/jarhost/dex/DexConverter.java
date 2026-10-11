package dev.vodbox.jarhost.dex;

import java.nio.file.Path;
import java.util.List;

/**
 * DEX→JVM 转换的高层编排：先做结构检查（多 DEX、版本、头字段），再调用 {@link DexToolchain}
 * 发现的转换器，最后校验产物确实是含 {@code .class} 的可加载 JAR。
 *
 * 结构检查先行，使失败分类更准确：损坏的 DEX 报 {@link DexCodes#CORRUPT}、版本过新报
 * {@link DexCodes#UNSUPPORTED_VERSION}，都不必等到转换器里去猜。
 *
 * <b>边界（不夸大）</b>：本类只负责把 DEX 变成 JVM 字节码；转换成功后插件若引用 {@code android.*}
 * 等缺失类，仍会在加载阶段以 {@code unsupported_android} 明确失败，绝不被这里伪装成「可运行」。
 */
public final class DexConverter {
    private DexConverter() {}

    private static boolean rawDex(Path input) {
        try (var stream = java.nio.file.Files.newInputStream(input)) {
            byte[] magic = stream.readNBytes(4);
            return java.util.Arrays.equals(magic, new byte[]{'d', 'e', 'x', 10});
        } catch (java.io.IOException error) { return false; }
    }

    /** 一次成功转换的事实。 */
    public record Result(int dexFileCount, String dexVersion, int classCount) {}

    /**
     * 把含 DEX 的归档 {@code archive} 转换为可加载的 JVM JAR {@code outputJar}。
     * 归档不含 DEX 时调用方不应走到这里（由上层先判断）。
     */
    public static Result convert(Path archive, Path outputJar, List<Path> classpath) throws DexException {
        if (rawDex(archive)) {
            Path wrapped = null;
            try {
                java.nio.file.Files.createDirectories(outputJar.toAbsolutePath().getParent());
                wrapped = java.nio.file.Files.createTempFile(outputJar.toAbsolutePath().getParent(), "raw-dex-", ".zip");
                try (var zip = new java.util.zip.ZipOutputStream(java.nio.file.Files.newOutputStream(wrapped))) {
                    zip.putNextEntry(new java.util.zip.ZipEntry("classes.dex"));
                    java.nio.file.Files.copy(archive, zip);
                    zip.closeEntry();
                }
                return convert(wrapped, outputJar, classpath);
            } catch (java.io.IOException error) {
                throw new DexException(DexCodes.CORRUPT, "读取裸 DEX 失败", error);
            } finally {
                if (wrapped != null) try { java.nio.file.Files.deleteIfExists(wrapped); } catch (java.io.IOException ignored) { }
            }
        }
        DexArchive.Inspected inspected = DexArchive.inspect(archive);
        DexToolchain.convert(archive, outputJar, classpath);
        if (!DexArchive.isLoadableJar(outputJar)) {
            throw new DexException(DexCodes.CONVERSION_FAILED,
                    "转换产物不含任何 .class，视为转换失败（输入 " + archive.getFileName() + "）。");
        }
        int classCount = DexArchive.countClasses(outputJar);
        if (classCount < 0) {
            throw new DexException(DexCodes.CONVERSION_FAILED, "无法读取转换产物：" + outputJar.getFileName());
        }
        return new Result(inspected.dexFileCount(), inspected.version(), classCount);
    }

    /**
     * **惰性路径**：把归档里的**单个类** {@code internalName}（形如 {@code com/foo/Bar}）转换为
     * 只含该类的 JVM JAR，并校验产物确实含它（不含即失败，绝不把空产物当成功）。
     *
     * <p>结构检查先行的理由同 {@link #convert}：损坏的 DEX 应报 {@link DexCodes#CORRUPT}，
     * 版本过新应报 {@link DexCodes#UNSUPPORTED_VERSION}，不必等到转换器里去猜。
     *
     * <p>类的**父类/接口**不随本产物带出（JVM 在链接时按需解析），这与 ART 的按需加载一致；
     * 因此本方法的返回值只描述「该类已转出」，不代表插件可运行——引用未实现 Android 类的插件
     * 仍会在加载/链接阶段以 {@code unsupported_android} 明确失败。
     */
    public static Result convertClass(Path archive, String internalName, Path outputJar, List<Path> classpath) throws DexException {
        DexArchive.Inspected inspected = DexArchive.inspect(archive);
        DexToolchain.convertClass(archive, internalName, outputJar, classpath);
        if (!DexArchive.containsClass(outputJar, internalName)) {
            throw new DexException(DexCodes.CONVERSION_FAILED,
                    "惰性转换未产出目标类 " + internalName + "（输入 " + archive.getFileName() + "）。");
        }
        return new Result(inspected.dexFileCount(), inspected.version(), 1);
    }
}
