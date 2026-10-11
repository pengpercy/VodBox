package dev.vodbox.jarhost;

import com.github.catvod.spider.FtyOrderSpider;
import com.github.catvod.spider.Init;

import java.util.ArrayList;
import java.util.List;

/**
 * {@link Host} 的 **FTY（饭太硬）bootstrap 调用顺序**离线自测（VodBox 自有实现）。
 *
 * <p>用假 {@code com.github.catvod.spider.Init}（见 test 树里的 {@code com/github/catvod/spider/Init.java}）
 * 驱动 {@code Host#ftySpider}，验证它按真实链路调用：
 * <ol>
 *   <li>{@code Init.init(...)}（对应真实链路里的 loader：native {@code DexNative.getLoader}）**先于**</li>
 *   <li>{@code Init.getSpider(...)}（对应 native {@code DexNative.getSpider}）；</li>
 * </ol>
 * 并验证任一步失败都**可诊断**（带步骤名抛出、保留原因），绝不静默返回半成品；缺少 Init 时明确失败。
 *
 * <p><b>诚实边界</b>：本自测不启动 JVM 内的 ARM64 模拟、不加载任何 guard、不触碰真实 FTY 样本——它只
 * 校验 {@link Host} 的**调用顺序与失败分类**这一层接线；真正跑 guest 代码的链路见
 * {@code dev.vodbox.jarhost.guard.DynarmicGuardBackend}（需 unidbg + Dynarmic，另由操作方配置）。
 *
 * 用法：{@code java -cp <classes> dev.vodbox.jarhost.HostFtyBootstrapSelfTest}
 * 退出码：0 全过；1 有断言失败。
 */
public final class HostFtyBootstrapSelfTest {
    /** 假 spider 的类名（作为 {@code Init.getSpider} 的入参）。 */
    private static final String SPIDER = "com.github.catvod.spider.FtyOrderSpider";

    private static int passed;
    private static final List<String> failures = new ArrayList<>();

    private HostFtyBootstrapSelfTest() {}

    public static void main(String[] args) throws Exception {
        ClassLoader loader = HostFtyBootstrapSelfTest.class.getClassLoader();

        // 1) 正常顺序：Init.init（loader）→ Init.getSpider。
        Init.ORDER.clear();
        Init.toThrowOnInit = null;
        FtyOrderSpider spider = new FtyOrderSpider();
        Init.spider = spider;
        Object resolved = Host.ftySpider(loader, "context", SPIDER);
        check("FTY：调用顺序为 Init.init 先于 Init.getSpider", Init.ORDER.equals(List.of("init", "getSpider")));
        check("FTY：Init.getSpider 收到 spider 类名", SPIDER.equals(Init.lastSpiderName));
        check("FTY：返回解密后的 spider 实例", resolved == spider);

        // 2) Init.init 失败：可诊断（带步骤名抛出 + 保留原因），不静默返回。
        Init.ORDER.clear();
        Init.toThrowOnInit = new IllegalStateException("no guard backend");
        Throwable initFailure = thrown(() -> Host.ftySpider(loader, "context", SPIDER));
        check("FTY：Init.init 失败抛异常（不静默）", initFailure != null);
        check("FTY：Init.init 失败带步骤名", initFailure != null
                && initFailure.getMessage() != null && initFailure.getMessage().contains("loader"));
        check("FTY：Init.init 失败保留原因", initFailure != null && initFailure.getCause() instanceof IllegalStateException);
        check("FTY：Init.init 失败后不再取 spider", Init.ORDER.equals(List.of("init")));
        Init.toThrowOnInit = null;

        // 3) getSpider 返回空：明确失败，不把半成品当作可用。
        Init.ORDER.clear();
        Init.spider = null;
        Throwable emptyFailure = thrown(() -> Host.ftySpider(loader, "context", SPIDER));
        check("FTY：getSpider 返回空明确失败", emptyFailure != null
                && emptyFailure.getMessage() != null && emptyFailure.getMessage().contains("getSpider"));
        Init.spider = new FtyOrderSpider();

        // 4) 缺少 Init：明确失败（对应 plugin_class_missing 路径）。
        Throwable missingFailure = thrown(() -> Host.ftySpider(new ClassLoader(null) {}, "context", SPIDER));
        check("FTY：缺少 Init 明确失败", missingFailure != null
                && missingFailure.getMessage() != null && missingFailure.getMessage().contains("Init"));

        System.out.println("HostFtyBootstrapSelfTest: " + passed + " 项通过, " + failures.size() + " 项失败");
        for (String failure : failures) System.out.println("  FAIL " + failure);
        if (!failures.isEmpty()) System.exit(1);
    }

    /** 允许抛受检异常的 lambda（Host#ftySpider 声明 throws Exception）。 */
    @FunctionalInterface
    private interface Attempt {
        Object run() throws Exception;
    }

    /** 执行并把抛出的异常返回（未抛返回 null）。 */
    private static Throwable thrown(Attempt attempt) {
        try {
            attempt.run();
            return null;
        } catch (Throwable error) {
            return error;
        }
    }

    private static void check(String name, boolean condition) {
        if (condition) {
            passed++;
            System.out.println("  ok   " + name);
        } else {
            failures.add(name);
        }
    }
}
