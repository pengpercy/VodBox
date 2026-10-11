package android.database.sqlite;

import android.content.Context;
import android.database.DatabaseErrorHandler;

/**
 * Android {@code SQLiteOpenHelper} 最小子集（VodBox 自有实现，独立编写）。
 *
 * <h2>语义（与 Android 的 {@code getDatabaseLocked} 对齐）</h2>
 * <ul>
 *   <li>数据库路径取自 {@code Context.getDatabasePath(name)}；{@code name == null} 为内存库；</li>
 *   <li>版本号就是 SQLite 的 {@code PRAGMA user_version}：新库为 0 → 在事务里
 *       {@code onCreate}；已存在且低于目标版本 → {@code onUpgrade}；高于目标版本 → {@code onDowngrade}
 *       （默认抛 {@link SQLiteException}，与 Android 一致）；</li>
 *   <li>{@code onConfigure} 在读版本之前调用，{@code onOpen} 在版本处理之后调用；</li>
 *   <li>版本处理在一个事务里完成，失败则回滚并关闭刚打开的数据库（不留下半迁移状态）。</li>
 * </ul>
 *
 * 引擎缺失时，第一次真正读版本/建表就会抛 {@link UnsupportedOperationException}
 * （宿主归类为 {@code unsupported_android}）——**不**假装建过表。
 */
public abstract class SQLiteOpenHelper implements AutoCloseable {

    public static final int DATABASE_VERSION = 0;

    private final Context context;
    private final String name;
    private final int version;
    private final DatabaseErrorHandler errorHandler;

    private SQLiteDatabase database;
    private boolean writeAheadLoggingEnabled;

    public SQLiteOpenHelper(Context context, String name, SQLiteDatabase.CursorFactory factory, int version) {
        this(context, name, factory, version, null);
    }

    public SQLiteOpenHelper(Context context, String name, SQLiteDatabase.CursorFactory factory, int version,
                            DatabaseErrorHandler errorHandler) {
        if (version < 1) throw new IllegalArgumentException("Version must be >= 1, was " + version);
        this.context = context;
        this.name = name;
        this.version = version;
        this.errorHandler = errorHandler;
    }

    public String getDatabaseName() {
        return name;
    }

    /** Android 契约：在数据库打开前设置；打开后立即生效（{@code PRAGMA journal_mode}）。 */
    public void setWriteAheadLoggingEnabled(boolean enabled) {
        synchronized (this) {
            if (writeAheadLoggingEnabled != enabled) {
                writeAheadLoggingEnabled = enabled;
                if (database != null && database.isOpen()) {
                    if (enabled) database.enableWriteAheadLogging();
                    else database.disableWriteAheadLogging();
                }
            }
        }
    }

    public SQLiteDatabase getWritableDatabase() {
        synchronized (this) {
            if (database != null) {
                if (!database.isOpen()) {
                    database = null;
                } else {
                    database.acquireReference();
                    return database;
                }
            }
            SQLiteDatabase opened = open();
            if (writeAheadLoggingEnabled) opened.enableWriteAheadLogging();
            database = opened;
            return database;
        }
    }

    /** Android 语义：缓存的连接可写时直接复用（本宿主只有一种连接）。 */
    public SQLiteDatabase getReadableDatabase() {
        return getWritableDatabase();
    }

    /** Android 契约：回调只对「新建的数据库」触发一次。 */
    public void onConfigure(SQLiteDatabase database) {
    }

    public abstract void onCreate(SQLiteDatabase database);

    public abstract void onUpgrade(SQLiteDatabase database, int oldVersion, int newVersion);

    /** Android 契约：默认拒绝降级。 */
    public void onDowngrade(SQLiteDatabase database, int oldVersion, int newVersion) {
        throw new SQLiteException("Can't downgrade database from version " + oldVersion + " to " + newVersion);
    }

    public void onOpen(SQLiteDatabase database) {
    }

    @Override
    public void close() {
        synchronized (this) {
            if (database != null) database.close();
            database = null;
        }
    }

    // ---------- 内部 ----------

    private SQLiteDatabase open() {
        String path = name == null ? null : context.getDatabasePath(name).getPath();
        SQLiteDatabase opened = SQLiteDatabase.openOrCreateDatabase(path, null, errorHandler);
        try {
            onConfigure(opened);
            int current = opened.getVersion();
            if (current != version) {
                opened.beginTransaction();
                try {
                    if (current == 0) onCreate(opened);
                    else if (current > version) onDowngrade(opened, current, version);
                    else onUpgrade(opened, current, version);
                    opened.setVersion(version);
                    opened.setTransactionSuccessful();
                } finally {
                    opened.endTransaction();
                }
            }
            onOpen(opened);
            return opened;
        } catch (RuntimeException failure) {
            opened.close();
            throw failure;
        }
    }
}
