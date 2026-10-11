package android.os;

/**
 * Android {@code Build} 最小子集（VodBox 自有实现）。
 *
 * 取值是**固定的宿主身份**，不是某个真实 Android 设备——插件若据此分平台，得到的是一致、可预测的结果。
 * {@code Build.VERSION.SDK_INT} 由宿主声明为 {@link VERSION#SDK_INT}（Android 11 / API 30）。
 * 注意：这些字段很多在插件编译期就被内联，本类提供的是运行时读取（如 {@code Build.MANUFACTURER}）的值。
 */
public final class Build {

    public static final String CPU_ABI = "arm64-v8a";
    public static final String CPU_ABI2 = "";
    public static final String[] SUPPORTED_ABIS = { CPU_ABI };
    public static final String[] SUPPORTED_64_BIT_ABIS = { CPU_ABI };
    public static final String[] SUPPORTED_32_BIT_ABIS = {};
    public static final String MANUFACTURER = "VodBox";
    public static final String BRAND = "VodBox";
    public static final String MODEL = "VodBox Desktop";
    public static final String DEVICE = "vodbox";
    public static final String PRODUCT = "vodbox";
    public static final String BOARD = "vodbox";
    public static final String HARDWARE = "unknown";
    public static final String BOOTLOADER = "unknown";
    public static final String RADIO = "unknown";
    public static final String DISPLAY = "VodBox";
    public static final String ID = "VODBOX.1.0.0";
    public static final String TYPE = "user";
    public static final String TAG = "vodbox";
    public static final String USER = "vodbox";
    public static final String HOST = "localhost";
    public static final String FINGERPRINT = "VodBox/vodbox/vodbox:11/VODBOX.1.0.0/1:user/release-keys";
    public static final long TIME = 0L;

    private Build() {
    }

    /** Android 版本字段（本宿主声明为 API 30）。 */
    public static class VERSION {
        public static final int SDK_INT = 30;
        public static final String SDK = String.valueOf(SDK_INT);
        public static final String RELEASE = "11";
        public static final String CODENAME = "REL";
        public static final String INCREMENTAL = "1";
        public static final String BASE_OS = "";
        public static final String SECURITY_PATCH = "2021-01-01";
        public static final String[] ACTIVE_CODENAMES = new String[0];
        public static final int PREVIEW_SDK_INT = 0;

        private VERSION() {
        }
    }

    /** 常用版本号常量，取值与 Android 官方一致。 */
    public static class VERSION_CODES {
        public static final int BASE = 1;
        public static final int BASE_1_1 = 2;
        public static final int CUPCAKE = 3;
        public static final int DONUT = 4;
        public static final int ECLAIR = 5;
        public static final int ECLAIR_0_1 = 6;
        public static final int ECLAIR_MR1 = 7;
        public static final int FROYO = 8;
        public static final int GINGERBREAD = 9;
        public static final int GINGERBREAD_MR1 = 10;
        public static final int HONEYCOMB = 11;
        public static final int HONEYCOMB_MR1 = 12;
        public static final int HONEYCOMB_MR2 = 13;
        public static final int ICE_CREAM_SANDWICH = 14;
        public static final int ICE_CREAM_SANDWICH_MR1 = 15;
        public static final int JELLY_BEAN = 16;
        public static final int JELLY_BEAN_MR1 = 17;
        public static final int JELLY_BEAN_MR2 = 18;
        public static final int KITKAT = 19;
        public static final int KITKAT_WATCH = 20;
        public static final int LOLLIPOP = 21;
        public static final int LOLLIPOP_MR1 = 22;
        public static final int M = 23;
        public static final int N = 24;
        public static final int N_MR1 = 25;
        public static final int O = 26;
        public static final int O_MR1 = 27;
        public static final int P = 28;
        public static final int Q = 29;
        public static final int R = 30;
        public static final int S = 31;
        public static final int S_V2 = 32;
        public static final int TIRAMISU = 33;
        public static final int UPSIDE_DOWN_CAKE = 34;
        public static final int VANILLA_ICE_CREAM = 35;

        private VERSION_CODES() {
        }
    }
}
