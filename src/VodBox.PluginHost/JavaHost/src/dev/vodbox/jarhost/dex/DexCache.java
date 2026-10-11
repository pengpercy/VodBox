package dev.vodbox.jarhost.dex;

import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.channels.OverlappingFileLockException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Comparator;
import java.util.List;
import java.util.stream.Stream;

/**
 * DEX 转换产物的磁盘缓存：键 = <b>输入内容哈希</b> + <b>宿主版本</b>。
 *
 * <ul>
 *   <li><b>内容哈希</b>：对归档字节做 SHA-256，同一地址换内容会重新转换，避免张冠李戴；</li>
 *   <li><b>宿主版本</b>：宿主升级（转换器/协议变化）后旧产物作废；</li>
 *   <li><b>锁 + 原子提交</b>：同一 key 的并发转换由跨进程文件锁串行化；产物先写临时文件、
 *       校验通过后再原子改名，并写一份提交标记；缺标记或产物不可读一律视为未命中；</li>
 *   <li><b>损坏回退</b>：缓存项损坏（jar 非 zip / 无 .class / 缺标记）时删除并重新转换，
 *       而不是把坏产物交给类加载器。</li>
 * </ul>
 *
 * 这是宿主侧的自足缓存（供 {@code --serve} 直接吃 DEX 归档时使用）；桌面端另有一层同语义的
 * 缓存（见 C# {@code JarPluginConversionPipeline}），两层键规则一致。
 */
public final class DexCache {
    private final Path root;
    private final String hostVersion;
    private final long lockTimeoutMillis;

    public DexCache(Path root, String hostVersion, long lockTimeoutMillis) {
        this.root = root;
        this.hostVersion = hostVersion == null || hostVersion.isBlank() ? "unknown" : hostVersion;
        this.lockTimeoutMillis = lockTimeoutMillis <= 0 ? 60_000 : lockTimeoutMillis;
    }

    /**
     * 返回归档对应的可加载 JAR；必要时**整包**转换并落盘。
     * 键 = 输入内容哈希 + 宿主版本，与桌面端 {@code JarPluginConversionPipeline} 完全一致
     * （两层可在同一输入上互相命中）。
     */
    public Path resolve(Path archive, List<Path> classpath) throws DexException {
        return resolveArtifact(archive, "",
                target -> DexConverter.convert(archive, target, classpath),
                DexArchive::isLoadableJar);
    }

    /**
     * **惰性路径**：只转换并缓存**单个类**（键 = 输入内容哈希 + 宿主版本 + 类名）。
     *
     * <p>与整包转换共享同一套纪律：跨进程文件锁串行化、产物先写临时文件再原子改名、写提交标记、
     * 缺标记或产物不含该类一律视为未命中并重转。未命中且转换失败时抛 {@link DexException}，
     * 且**不把半成品**留给类加载器。
     *
     * @param className 全限定类名（点分隔）
     */
    public Path resolveClass(Path archive, String className, List<Path> classpath) throws DexException {
        String internal = className.replace('.', '/');
        return resolveArtifact(archive, "class-" + internal,
                target -> DexConverter.convertClass(archive, internal, target, classpath),
                target -> DexArchive.containsClass(target, internal));
    }

    /**
     * 中断安全外壳（上游同款语义，独立实现）：插件会**中断自己的 worker 线程**（超时/取消的 future），
     * 而 {@link java.nio.channels.FileChannel} 与 NIO 写入是可中断的——一次被中断的转换会被 JVM 缓存成
     * {@code NoClassDefFoundError} 直到重启。这里清零中断标志后重试（至多 3 次），结束后恢复标志。
     */
    public Path resolveClassSafely(Path archive, String className, List<Path> classpath) throws DexException {
        boolean interrupted = Thread.interrupted();
        try {
            for (int attempt = 1; ; attempt++) {
                try {
                    return resolveClass(archive, className, classpath);
                } catch (DexException error) {
                    if (attempt >= 3 || !interruptedByIo(error)) throw error;
                    interrupted = true;
                    Thread.interrupted();
                }
            }
        } finally {
            if (interrupted) Thread.currentThread().interrupt();
        }
    }

