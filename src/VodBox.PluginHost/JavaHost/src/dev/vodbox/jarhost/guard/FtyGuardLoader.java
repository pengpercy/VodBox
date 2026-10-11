package dev.vodbox.jarhost.guard;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/**
 * FTY（饭太硬）**加密 loader** 的真实流程定位与可行链路（本仓库自有实现）。
 *
 * <h2>真实流程（据归档**内容**定位，不停留在库名/文件名猜测）</h2>
 * 对真实样本（饭太硬订阅的全局 <b>spider</b>，URL 以 {@code .jpg} 结尾、响应体实为 ZIP）逐字节核对后的可观测事实：
 * <ol>
 *   <li>{@code assets/ftyguard_v8.so}（另有 {@code _v7} 为 32 位，本宿主只用 arm64）——真正的 guard 原生库；</li>
 *   <li>{@code assets/ftyshinidie.guard}（约 1.1 MB）——**加密载荷**，真正的 spider 类不在 {@code classes.dex} 里；</li>
 *   <li>{@code classes.dex}（数十 KB）——**bootstrap**：包含 {@code com.github.catvod.spider.Init}、{@code DexNative}、
 *       {@code BaseSpiderGuard} 与每个 {@code *Guard} 的壳类；引用 {@code dalvik/system/DexClassLoader} 与
 *       {@code java/lang/System}；</li>
 *   <li>{@code com.github.catvod.spider.DexNative} 声明一组 <b>static native</b> 方法（真实样本里为
 *       {@code decrypt}/{@code encrypt}/{@code getLoader}/{@code getSpider}/{@code calcResult}/{@code noxSign}/
 *       {@code native_ting_md5}/{@code proxyInvoke}）——它们是 bootstrap 与加密 spider 之间的唯一桥。</li>
 * </ol>
 *
 * <h2>可行链路（本宿主接上的部分）</h2>
 * <ol>
 *   <li>装载 arm64 guard 到**同进程模拟器**并执行 {@code JNI_OnLoad}（含 {@code RegisterNatives}）；</li>
 *   <li>{@link NativeBridgingClassLoader} 把 {@code DexNative} 的 native 方法去 native 化，静态/实例、多类型参数/返回、
 *       数组全部经 {@link NativeMethodBridge} 转发给模拟器；</li>
 *   <li>bootstrap 调 {@code DexNative.decrypt/getLoader/getSpider} 取回**解密后的类**；产物是原生 {@code .dex}，
 *       经 {@link NativeMethodBridge#materialize}（宿主 DEX→JVM 能力）转成 JVM 产物后再由类加载器读取。</li>
 * </ol>
 *
 * <p><b>诚实边界</b>：本类只做「按内容定位 + 描述链路 + 校验 arm64 guard 是否存在」；它**不**伪报解密成功。
 * 没有可用的模拟后端时，第 1 步就以 {@code unsupported_native_library} 明确失败；解密产物未转换时，
 * 第 3 步的类加载仍按「未转换即拒绝」处理。
 */
public final class FtyGuardLoader {
    /** FTY bootstrap 里承载 native 成员、用作桥接锚点的类（DVM 点分名）。 */
    public static final String BRIDGE_CLASS = "com.github.catvod.spider.DexNative";
    /** bootstrap 里判定 FTY 形态的类引用（dex 字符串表内的内部斜杠名）。 */
    private static final String BRIDGE_CLASS_INTERNAL = "com/github/catvod/spider/DexNative";
    /** 加密载荷的后缀（真实样本为 {@code .guard}）。 */
    private static final String PAYLOAD_SUFFIX = ".guard";

    private FtyGuardLoader() {}

    /** 一次内容定位的结果（全部来自归档字节，不来自文件名/扩展名猜测）。 */
    public record Layout(
            boolean detected,
            String guardEntry,
            String payloadEntry,
            String bootstrapDex,
            String bridgeClass,
            List<String> dexEntries) {

        /** 是否命中 FTY 加密 loader 的真实形态。 */
        public boolean isFty() {
            return detected;
        }

        public String describe() {
            if (!detected) return "非 FTY 形态（未在归档内容里发现 DexNative + arm64 guard + .guard 载荷）";
            return "FTY 加密 loader：guard=" + guardEntry + "，载荷=" + payloadEntry
                    + "，bootstrap=" + bootstrapDex + "，桥接类=" + bridgeClass;
        }
    }

