package android.database.sqlite;

import java.util.ArrayList;
import java.util.List;

/**
 * Android {@code SQLiteProgram} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 真实的绑定语义：按 1 基序号保存绑定值，{@link #bindAllArgsAsStrings(String[])} 从序号 1 起覆盖；
 * 绑定值最终交给 {@code dev.vodbox.jarhost.android.JdbcSqliteEngine} 以 JDBC 参数写入。
 */
public abstract class SQLiteProgram extends SQLiteClosable {

    private final SQLiteDatabase database;
    private final String sql;
    private final List<Object> bindings = new ArrayList<>();

    protected SQLiteProgram(SQLiteDatabase database, String sql) {
        if (database == null) throw new IllegalArgumentException("database == null");
        if (sql == null) throw new IllegalArgumentException("sql == null");
        this.database = database;
        this.sql = sql;
    }

    public final SQLiteDatabase getDatabase() {
        return database;
    }

    public final String getSql() {
        return sql;
    }

    public void bindNull(int index) {
        bind(index, null);
    }

    public void bindLong(int index, long value) {
        bind(index, value);
    }

    public void bindDouble(int index, double value) {
        bind(index, value);
    }

    public void bindString(int index, String value) {
        bind(index, value);
    }

    public void bindBlob(int index, byte[] value) {
        bind(index, value);
    }

    /** Android 契约：清空全部绑定，随后从序号 1 开始重新写入。 */
    public void bindAllArgsAsStrings(String[] bindArgs) {
        if (bindArgs == null) return;
        bindings.clear();
        for (String argument : bindArgs) bindings.add(argument);
    }

    public void clearBindings() {
        bindings.clear();
    }

    /** 供引擎消费的绑定值（按序号 1..n 展开为数组；未绑定的序号为 null）。 */
    protected final Object[] boundArguments(int count) {
        Object[] arguments = new Object[count];
        for (int i = 0; i < count; i++) {
            arguments[i] = i < bindings.size() ? bindings.get(i) : null;
        }
        return arguments;
    }

    /** 按 SQL 里的 {@code ?} 占位符个数展开绑定值（这是 Android 的 {@code bindAllArgsAsStrings} 契约）。 */
    protected final Object[] boundArguments() {
        return boundArguments(placeholderCount(sql));
    }

    /** 统计 SQL 中不在字符串/标识符/注释里的 {@code ?} 占位符个数。 */
    private static int placeholderCount(String sql) {
        int count = 0;
        char quote = 0;
        boolean lineComment = false;
        boolean blockComment = false;
        for (int i = 0; i < sql.length(); i++) {
            char current = sql.charAt(i);
            char next = i + 1 < sql.length() ? sql.charAt(i + 1) : 0;
            if (lineComment) {
                if (current == '\n') lineComment = false;
                continue;
            }
            if (blockComment) {
                if (current == '*' && next == '/') {
                    blockComment = false;
                    i++;
                }
                continue;
            }
            if (quote != 0) {
                if (current == quote) {
                    if (next == quote) i++;
                    else quote = 0;
                }
                continue;
            }
            if (current == '-' && next == '-') {
                lineComment = true;
                i++;
            } else if (current == '/' && next == '*') {
                blockComment = true;
                i++;
            } else if (current == '\'' || current == '"' || current == '`') {
                quote = current;
            } else if (current == '[') {
                quote = ']';
            } else if (current == '?') {
                count++;
            }
        }
        return count;
    }

    private void bind(int index, Object value) {
        if (index < 1) throw new IllegalArgumentException("Cannot bind argument at index " + index + " because the index is out of range.");
        while (bindings.size() < index) bindings.add(null);
        bindings.set(index - 1, value);
    }

    @Override
    protected void onAllReferencesReleased() {
        clearBindings();
    }
}