    private static boolean interruptedByIo(Throwable error) {
        for (Throwable cause = error; cause != null; cause = cause.getCause()) {
            if (cause instanceof java.nio.channels.ClosedByInterruptException
                    || cause instanceof java.nio.channels.FileLockInterruptionException) {
                return true;
            }
        }
        return false;
    }

    /** 转换步骤：把产物写到 {@code target}（由调用方保证 target 在数据目录内）。 */
    private interface ConvertStep {
        DexConverter.Result run(Path target) throws DexException;
    }

    /** 产物有效性判定：整包要求可加载 JAR；单类要求含该类。 */
    private interface Validator {
        boolean test(Path target);
    }

    /** 内容寻址缓存 + 跨进程锁 + 原子提交的公共骨架，{@code scope} 区分整包与单类。 */
    private Path resolveArtifact(Path archive, String scope, ConvertStep step, Validator valid) throws DexException {
        String hash = sha256(archive);
        // scope 为空时退化为「内容哈希 + 宿主版本」，与桌面端缓存键逐字一致。
        String key = hash + "-" + sanitize(hostVersion) + (scope.isEmpty() ? "" : "-" + sanitize(scope));
        Path jars = root.resolve("jars");
        Path markers = root.resolve("markers");
        Path locks = root.resolve("locks");
        Path target = jars.resolve(key + ".jar");
        Path marker = markers.resolve(key + ".json");
        try {
            Files.createDirectories(jars);
            Files.createDirectories(markers);
            Files.createDirectories(locks);
        } catch (IOException error) {
            throw new DexException(DexCodes.CONVERSION_FAILED, "无法创建 DEX 缓存目录：" + error, error);
        }
        if (hit(target, marker, hash, valid)) return target;
        try (LockHandle lock = acquire(locks.resolve(key + ".lock"))) {
            if (hit(target, marker, hash, valid)) return target;
            // 损坏回退：清掉不可信产物后重转。
            deleteQuietly(target);
            deleteQuietly(marker);
            Path temporary = jars.resolve(key + "." + Long.toHexString(System.nanoTime()) + ".tmp");
            try {
                DexConverter.Result result = step.run(temporary);
                if (!valid.test(temporary)) {
                    throw new DexException(DexCodes.CONVERSION_FAILED, "转换产物不可用，已丢弃。");
                }
                moveAtomically(temporary, target);
                writeMarker(marker, hash, hostVersion, result);
            } finally {
                deleteQuietly(temporary);
            }
            return target;
        }
    }

    /** 缓存命中判定：产物通过 {@code valid}，且提交标记里的内容哈希与当前一致。 */
    private boolean hit(Path jar, Path marker, String hash, Validator valid) {
        if (!Files.isRegularFile(jar) || !Files.isRegularFile(marker)) return false;
        if (!valid.test(jar)) return false;
        try {
            String text = Files.readString(marker, StandardCharsets.UTF_8);
            return text.contains("\"contentHash\":\"" + hash + "\"");
        } catch (IOException error) {
            return false;
        }
    }

    private LockHandle acquire(Path lockFile) throws DexException {
        long deadline = System.currentTimeMillis() + lockTimeoutMillis;
        while (true) {
            try {
                RandomAccessFile handle = new RandomAccessFile(lockFile.toFile(), "rw");
                FileChannel channel = handle.getChannel();
                FileLock lock;
                try {
                    lock = channel.tryLock();
                } catch (OverlappingFileLockException overlap) {
                    lock = null; // 同 JVM 内已有线程持锁。
                } catch (IOException error) {
                    handle.close();
                    throw error;
                }
                if (lock != null) return new LockHandle(handle, channel, lock);
                handle.close();
            } catch (IOException error) {
                throw new DexException(DexCodes.CONVERSION_FAILED, "无法获取 DEX 转换锁：" + error, error);
            }
            if (System.currentTimeMillis() >= deadline) {
                throw new DexException(DexCodes.LOCKED, "等待 DEX 转换锁超时（可能有其它会话正在转换同一插件）。");
            }
            try {
                Thread.sleep(50);
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
                throw new DexException(DexCodes.LOCKED, "等待 DEX 转换锁被中断。");
            }
        }
    }

