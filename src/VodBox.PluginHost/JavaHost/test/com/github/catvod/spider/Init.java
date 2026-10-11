package com.github.catvod.spider;

import java.util.ArrayList;
import java.util.List;

/**
 * FTY（饭太硬）bootstrap 调用顺序自测的**假 Init**（VodBox 自有测试桩）。
 *
 * <p>真实 FTY bootstrap 里的 {@code com.github.catvod.spider.Init} 有两个静态入口：
 * {@code init(Context)}（内部调 native {@code DexNative.getLoader} 取回 loader）与
 * {@code getSpider(String)}（内部调 native {@code DexNative.getSpider} 取回解密后的 spider）。
 * 本桩**保持同名**，好让 {@code dev.vodbox.jarhost.Host#ftySpider} 的反射查找命中；它把两步写进
 * {@link #ORDER}，供 {@code dev.vodbox.jarhost.HostFtyBootstrapSelfTest} 断言顺序与失败可诊断。
 *
 * <p>⚠️ 仅用于离线自测（{@code JavaHost/test.sh}），**不随宿主打包**、不被真实插件加载；这里没有任何
 * 解密/原生逻辑，也不联网。参数取 {@code Object}（反射按 名称+元数 匹配，无需真实 Context）。
 */
public final class Init {

    /** 依次记录被调到的方法名（{@code init}/{@code getSpider}）。 */
    public static final List<String> ORDER = new ArrayList<>();

    /** {@code getSpider} 的返回值（由自测设置）。 */
    public static Object spider;

    /** 非空时 {@code init} 抛出它（自测用，验证失败可诊断）。 */
    public static RuntimeException toThrowOnInit;

    /** 最后一次 {@code getSpider} 收到的 spider 类名（自测用）。 */
    public static String lastSpiderName;

    private Init() {}

    public static void init(Object context) {
        ORDER.add("init");
        if (toThrowOnInit != null) throw toThrowOnInit;
    }

    public static Object getSpider(String name) {
        ORDER.add("getSpider");
        lastSpiderName = name;
        return spider;
    }
}
