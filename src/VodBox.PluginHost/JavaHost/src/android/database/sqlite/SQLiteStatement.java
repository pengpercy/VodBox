package android.database.sqlite;

/**
 * Android {@code SQLiteStatement} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 真实语义：把 {@link #execute()} / {@link #executeInsert()} / {@link #executeUpdateDelete()} /
 * {@link #simpleQueryForLong()} / {@link #simpleQueryForString()} 交给真实 SQLite 引擎执行
 * （见 {@code dev.vodbox.jarhost.android.JdbcSqliteEngine}）。绑定值来自 {@link SQLiteProgram}。
 *
 * 未实现的 Android 成员（调用即明确失败，不伪装）：{@code simpleQueryForBlobFileDescriptor}。
 */
public final class SQLiteStatement extends SQLiteProgram {

    SQLiteStatement(SQLiteDatabase database, String sql) {
        super(database, sql);
    }

    /** 执行任意语句（DDL/DML），丢弃结果集。 */
    public void execute() {
        getDatabase().engine().execute(getSql(), boundArguments());
    }

    /** Android 契约：INSERT 返回 rowid；非 INSERT 时等价于 {@link #execute()} 并返回 -1。 */
    public long executeInsert() {
        if (!isInsertStatement()) {
            execute();
            return -1;
        }
        return getDatabase().engine().insert(getSql(), boundArguments());
    }

    /** Android 契约：返回受影响行数；非 DML 时等价于 {@link #execute()} 并返回 0。 */
    public int executeUpdateDelete() {
        if (isQueryStatement()) {
            execute();
            return 0;
        }
        return getDatabase().engine().update(getSql(), boundArguments());
    }

    public long simpleQueryForLong() {
        return getDatabase().engine().scalarLong(getSql(), boundArguments());
    }

    public String simpleQueryForString() {
        return getDatabase().engine().scalarString(getSql(), boundArguments());
    }

    private boolean isInsertStatement() {
        String trimmed = getSql().trim().toLowerCase(java.util.Locale.ROOT);
        return trimmed.startsWith("insert") || trimmed.startsWith("replace");
    }

    private boolean isQueryStatement() {
        String trimmed = getSql().trim().toLowerCase(java.util.Locale.ROOT);
        return trimmed.startsWith("select") || trimmed.startsWith("pragma");
    }

    @Override
    public String toString() {
        return "SQLiteStatement: " + getSql();
    }
}
