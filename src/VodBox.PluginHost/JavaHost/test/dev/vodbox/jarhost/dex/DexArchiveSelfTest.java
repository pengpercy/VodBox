package dev.vodbox.jarhost.dex;

import java.nio.file.Path;
import java.util.List;

/**
 * {@link DexArchive} 结构解析的离线自测（**需 JDK、不触网、不启动转换引擎**）。
 *
 * <p>用仓库里**真 DEX** fixture（{@code build/dex_fixture.py} 生成、提交在
 * {@code tests/VodBox.Tests/fixtures/dex/}）驱动，覆盖：多 DEX 入口顺序、DEX 头逐字段校验、
 * 声明类计数，以及 {@link DexArchive#danglingTypes} 的 string/type/class 三表解析。
 *
 * <p>不做的事：不调用 dex2jar（那是 {@code build/dex-acceptance.sh} 的真实转换验收），
 * 所以本自测只说明「结构解析对」，不说明「转换成功」。
 *
 * 用法：{@code java -cp <classes> dev.vodbox.jarhost.dex.DexArchiveSelfTest <multidex.zip>}
 * 退出码：0 全过；1 有断言失败。
 */
public final class DexArchiveSelfTest {
    private static int checks;
    private static int failures;

    private DexArchiveSelfTest() {}

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("用法：DexArchiveSelfTest <multidex.zip>");
            System.exit(2);
        }
        Path archive = Path.of(args[0]);

        List<String> entries = DexArchive.dexEntries(archive);
        expect("多 DEX 入口按 Android 约定排序", List.of("classes.dex", "classes2.dex").equals(entries), entries.toString());

        DexArchive.Inspected inspected = DexArchive.inspect(archive);
        expect("DEX 文件数", inspected.dexFileCount() == 2, String.valueOf(inspected.dexFileCount()));
        expect("DEX 版本 035", "035".equals(inspected.version()), inspected.version());
        expect("声明类计数 = 2（每 DEX 一个类）", inspected.declaredClassCount() == 2, String.valueOf(inspected.declaredClassCount()));

        DexArchive.Header first = DexArchive.readHeader(archive, "classes.dex");
        expect("classes.dex 声明 1 个类", first.classCount() == 1, String.valueOf(first.classCount()));

        // 引用但未定义的类：fixture 只引用 java/lang/Object 与 java/lang/String（平台前缀），故应为空。
        List<String> dangling = DexArchive.danglingTypes(archive);
        expect("danglingTypes 已滤掉平台前缀（应为空）", dangling.isEmpty(), dangling.toString());

        // 产物类校验：用 fixture 里的 DEX 自身当作 zip 显然不含 .class，必须为 false（不误判）。
        expect("containsClass 对非转换产物返回 false", !DexArchive.containsClass(archive, "dev/vodbox/fixture/Sample"), "false");

        System.out.println("DexArchiveSelfTest: " + checks + " 项断言，" + failures + " 项失败。");
        System.exit(failures == 0 ? 0 : 1);
    }

    private static void expect(String name, boolean condition, String detail) {
        checks++;
        if (condition) {
            System.out.println("  ok   " + name);
        } else {
            failures++;
            System.out.println("  FAIL " + name + "（实际 " + detail + "）");
        }
    }
}
