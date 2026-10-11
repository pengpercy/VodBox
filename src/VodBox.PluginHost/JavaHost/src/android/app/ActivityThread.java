package android.app;

/**
 * Android {@code ActivityThread} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 只保留插件常见的「拿到 Application 实例」这条路径：{@link #currentActivityThread()} 单例 +
 * {@link #currentApplication()}/{@link #getApplication()}。返回的**就是宿主在插件 {@code init}
 * 前装配好的那个 {@code Application}**（见 {@link Application#attachBaseContext}），不是空壳。
 *
 * 宿主不模拟 Android 的 Activity/UI 线程模型，因此 {@code getSystemContext}、
 * {@code currentActivityThread()} 之外的进程/消息队列成员一律不声明（调用即 NoSuchMethodError，
 * 归类为 {@code unsupported_android}）。
 */
public final class ActivityThread {

    private static volatile ActivityThread current;
    private static volatile Application application;

    private ActivityThread() {
    }

    public static ActivityThread currentActivityThread() {
        ActivityThread value = current;
        if (value == null) {
            synchronized (ActivityThread.class) {
                if (current == null) current = new ActivityThread();
                value = current;
            }
        }
        return value;
    }

    /** 未装配过 {@code Application} 时返回 {@code null}（宿主装配前的可观测事实）。 */
    public static Application currentApplication() {
        return application;
    }

    public Application getApplication() {
        return application;
    }

    /** 宿主装配入口（**非** Android API）：{@link Application#attachBaseContext} 会调用它。 */
    public static void attach(Application value) {
        application = value;
    }
}
