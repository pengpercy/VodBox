package android.database.sqlite;

/**
 * Android {@code SQLiteQuery} 最小子集（VodBox 自有实现）。
 *
 * 只承载「一条已编译的查询」这一事实：SQL 文本 + 绑定值（绑定语义见 {@link SQLiteProgram}）。
 * 它作为 {@link SQLiteDatabase.CursorFactory#newCursor} 的参数出现，插件据此替换游标实现。
 * 未实现的 Android 内部成员（{@code fillWindow}）不做伪装。
 */
public class SQLiteQuery extends SQLiteProgram {

    SQLiteQuery(SQLiteDatabase database, String sql) {
        super(database, sql);
    }

    @Override
    public String toString() {
        return getSql();
    }
}
