package dalvik.system;

import android.os.Build;

/**
 * Android {@code dalvik.system.VMRuntime} 最小子集（VodBox 自有实现）。
 *
 * 存在意义：大量 guard 型插件的初始化路径会调用 {@code VMRuntime.getRuntime()} 设置 targetSdkVersion
 * 或放宽 hidden API 限制。这些调用在本 JVM 宿主上**没有真实效果**（这里没有 ART、没有 hidden API 名单），
 * 因此实现为**记录 + 无副作用**，绝不假装「已绕过 ART 限制」。需要真实 ART 行为的插件会在别处明确失败。
 */
public final class VMRuntime {

    private static final VMRuntime INSTANCE = new VMRuntime();

    private volatile int targetSdkVersion = Build.VERSION.SDK_INT;
    private volatile String[] hiddenApiExemptions = new String[0];

    private VMRuntime() {
    }

    public static VMRuntime getRuntime() {
        return INSTANCE;
    }

    public int getTargetSdkVersion() {
        return targetSdkVersion;
    }

    public void setTargetSdkVersion(int value) {
        if (value <= 0) throw new IllegalArgumentException("targetSdkVersion 必须为正");
        targetSdkVersion = value;
    }

    /** Android 返回 {@code "libart.so"}；本宿主保持同一事实字符串。 */
    public String vmLibrary() {
        return "libart.so";
    }

    /** 宿主声明的指令集事实（非真实 ART）。 */
    public String vmInstructionSet() {
        return "arm64";
    }

    public boolean is64Bit() {
        return true;
    }

    public boolean isCheckJniEnabled() {
        return false;
    }

    public String bootClassPath() {
        return System.getProperty("java.class.path", "");
    }

    public String classPath() {
        return System.getProperty("java.class.path", "");
    }

    /** 记录豁免名单但**不改变任何解析行为**（本宿主没有 hidden API 检查）。 */
    public void setHiddenApiExemptions(String[] exemptions) {
        hiddenApiExemptions = exemptions == null ? new String[0] : exemptions.clone();
    }

    public String[] getHiddenApiExemptions() {
        return hiddenApiExemptions.clone();
    }

    public void setHiddenApiAccessLogSamplingRate(int rate) {
        // 无 hidden API 日志，忽略（显式无副作用）。
    }

    public void setHiddenApiAccessLogSampleRate(int rate) {
        // 兼容旧签名，同样无副作用。
    }
}
