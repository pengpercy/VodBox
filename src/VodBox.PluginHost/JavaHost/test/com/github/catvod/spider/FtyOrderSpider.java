package com.github.catvod.spider;

/**
 * {@code HostFtyBootstrapSelfTest} 用的假 spider（VodBox 自有测试桩）：
 * 代表「FTY guard 解密后由 {@code Init.getSpider} 返回」的 spider 实例。
 * 仅用于离线自测，不随宿主打包。
 */
public final class FtyOrderSpider {

    public String init(Object context, String extend) {
        return "inited";
    }

    public String homeContent(boolean filter) {
        return "{}";
    }
}
