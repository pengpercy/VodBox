package android.os;

/**
 * Android {@code android.os.Process} 最小子集（VodBox 自有实现，独立编写）。
 *
 * <ul>
 *   <li>{@link #myPid()} / {@link #myTid()} 是**真实进程/线程号**（插件宿主在独立进程里）。</li>
 *   <li>{@link #is64Bit()} 按当前 JVM 的真实位宽回答。</li>
 *   <li>{@link #myUid()}：桌面进程没有 Android uid，这里返回应用 uid 区间的起始值
 *       （{@link #FIRST_APPLICATION_UID}）——**稳定、且永不为 0（root）或系统 uid**，
 *       不伪造特权身份。</li>
 *   <li>{@link #killProcess(int)} 是**明确拒绝**：会话进程一旦退出，宿主的协议通道就断了，
 *       因此宿主不允许插件自行终止进程（宿主归类为 {@code unsupported_android}）。</li>
 * </ul>
 *
 * 未实现（调用即 NoSuchMethodError）：{@code setThreadPriority}、{@code getThreadPriority}、
 * {@code getUidForPid}、{@code sendSignal}。
 */
public final class Process {

    public static final int SYSTEM_UID = 1000;
    public static final int FIRST_APPLICATION_UID = 10000;
    public static final int LAST_APPLICATION_UID = 19999;

    private Process() {
    }

    public static boolean is64Bit() {
        String model = System.getProperty("sun.arch.data.model");
        if (model != null) return model.contains("64");
        return System.getProperty("os.arch", "").contains("64");
    }

    public static int myPid() {
        return Math.toIntExact(ProcessHandle.current().pid());
    }

    public static int myTid() {
        return (int) Thread.currentThread().getId();
    }

    public static int myUid() {
        return FIRST_APPLICATION_UID;
    }

    /** 明确拒绝：插件不得终止宿主会话进程。 */
    public static void killProcess(int pid) {
        throw new UnsupportedOperationException(
                "android.os.Process.killProcess(" + pid + ")：桌面宿主不允许插件终止会话进程（会断掉协议通道）");
    }
}
