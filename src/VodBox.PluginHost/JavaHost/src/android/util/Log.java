package android.util;

import java.io.PrintWriter;
import java.io.StringWriter;

/**
 * Android {@code Log} 最小子集（VodBox 自有实现）。
 *
 * 输出走 **stderr**（宿主把 stdout 让给诊断、协议只走文件），格式固定为
 * {@code <级别字母>/<tag>: <msg>}，便于离线断言。级别常量取值与 Android 一致。
 */
public final class Log {

    public static final int VERBOSE = 2;
    public static final int DEBUG = 3;
    public static final int INFO = 4;
    public static final int WARN = 5;
    public static final int ERROR = 6;
    public static final int ASSERT = 7;

    public static final int LOG_ID_MAIN = 0;
    public static final int LOG_ID_RADIO = 1;
    public static final int LOG_ID_EVENTS = 2;
    public static final int LOG_ID_SYSTEM = 3;
    public static final int LOG_ID_CRASH = 4;

    private Log() {}

    public static int v(String tag, String msg) { return println(VERBOSE, tag, msg); }
    public static int v(String tag, String msg, Throwable tr) { return println(VERBOSE, tag, msg + '\n' + getStackTraceString(tr)); }
    public static int d(String tag, String msg) { return println(DEBUG, tag, msg); }
    public static int d(String tag, String msg, Throwable tr) { return println(DEBUG, tag, msg + '\n' + getStackTraceString(tr)); }
    public static int i(String tag, String msg) { return println(INFO, tag, msg); }
    public static int i(String tag, String msg, Throwable tr) { return println(INFO, tag, msg + '\n' + getStackTraceString(tr)); }
    public static int w(String tag, String msg) { return println(WARN, tag, msg); }
    public static int w(String tag, String msg, Throwable tr) { return println(WARN, tag, msg + '\n' + getStackTraceString(tr)); }
    public static int w(String tag, Throwable tr) { return println(WARN, tag, getStackTraceString(tr)); }
    public static int e(String tag, String msg) { return println(ERROR, tag, msg); }
    public static int e(String tag, String msg, Throwable tr) { return println(ERROR, tag, msg + '\n' + getStackTraceString(tr)); }
    public static int wtf(String tag, String msg) { return println(ASSERT, tag, msg); }
    public static int wtf(String tag, String msg, Throwable tr) { return println(ASSERT, tag, msg + '\n' + getStackTraceString(tr)); }
    public static int wtf(String tag, Throwable tr) { return println(ASSERT, tag, getStackTraceString(tr)); }

    /** 本宿主所有级别都输出到 stderr；{@link #VERBOSE} 及以上均视为可记录。 */
    public static boolean isLoggable(String tag, int level) {
        return level >= VERBOSE;
    }

    public static int println(int priority, String tag, String msg) {
        System.err.println(String.valueOf(letter(priority)) + '/' + (tag == null ? "" : tag) + ": " + (msg == null ? "null" : msg));
        return 0;
    }

    public static String getStackTraceString(Throwable tr) {
        if (tr == null) return "";
        StringWriter writer = new StringWriter();
        try (PrintWriter printer = new PrintWriter(writer)) {
            tr.printStackTrace(printer);
        }
        return writer.toString();
    }

    private static char letter(int priority) {
        switch (priority) {
            case VERBOSE: return 'V';
            case DEBUG: return 'D';
            case INFO: return 'I';
            case WARN: return 'W';
            case ERROR: return 'E';
            case ASSERT: return 'A';
            default: return '?';
        }
    }
}
