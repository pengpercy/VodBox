package android.database.sqlite;

import android.content.ContentValues;
import android.database.Cursor;
import android.database.DatabaseErrorHandler;
import android.database.MatrixCursor;
import android.os.CancellationSignal;

import dev.vodbox.jarhost.android.JdbcSqliteEngine;

import java.io.File;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * Android {@code SQLiteDatabase} 最小子集（VodBox 自有实现，独立编写）。
 *
 * <h2>语义</h2>
 * 数据路径是**真实 SQLite**：本类把语句交给 {@code dev.vodbox.jarhost.android.JdbcSqliteEngine}
 * （JDBC + 运行时发现的 SQLite 驱动）。查询结果物化为 {@link MatrixCursor}，因此
 * {@code rawQuery}/{@code query}/{@code insert}/{@code update}/{@code delete}/{@code execSQL}/
 * 事务/版本号都是真实现，不是空壳。
 *
 * <h2>没有 SQLite 驱动时的行为（不伪装）</h2>
 * {@link #openDatabase} 只做**文件层校验**（不存在 / 只读冲突），因此可以成功；一旦真的读写数据，
 * 就抛 {@link UnsupportedOperationException}（消息点明本类与缺失的驱动），宿主归类为
 * {@code unsupported_android}。**绝不**把写操作丢弃、把查询当空表——那会让插件以为自己写的缓存
 * 真的落盘了。
 *
 * <h2>明确不实现（调用即 NoSuchMethodError → unsupported_android）</h2>
 * {@code getAttachedDbs}、{@code setCustomCollation}、{@code dump}、{@code getOuterTransaction}、
 * {@code SQLiteDatabase.OpenParams}/{@code openDatabase(File, OpenParams)}。
 *
 * <h2>明确是「建议性」的成员</h2>
 * {@code setMaximumSize}（返回传入值，Android 亦为建议性）、{@code setMaxSqlCacheSize}、
 * {@code setLocale}（引擎使用默认 BINARY 排序规则，不改 SQLite 的注册排序规则）、
 * {@code setLockingEnabled}（废弃）。这些**不影响可见的数据语义**，已在各自注释里说明。
 */
public final class SQLiteDatabase extends SQLiteClosable {

    // ---------- 冲突策略（取值与 Android 一致） ----------
    public static final int CONFLICT_ROLLBACK = 1;
    public static final int CONFLICT_ABORT = 2;
    public static final int CONFLICT_FAIL = 3;
    public static final int CONFLICT_IGNORE = 4;
    public static final int CONFLICT_REPLACE = 5;
    public static final int CONFLICT_NONE = 0;

    public static final int SQLITE_MAX_LIKE_PATTERN_LENGTH = 50000;

    // ---------- 打开标志（取值与 Android 一致） ----------
    public static final int OPEN_READWRITE = 0x00000000;
    public static final int OPEN_READONLY = 0x00000001;
    public static final int NO_LOCALIZED_COLLATORS = 0x00000010;
    public static final int CREATE_IF_NECESSARY = 0x10000000;
    public static final int ENABLE_WRITE_AHEAD_LOGGING = 0x20000000;

    /** 引擎的 statement 缓存上限；Android 亦为建议性。 */
    public static final int MAX_SQL_CACHE_SIZE = 100;

    public interface CursorFactory {
        Cursor newCursor(SQLiteDatabase db, SQLiteCursorDriver masterQuery, String editTable, SQLiteQuery query);
    }

    private final String path;
    private final boolean readOnly;
    private final File file;
    private final DatabaseErrorHandler errorHandler;

    private JdbcSqliteEngine engine;
    private boolean closed;
    private int transactionDepth;
    private boolean transactionSuccessful;
    private SQLiteTransactionListener transactionListener;
    private long maximumSize = Long.MAX_VALUE;

    private SQLiteDatabase(String path, boolean readOnly, DatabaseErrorHandler errorHandler) {
        this.path = path == null || path.isEmpty() ? ":memory:" : path;
        this.readOnly = readOnly;
        this.file = isMemory() ? null : new File(this.path);
        this.errorHandler = errorHandler;
    }

    // ---------- 打开 / 删除 ----------

    public static SQLiteDatabase openDatabase(String path, CursorFactory factory, int flags) {
        return openDatabase(path, factory, flags, null);
    }

    /**
     * 打开数据库。这里只做**文件层**校验（与 Android 可观测行为一致）：
     * 只读打开要求文件已存在；{@code CREATE_IF_NECESSARY} 未设置时也要求文件已存在。
     * 引擎连接是**惰性**的，第一次真正读写时才建立。
     */
    public static SQLiteDatabase openDatabase(String path, CursorFactory factory, int flags, DatabaseErrorHandler handler) {
        boolean readOnly = (flags & OPEN_READONLY) != 0;
        boolean create = (flags & CREATE_IF_NECESSARY) != 0;
        SQLiteDatabase database = new SQLiteDatabase(path, readOnly, handler);
        database.checkFile(create);
        return database;
    }

    public static SQLiteDatabase openOrCreateDatabase(File file, CursorFactory factory) {
        return openDatabase(file == null ? null : file.getPath(), factory, CREATE_IF_NECESSARY);
    }

    public static SQLiteDatabase openOrCreateDatabase(String path, CursorFactory factory) {
        return openDatabase(path, factory, CREATE_IF_NECESSARY);
    }

    public static SQLiteDatabase openOrCreateDatabase(File file, CursorFactory factory, DatabaseErrorHandler handler) {
        return openDatabase(file == null ? null : file.getPath(), factory, CREATE_IF_NECESSARY, handler);
    }

    public static SQLiteDatabase openOrCreateDatabase(String path, CursorFactory factory, DatabaseErrorHandler handler) {
        return openDatabase(path, factory, CREATE_IF_NECESSARY, handler);
    }

    /** Android 契约：{@code null} 游标工厂 + 内存库。 */
    public static SQLiteDatabase create(CursorFactory factory) {
        return openDatabase(":memory:", factory, CREATE_IF_NECESSARY);
    }

    /** Android 契约：删除主文件与 {@code -journal}/{@code -wal}/{@code -shm} 边车文件。 */
    public static boolean deleteDatabase(File file) {
        if (file == null) throw new IllegalArgumentException("file must not be null");
        boolean deleted = deleteIfExists(file);
        deleted |= deleteIfExists(new File(file.getPath() + "-journal"));
        deleted |= deleteIfExists(new File(file.getPath() + "-wal"));
        deleted |= deleteIfExists(new File(file.getPath() + "-shm"));
        return deleted;
    }

    /** Android 亦为建议性：本宿主没有跨库的 LRU 缓存需要回收。 */
    public static int releaseMemory() {
        return 0;
    }

    /** Android 契约：{@code 1} 时返回第一个表名，否则 {@code null}。 */
    public static String findEditTable(String tables) {
        if (tables == null) return null;
        int space = tables.indexOf(' ');
        int comma = tables.indexOf(',');
        int end;
        if (space > 0 && (comma < 0 || space < comma)) end = space;
        else if (comma > 0) end = comma;
        else end = tables.length();
        String candidate = tables.substring(0, end);
        return candidate.isEmpty() ? null : candidate;
    }

    private static boolean deleteIfExists(File file) {
        return file.exists() && file.delete();
    }

    private void checkFile(boolean createIfNecessary) {
        if (isMemory()) return;
        if (readOnly) {
            if (!file.isFile()) throw new SQLiteException("unable to open database file (readonly): " + path);
            return;
        }
        if (!file.isFile() && !createIfNecessary) {
            throw new SQLiteException("unable to open database file: " + path);
        }
        if (createIfNecessary && file.getParentFile() != null) file.getParentFile().mkdirs();
    }

    private boolean isMemory() {
        return path == null || ":memory:".equals(path);
    }

    // ---------- 引擎 ----------

    /** 懒连接真实引擎；驱动缺失时抛出明确的「未实现」拒绝（宿主归类为 {@code unsupported_android}）。 */
    JdbcSqliteEngine engine() {
        if (closed) throw new IllegalStateException("attempt to re-open an already-closed object: " + this);
        if (engine != null) return engine;
        try {
            engine = JdbcSqliteEngine.open(isMemory() ? ":memory:" : path, readOnly, true);
            return engine;
        } catch (SQLiteException failure) {
            if (isMemory() || !looksCorrupt(failure)) throw failure;
            // Android 语义：损坏时回调处理器，或（无处理器）删除损坏文件后重试一次。
            if (errorHandler != null) {
                errorHandler.onCorruption(this);
            } else {
                deleteDatabase(file);
            }
            engine = JdbcSqliteEngine.open(path, readOnly, true);
            return engine;
        }
    }

    private static boolean looksCorrupt(SQLiteException failure) {
        String message = failure.getMessage() == null ? "" : failure.getMessage().toLowerCase(Locale.ROOT);
        return message.contains("not a database") || message.contains("malformed") || message.contains("corrupt");
    }

    // ---------- 查询 ----------

    public Cursor rawQuery(String sql, String[] selectionArgs) {
        return rawQuery(sql, selectionArgs, null);
    }

    public Cursor rawQuery(String sql, String[] selectionArgs, CancellationSignal cancellationSignal) {
        return rawQueryWithFactory(null, sql, selectionArgs, null, cancellationSignal);
    }

    public Cursor rawQueryWithFactory(CursorFactory cursorFactory, String sql, String[] selectionArgs, String editTable) {
        return rawQueryWithFactory(cursorFactory, sql, selectionArgs, editTable, null);
    }

    public Cursor rawQueryWithFactory(CursorFactory cursorFactory, String sql, String[] selectionArgs,
                                      String editTable, CancellationSignal cancellationSignal) {
        checkCanceled(cancellationSignal);
        SQLiteQuery query = new SQLiteQuery(this, sql);
        query.bindAllArgsAsStrings(selectionArgs);
        Driver driver = new Driver(query, editTable, selectionArgs);
        Cursor cursor = driver.query(cursorFactory, selectionArgs);
        checkCanceled(cancellationSignal);
        return cursor;
    }

    /** 与 {@code SQLiteQueryBuilder.buildQueryString} 同形的组装（本仓库独立实现）。 */
    private static String buildQueryString(boolean distinct, String tables, String[] columns, String where,
                                          String groupBy, String having, String orderBy, String limit) {
        if (tables == null || tables.isEmpty()) throw new IllegalArgumentException("Invalid tables");
        StringBuilder sql = new StringBuilder("SELECT ");
        if (distinct) sql.append("DISTINCT ");
        sql.append(columns == null || columns.length == 0 ? "*" : String.join(", ", columns));
        sql.append(" FROM ").append(tables);
        appendClause(sql, " WHERE ", where);
        appendClause(sql, " GROUP BY ", groupBy);
        appendClause(sql, " HAVING ", having);
        appendClause(sql, " ORDER BY ", orderBy);
        appendClause(sql, " LIMIT ", limit);
        return sql.toString();
    }

    private static void appendClause(StringBuilder sql, String name, String clause) {
        if (clause != null && !clause.isEmpty()) sql.append(name).append(clause);
    }

    public Cursor query(boolean distinct, String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy, String limit) {
        return query(distinct, table, columns, selection, selectionArgs, groupBy, having, orderBy, limit, null);
    }

    public Cursor query(boolean distinct, String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy, String limit, CancellationSignal cancellationSignal) {
        return rawQueryWithFactory(null,
                buildQueryString(distinct, table, columns, selection, groupBy, having, orderBy, limit),
                selectionArgs, findEditTable(table), cancellationSignal);
    }

    public Cursor query(String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy) {
        return query(false, table, columns, selection, selectionArgs, groupBy, having, orderBy, null);
    }

    public Cursor query(String table, String[] columns, String selection, String[] selectionArgs,
                        String groupBy, String having, String orderBy, String limit) {
        return query(false, table, columns, selection, selectionArgs, groupBy, having, orderBy, limit);
    }

    public Cursor queryWithFactory(CursorFactory cursorFactory, boolean distinct, String table, String[] columns,
                                   String selection, String[] selectionArgs, String groupBy, String having,
                                   String orderBy, String limit) {
        return queryWithFactory(cursorFactory, distinct, table, columns, selection, selectionArgs, groupBy, having, orderBy, limit, null);
    }

    public Cursor queryWithFactory(CursorFactory cursorFactory, boolean distinct, String table, String[] columns,
                                   String selection, String[] selectionArgs, String groupBy, String having,
                                   String orderBy, String limit, CancellationSignal cancellationSignal) {
        return rawQueryWithFactory(cursorFactory,
                buildQueryString(distinct, table, columns, selection, groupBy, having, orderBy, limit),
                selectionArgs, findEditTable(table), cancellationSignal);
    }

    // ---------- 写 ----------

    /** Android 契约：出错时返回 -1（并把 SQL 错误留在 stderr），**不**吞掉「未实现引擎」的显式拒绝。 */
    public long insert(String table, String nullColumnHack, ContentValues values) {
        try {
            return insertWithOnConflict(table, nullColumnHack, values, CONFLICT_NONE);
        } catch (SQLiteException error) {
            return logInsertFailure(values, error);
        }
    }

    public long insertOrThrow(String table, String nullColumnHack, ContentValues values) {
        return insertWithOnConflict(table, nullColumnHack, values, CONFLICT_NONE);
    }

    public long replace(String table, String nullColumnHack, ContentValues values) {
        try {
            return insertWithOnConflict(table, nullColumnHack, values, CONFLICT_REPLACE);
        } catch (SQLiteException error) {
            return logInsertFailure(values, error);
        }
    }

    public long replaceOrThrow(String table, String nullColumnHack, ContentValues values) {
        return insertWithOnConflict(table, nullColumnHack, values, CONFLICT_REPLACE);
    }

    private static long logInsertFailure(ContentValues values, SQLiteException error) {
        System.err.println("E/SQLiteDatabase: Error inserting " + values + ": " + error.getMessage());
        return -1;
    }

    public long insertWithOnConflict(String table, String nullColumnHack, ContentValues initialValues, int conflictAlgorithm) {
        if (initialValues == null) throw new IllegalArgumentException("null ContentValues");
        StringBuilder sql = new StringBuilder("INSERT");
        String conflict = conflictClause(conflictAlgorithm);
        if (!conflict.isEmpty()) sql.append(' ').append(conflict);
        sql.append(" INTO ").append(table);
        List<Object> arguments = new java.util.ArrayList<>();
        if (initialValues.size() > 0) {
            StringBuilder columns = new StringBuilder();
            StringBuilder placeholders = new StringBuilder();
            for (Map.Entry<String, Object> entry : initialValues.valueSet()) {
                if (columns.length() > 0) {
                    columns.append(", ");
                    placeholders.append(", ");
                }
                columns.append(entry.getKey());
                placeholders.append('?');
                arguments.add(entry.getValue());
            }
            sql.append(" (").append(columns).append(") VALUES (").append(placeholders).append(')');
        } else if (nullColumnHack != null && !nullColumnHack.isEmpty()) {
            sql.append(" (").append(nullColumnHack).append(") VALUES (NULL)");
        } else {
            throw new IllegalArgumentException("Empty values");
        }
        return engine().insert(sql.toString(), arguments.toArray());
    }

    public int delete(String table, String whereClause, String[] whereArgs) {
        String sql = "DELETE FROM " + table + (whereClause != null && !whereClause.isEmpty() ? " WHERE " + whereClause : "");
        return engine().update(sql, toObjects(whereArgs));
    }

    public int update(String table, ContentValues values, String whereClause, String[] whereArgs) {
        return updateWithOnConflict(table, values, whereClause, whereArgs, CONFLICT_NONE);
    }

    public int updateWithOnConflict(String table, ContentValues values, String whereClause, String[] whereArgs, int conflictAlgorithm) {
        if (values == null || values.size() == 0) throw new IllegalArgumentException("Empty values");
        StringBuilder sql = new StringBuilder("UPDATE ");
        String conflict = conflictAlgorithm(conflictAlgorithm);
        if (!conflict.isEmpty()) sql.append("OR ").append(conflict).append(' ');
        sql.append(table).append(" SET ");
        List<Object> arguments = new java.util.ArrayList<>();
        boolean first = true;
        for (Map.Entry<String, Object> entry : values.valueSet()) {
            if (!first) sql.append(", ");
            sql.append(entry.getKey()).append(" = ?");
            arguments.add(entry.getValue());
            first = false;
        }
        if (whereClause != null && !whereClause.isEmpty()) sql.append(" WHERE ").append(whereClause);
        if (whereArgs != null) arguments.addAll(java.util.Arrays.asList(whereArgs));
        return engine().update(sql.toString(), arguments.toArray());
    }

    public void execSQL(String sql) {
        engine().execute(sql, null);
    }

    public void execSQL(String sql, Object[] bindArgs) {
        engine().execute(sql, bindArgs);
    }

    public SQLiteStatement compileStatement(String sql) {
        return new SQLiteStatement(this, sql);
    }

    /** {@code INSERT <clause> INTO ...} 用的完整冲突关键字（与 Android 一致）。 */
    private static String conflictClause(int conflictAlgorithm) {
        String algorithm = conflictAlgorithm(conflictAlgorithm);
        return algorithm.isEmpty() ? "" : "OR " + algorithm;
    }

    /** 冲突算法名（不含 {@code OR}）：供 INSERT 与 UPDATE 复用。 */
    private static String conflictAlgorithm(int conflictAlgorithm) {
        switch (conflictAlgorithm) {
            case CONFLICT_ROLLBACK: return "ROLLBACK";
            case CONFLICT_ABORT: return "ABORT";
            case CONFLICT_FAIL: return "FAIL";
            case CONFLICT_IGNORE: return "IGNORE";
            case CONFLICT_REPLACE: return "REPLACE";
            default: return "";
        }
    }

    // ---------- 事务 ----------

    public void beginTransaction() {
        beginTransactionWithListener(null);
    }

    public void beginTransactionNonExclusive() {
        beginTransactionWithListener(null);
    }

    /** Android 契约：嵌套调用只在外层真正开启事务，监听器也只对外层触发一次。 */
    public void beginTransactionWithListener(SQLiteTransactionListener listener) {
        if (closed) throw new IllegalStateException("database is closed");
        if (transactionDepth == 0) {
            engine().begin();
            transactionSuccessful = false;
            transactionListener = listener;
            if (listener != null) listener.onBegin();
        }
        transactionDepth++;
    }

    public void beginTransactionWithListenerNonExclusive(SQLiteTransactionListener listener) {
        beginTransactionWithListener(listener);
    }

    public void endTransaction() {
        if (transactionDepth <= 0) throw new IllegalStateException("no transaction is active");
        if (transactionDepth == 1) {
            try {
                if (transactionSuccessful) {
                    engine().commit();
                    if (transactionListener != null) transactionListener.onCommit();
                } else {
                    engine().rollback();
                    if (transactionListener != null) transactionListener.onRollback();
                }
            } finally {
                engine().endTransaction();
                transactionSuccessful = false;
                transactionListener = null;
            }
        }
        transactionDepth--;
    }

    public void setTransactionSuccessful() {
        if (!inTransaction()) throw new IllegalStateException("no transaction is active");
        transactionSuccessful = true;
    }

    public boolean inTransaction() {
        return transactionDepth > 0;
    }

    /** Android 亦为建议性：本宿主按会话串行执行语句，不做让出。 */
    public boolean yieldIfContended() {
        return false;
    }

    public boolean yieldIfContendedSafely() {
        return false;
    }

    public boolean yieldIfContendedSafely(long sleepAfterYieldDelay) {
        return false;
    }

    /** 宿主按会话串行处理请求，因此不存在「别的线程拿着本连接的锁」。 */
    public boolean isDbLockedByCurrentThread() {
        return transactionDepth > 0;
    }

    public boolean isDbLockedByOtherThreads() {
        return false;
    }

    @Deprecated
    public void setLockingEnabled(boolean lockingEnabled) {
        // 废弃成员：Android 早已忽略该开关，本宿主同样不改变行为。
    }

    // ---------- 属性 ----------

    /** 与 Android 一致：版本号就是 SQLite 的 {@code PRAGMA user_version}。 */
    public int getVersion() {
        return engine().getUserVersion();
    }

    public void setVersion(int newVersion) {
        engine().setUserVersion(newVersion);
    }

    public long getMaximumSize() {
        return maximumSize;
    }

    /** Android 亦为建议性：返回值即传入值，不改变引擎行为。 */
    public long setMaximumSize(long numBytes) {
        maximumSize = numBytes;
        return maximumSize;
    }

    public long getPageSize() {
        return engine().scalarLong("PRAGMA page_size", null);
    }

    public void setPageSize(long numBytes) {
        engine().execute("PRAGMA page_size = " + numBytes, null);
    }

    public boolean isReadOnly() {
        return readOnly;
    }

    public boolean isOpen() {
        return !closed;
    }

    public boolean needUpgrade(int newVersion) {
        return newVersion > getVersion();
    }

    public final String getPath() {
        return path;
    }

    public void setForeignKeyConstraintsEnabled(boolean enable) {
        engine().execute("PRAGMA foreign_keys = " + (enable ? "ON" : "OFF"), null);
    }

    public boolean enableWriteAheadLogging() {
        return setJournalMode("WAL");
    }

    public void disableWriteAheadLogging() {
        setJournalMode("DELETE");
    }

    public boolean isWriteAheadLoggingEnabled() {
        return "wal".equalsIgnoreCase(engine().scalarString("PRAGMA journal_mode", null));
    }

    private boolean setJournalMode(String mode) {
        String applied = engine().scalarString("PRAGMA journal_mode = " + mode, null);
        return applied != null && applied.equalsIgnoreCase(mode);
    }

    /** 真实校验：执行 SQLite 的 {@code PRAGMA integrity_check}。 */
    public boolean isDatabaseIntegrityOk() {
        String result = engine().scalarString("PRAGMA integrity_check", null);
        return result != null && "ok".equalsIgnoreCase(result.trim());
    }

    /** 废弃成员：本宿主没有需要同步的表。 */
    @Deprecated
    public Map<String, String> getSyncedTables() {
        return new HashMap<>();
    }

    /** Android 亦为建议性：引擎使用固定大小的语句缓存。 */
    public void setMaxSqlCacheSize(int cacheSize) {
        if (cacheSize > MAX_SQL_CACHE_SIZE || cacheSize < 0) {
            throw new IllegalStateException("expected value between 0 and " + MAX_SQL_CACHE_SIZE);
        }
    }

    /** 明确说明：不注册自定义排序规则，引擎使用默认 BINARY 比较（调用不改变行为）。 */
    public void setLocale(Locale locale) {
        if (locale == null) throw new IllegalArgumentException("locale must not be null");
    }

    @Override
    protected void onAllReferencesReleased() {
        closed = true;
        if (engine != null) {
            engine.close();
            engine = null;
        }
    }

    @Override
    public String toString() {
        return "SQLiteDatabase: " + path;
    }

    // ---------- 内部 ----------

    private void checkCanceled(CancellationSignal signal) {
        if (signal != null) signal.throwIfCanceled();
    }

    private static Object[] toObjects(String[] arguments) {
        if (arguments == null) return null;
        Object[] result = new Object[arguments.length];
        System.arraycopy(arguments, 0, result, 0, arguments.length);
        return result;
    }

    /** 把 {@link SQLiteQuery} 交给游标工厂；没有工厂时物化成本宿主的 {@link MatrixCursor}。 */
    private final class Driver implements SQLiteCursorDriver {

        private final SQLiteQuery query;
        private final String editTable;
        private String[] bindArgs;

        Driver(SQLiteQuery query, String editTable, String[] bindArgs) {
            this.query = query;
            this.editTable = editTable;
            this.bindArgs = bindArgs;
        }

        @Override
        public Cursor query(CursorFactory factory, String[] bindArgs) {
            if (factory != null) return factory.newCursor(SQLiteDatabase.this, this, editTable, query);
            String[] arguments = bindArgs != null ? bindArgs : this.bindArgs;
            return engine().query(query.getSql(), toObjects(arguments));
        }

        @Override
        public void cursorDeactivated() {
            // 物化游标没有需要释放的窗口。
        }

        @Override
        public void cursorRequeried(Cursor cursor) {
            // 物化游标由调用方重新查询。
        }

        @Override
        public void cursorClosed() {
            // 无底层句柄。
        }

        @Override
        public void setBindArguments(String[] bindArgs) {
            this.bindArgs = bindArgs;
        }
    }
}
