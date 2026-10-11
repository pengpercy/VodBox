package android.database.sqlite;

/**
 * Android {@code SQLiteException} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：继承 {@link android.database.SQLException}，表示 SQL 层面的失败
 * （表不存在、语法错误等）——这是**插件自身的错误**，不是「宿主未实现的能力」。
 */
public class SQLiteException extends android.database.SQLException {

    private static final long serialVersionUID = 1L;

    public SQLiteException() {
        super();
    }

    public SQLiteException(String message) {
        super(message);
    }

    public SQLiteException(String message, Throwable cause) {
        super(message, cause);
    }
}
