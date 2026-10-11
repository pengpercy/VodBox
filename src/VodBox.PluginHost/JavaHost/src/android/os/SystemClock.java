package android.os;

/**
 * Android {@code SystemClock} 最小子集（VodBox 自有实现）。
 *
 * {@link #uptimeMillis()} 与 {@link #elapsedRealtime()} 基于 {@link System#nanoTime()}（单调、不受墙钟调整影响），
 * 二者在本宿主中取值相同（没有 Android 的深度睡眠概念）。{@link #currentThreadTimeMillis()} 用当前线程 CPU 时间。
 */
public final class SystemClock {

    private SystemClock() {
    }

    public static long uptimeMillis() {
        return System.nanoTime() / 1_000_000L;
    }

    public static long elapsedRealtime() {
        return uptimeMillis();
    }

    public static long elapsedRealtimeNanos() {
        return System.nanoTime();
    }

    public static long currentThreadTimeMillis() {
        return currentThreadTimeMicro() / 1000L;
    }

    /** 近似为当前线程的 CPU 时间（Android 返回 worker 线程 CPU 时间）。 */
    public static long currentThreadTimeMicro() {
        ThreadMXBeanShim bean = ThreadMXBeanShim.instance();
        return bean.currentThreadCpuTimeMicros();
    }

    public static boolean setCurrentTimeMillis(long millis) {
        return false;
    }

    public static void sleep(long ms) {
        if (ms < 0) throw new IllegalArgumentException("ms < 0");
        try {
            Thread.sleep(ms);
        } catch (InterruptedException interrupted) {
            // 被中断：恢复中断标志并按 Android 语义提前返回（不清除中断状态）。
            Thread.currentThread().interrupt();
        }
    }

    /** 用 JDK JMX 读取当前线程 CPU 时间；不可用时回退为单调时钟。 */
    private static final class ThreadMXBeanShim {
        private final java.lang.management.ThreadMXBean bean;

        private ThreadMXBeanShim(java.lang.management.ThreadMXBean bean) {
            this.bean = bean;
        }

        static ThreadMXBeanShim instance() {
            try {
                return new ThreadMXBeanShim(java.lang.management.ManagementFactory.getThreadMXBean());
            } catch (Throwable unavailable) {
                return new ThreadMXBeanShim(null);
            }
        }

        long currentThreadCpuTimeMicros() {
            if (bean != null && bean.isCurrentThreadCpuTimeSupported()) {
                long nanos = bean.getCurrentThreadCpuTime();
                if (nanos >= 0) return nanos / 1000L;
            }
            return System.nanoTime() / 1000L;
        }
    }
}
