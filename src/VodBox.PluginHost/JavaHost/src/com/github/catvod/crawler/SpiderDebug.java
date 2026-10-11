package com.github.catvod.crawler;

/**
 * spider 的诊断日志门面——VodBox 的独立实现，非上游源码复制。
 *
 * <p>按**真实引用**核对：真实 FTY/TVBox 的 {@code com.github.catvod.crawler.SpiderDebug} 只暴露
 * {@link #log(String)} 与 {@link #log(Throwable)} 两个静态入口，插件用它们输出诊断。故本类只声明这两者，
 * 不额外发明 API。
 *
 * <p>输出到 {@code System.err} 并带 {@code [Spider]} 前缀（与真实引用的可观测行为一致），
 * 不依赖任何 Android 类，可在宿主 {@code host.jar} 内直接编译/运行。
 */
public final class SpiderDebug {

    private SpiderDebug() {
    }

    /** 输出一行 spider 诊断。{@code text} 为 {@code null} 时按空串处理（不吞调用、不抛）。 */
    public static void log(String text) {
        System.err.println("[Spider] " + (text == null ? "" : text));
    }

    /** 输出异常堆栈。{@code error} 为 {@code null} 时按「未知错误」记录，避免门面自身抛异常。 */
    public static void log(Throwable error) {
        if (error == null) {
            log("unknown error");
            return;
        }
        error.printStackTrace(System.err);
    }
}
