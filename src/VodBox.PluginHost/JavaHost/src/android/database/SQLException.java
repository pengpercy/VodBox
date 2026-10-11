package android.database;

/**
 * Android {@code android.database.SQLException} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：**非受检**异常；{@code android.database.sqlite.SQLiteException} 继承它。
 */
public class SQLException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    public SQLException() {
        super();
    }

    public SQLException(String message) {
        super(message);
    }

    public SQLException(String message, Throwable cause) {
        super(message, cause);
    }
}