    /** 按**内容**定位归档是否为 FTY 形态。归档不可读时返回 detected=false（不抛）。 */
    public static Layout inspect(Path archive) {
        if (archive == null || !Files.isRegularFile(archive)) return empty();
        String guard = null;
        String payload = null;
        List<String> dexEntries = new ArrayList<>();
        Map<String, byte[]> dexBytes = new LinkedHashMap<>();
        try (ZipFile zip = new ZipFile(archive.toFile())) {
            var entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                if (entry.isDirectory()) continue;
                String name = entry.getName();
                if (isArm64GuardEntry(name)) {
                    // 优先 FTY 自有 guard（ftyguard_*），否则取第一个 arm64 guard。
                    if (guard == null || (isFtyGuardName(name) && !isFtyGuardName(guard))) guard = name;
                    continue;
                }
                if (name.startsWith("assets/") && name.endsWith(PAYLOAD_SUFFIX)) {
                    if (payload == null) payload = name;
                    continue;
                }
                if (isDexEntry(name)) {
                    dexEntries.add(name);
                    try (InputStream input = zip.getInputStream(entry)) {
                        // 只读前若干 MB，避免把超大 dex 全读进内存；DexNative 引用出现在字符串表里，位置不定。
                        dexBytes.put(name, input.readNBytes(8 * 1024 * 1024));
                    }
                }
            }
        } catch (IOException unreadable) {
            return empty();
        }
        String bootstrap = null;
        for (Map.Entry<String, byte[]> candidate : dexBytes.entrySet()) {
            if (containsAscii(candidate.getValue(), BRIDGE_CLASS_INTERNAL)) {
                bootstrap = candidate.getKey();
                break;
            }
        }
        boolean detected = guard != null && payload != null && bootstrap != null;
        return new Layout(detected, guard, payload, bootstrap, detected ? BRIDGE_CLASS : null, dexEntries);
    }

    /** 简版判定：归档内容是否为 FTY 加密 loader 形态。 */
    public static boolean isFtyContainer(Path archive) {
        return inspect(archive).detected();
    }

    /** 链路的可读步骤（诊断/验收输出用，不含任何「已成功」的断言）。 */
    public static List<String> chain(Layout layout) {
        List<String> steps = new ArrayList<>();
        Layout current = layout == null ? empty() : layout;
        steps.add("1. 按内容定位：" + current.describe());
        steps.add("2. 装载 " + (current.guardEntry() == null ? "(无)" : current.guardEntry()) + " 到同进程模拟器并执行 JNI_OnLoad（含 RegisterNatives）");
        steps.add("3. 桥接 " + BRIDGE_CLASS + " 的 static native 方法（decrypt/getLoader/getSpider 等）到模拟器");
        steps.add("4. bootstrap 取回解密类（原生 .dex）后，经 DEX→JVM 物化再由类加载器读取；未转换即明确拒绝");
        return steps;
    }

    private static boolean isArm64GuardEntry(String name) {
        if (!name.startsWith("assets/")) return false;
        String leaf = name.substring("assets/".length());
        if (leaf.isEmpty() || leaf.indexOf('/') >= 0) return false;
        return leaf.endsWith("-v8.so") || leaf.endsWith("_v8.so");
    }

    private static boolean isFtyGuardName(String entry) {
        String leaf = entry.substring(entry.lastIndexOf('/') + 1);
        return leaf.toLowerCase().startsWith("ftyguard");
    }

    private static boolean isDexEntry(String name) {
        return name.endsWith(".dex");
    }

    private static boolean containsAscii(byte[] haystack, String needle) {
        byte[] pattern = needle.getBytes(StandardCharsets.US_ASCII);
        outer:
        for (int i = 0; i + pattern.length <= haystack.length; i++) {
            for (int j = 0; j < pattern.length; j++) {
                if (haystack[i + j] != pattern[j]) continue outer;
            }
            return true;
        }
        return false;
    }

    private static Layout empty() {
        return new Layout(false, null, null, null, null, List.of());
    }
}
