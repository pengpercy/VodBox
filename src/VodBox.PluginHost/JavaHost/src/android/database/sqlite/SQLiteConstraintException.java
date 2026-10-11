package android.database.sqlite;

/**
 * Android {@code SQLiteConstraintException} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：继承 {@link SQLiteException}，表示约束冲突（UNIQUE/NOT NULL/主键等）。
 * 本宿主由真实 SQLite 引擎的约束错误映射而来（见 {@code dev.vodbox.jarhost.android.JdbcSqliteEngine}）。
 */
public class SQLiteConstraintException extends SQLiteException {

    private static final long serialVersionUID = 1L;

    public SQLiteConstraintException() {
        super();
    }

    public SQLiteConstraintException(String message) {
        super(message);
    }

    public SQLiteConstraintException(String message, Throwable cause) {
        super(message, cause);
    }
}