    private static void moveAtomically(Path from, Path to) throws DexException {
        try {
            Files.move(from, to, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException atomicUnsupported) {
            try {
                Files.move(from, to, StandardCopyOption.REPLACE_EXISTING);
            } catch (IOException error) {
                throw new DexException(DexCodes.CONVERSION_FAILED, "无法提交 DEX 转换产物：" + error, error);
            }
        }
    }

    private static void writeMarker(Path marker, String hash, String hostVersion, DexConverter.Result result) throws DexException {
        String json = "{\"contentHash\":\"" + hash + "\",\"hostVersion\":\"" + hostVersion + "\","
                + "\"dexFiles\":" + result.dexFileCount() + ",\"dexVersion\":\"" + result.dexVersion() + "\","
                + "\"classCount\":" + result.classCount() + "}";
        try {
            Files.writeString(marker, json, StandardCharsets.UTF_8);
        } catch (IOException error) {
            throw new DexException(DexCodes.CONVERSION_FAILED, "无法写入 DEX 缓存标记：" + error, error);
        }
    }

    private static void deleteQuietly(Path path) {
        if (path == null) return;
        try {
            Files.deleteIfExists(path);
        } catch (IOException ignored) {
            // 缓存清理失败不影响正确性。
        }
    }

    private static String sanitize(String value) {
        StringBuilder builder = new StringBuilder(value.length());
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            builder.append(Character.isLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
        }
        return builder.toString();
    }

    /** 对文件内容做 SHA-256（流式，避免整文件读入内存）。宿主也用它推导自身版本。 */
    public static String sha256(Path file) throws DexException {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            try (var input = Files.newInputStream(file)) {
                byte[] buffer = new byte[81920];
                int count;
                while ((count = input.read(buffer)) > 0) digest.update(buffer, 0, count);
            }
            return hex(digest.digest());
        } catch (IOException error) {
            throw new DexException(DexCodes.CORRUPT, "无法读取归档以计算哈希：" + error, error);
        } catch (NoSuchAlgorithmException impossible) {
            throw new DexException(DexCodes.CONVERSION_FAILED, "当前 JVM 不支持 SHA-256。", impossible);
        }
    }

    private static String hex(byte[] bytes) {
        StringBuilder builder = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) builder.append(Character.forDigit((value >> 4) & 0xF, 16)).append(Character.forDigit(value & 0xF, 16));
        return builder.toString();
    }

    /** 清理超过 {@code maxAgeMillis} 的临时残留（进程被杀留下的 .tmp）。 */
    public void sweepTemporaries(long maxAgeMillis) {
        Path jars = root.resolve("jars");
        if (!Files.isDirectory(jars)) return;
        long cutoff = System.currentTimeMillis() - Math.max(0, maxAgeMillis);
        try (Stream<Path> stream = Files.list(jars)) {
            stream.filter(path -> path.getFileName().toString().endsWith(".tmp"))
                    .filter(path -> {
                        try {
                            return Files.getLastModifiedTime(path).toMillis() < cutoff;
                        } catch (IOException error) {
                            return true;
                        }
                    })
                    .sorted(Comparator.naturalOrder())
                    .forEach(DexCache::deleteQuietly);
        } catch (IOException ignored) {
            // 清理失败不影响正确性。
        }
    }

    /** try-with-resources 用的锁句柄，关闭时释放锁与文件句柄。 */
    private static final class LockHandle implements AutoCloseable {
        private final RandomAccessFile handle;
        private final FileChannel channel;
        private final FileLock lock;

        LockHandle(RandomAccessFile handle, FileChannel channel, FileLock lock) {
            this.handle = handle;
            this.channel = channel;
            this.lock = lock;
        }

        @Override
        public void close() {
            try {
                lock.release();
            } catch (IOException ignored) {
                // 已释放。
            }
            try {
                channel.close();
            } catch (IOException ignored) {
                // 已关闭。
            }
            try {
                handle.close();
            } catch (IOException ignored) {
                // 已关闭。
            }
        }
    }
}
