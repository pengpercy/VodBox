package android.database;

import android.database.sqlite.SQLiteDatabase;

import java.util.Locale;

/**
 * Android {@code DatabaseUtils} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 这些助手都建立在 {@link SQLiteDatabase} 的**公开 API** 之上，因此和真实 SQLite 语义一致：
 * {@code longForQuery}/{@code stringForQuery}/{@code blobForQuery} 真的执行查询，
 * {@code queryNumEntries} 真的 {@code SELECT COUNT(*)}，{@code getSqlStatementType} 真的按首词分类。
 * 引擎缺失时它们随 {@link SQLiteDatabase} 一起以 {@code UnsupportedOperationException} 明确失败。
 *
 * 未实现（调用即 NoSuchMethodError）：{@code dumpCursor}、{@code readException*FromParcel}、
 * {@code bindObjectToProgram}、{@code bindArgsToProgram}、{@code appendValueToSql}。
 */
public final class DatabaseUtils {

    public static final int STATEMENT_SELECT = 1;
    public static final int STATEMENT_UPDATE = 2;
    public static final int STATEMENT_ATTACH = 3;
    public static final int STATEMENT_DETACH = 4;
    public static final int STATEMENT_BEGIN = 5;
    public static final int STATEMENT_COMMIT = 6;
    public static final int STATEMENT_ABORT = 7;
    public static final int STATEMENT_PRAGMA = 8;
    public static final int STATEMENT_DDL = 9;
    public static final int STATEMENT_UNPREPARED = 10;
    public static final int STATEMENT_OTHER = 99;

    private DatabaseUtils() {
    }

    public static long longForQuery(SQLiteDatabase database, String query, String[] selectionArgs) {
        try (Cursor cursor = database.rawQuery(query, selectionArgs)) {
            if (!cursor.moveToFirst()) return 0L;
            return cursor.getLong(0);
        }
    }

    public static String stringForQuery(SQLiteDatabase database, String query, String[] selectionArgs) {
        try (Cursor cursor = database.rawQuery(query, selectionArgs)) {
            if (!cursor.moveToFirst()) return null;
            return cursor.getString(0);
        }
    }

    public static byte[] blobForQuery(SQLiteDatabase database, String query, String[] selectionArgs) {
        try (Cursor cursor = database.rawQuery(query, selectionArgs)) {
            if (!cursor.moveToFirst()) return null;
            return cursor.getBlob(0);
        }
    }

    public static long queryNumEntries(SQLiteDatabase database, String table) {
        return longForQuery(database, "SELECT COUNT(*) FROM " + table, null);
    }

    public static long queryNumEntries(SQLiteDatabase database, String table, String selection, String[] selectionArgs) {
        String where = selection == null || selection.isEmpty() ? "" : " WHERE " + selection;
        return longForQuery(database, "SELECT COUNT(*) FROM " + table + where, selectionArgs);
    }

    /** Android 契约：游标为空（或为 null）时为 {@code true}；不改变调用方游标的位置语义。 */
    public static boolean isEmpty(Cursor cursor) {
        if (cursor == null) return true;
        if (!cursor.moveToFirst()) return true;
        cursor.moveToPosition(-1);
        return false;
    }

    public static void closeQuietly(Cursor cursor) {
        if (cursor == null) return;
        try {
            cursor.close();
        } catch (RuntimeException ignored) {
            // Android 亦为「安静关闭」。
        }
    }

    /** 按首词分类 SQL（与 Android 的分类语义一致，只覆盖可判定的常见语句）。 */
    public static int getSqlStatementType(String sql) {
        if (sql == null) return STATEMENT_OTHER;
        String trimmed = sql.trim().toUpperCase(Locale.ROOT);
        if (trimmed.isEmpty()) return STATEMENT_OTHER;
        String first = trimmed.split("\\s+", 2)[0];
        switch (first) {
            case "SELECT":
            case "WITH":
                return STATEMENT_SELECT;
            case "INSERT":
            case "UPDATE":
            case "DELETE":
            case "REPLACE":
                return STATEMENT_UPDATE;
            case "ATTACH":
                return STATEMENT_ATTACH;
            case "DETACH":
                return STATEMENT_DETACH;
            case "BEGIN":
                return STATEMENT_BEGIN;
            case "COMMIT":
            case "END":
                return STATEMENT_COMMIT;
            case "ROLLBACK":
                return STATEMENT_ABORT;
            case "PRAGMA":
                return STATEMENT_PRAGMA;
            case "CREATE":
            case "DROP":
            case "ALTER":
                return STATEMENT_DDL;
            case "EXPLAIN":
                return STATEMENT_UNPREPARED;
            default:
                return STATEMENT_OTHER;
        }
    }

    /**
     * Android 契约：建一个只有 {@code onCreate} 的辅助器，把给定的语句逐条执行。
     * 已经存在的库不会被重复初始化（版本号已写入）。
     */
    public static void createDbFromSqlStatements(android.content.Context context, String dbName, int dbVersion, String[] sqlStatements) {
        android.database.sqlite.SQLiteOpenHelper helper = new android.database.sqlite.SQLiteOpenHelper(context, dbName, null, dbVersion) {
            @Override
            public void onCreate(SQLiteDatabase database) {
                for (String statement : sqlStatements) {
                    if (statement == null || statement.trim().isEmpty()) continue;
                    database.execSQL(statement);
                }
            }

            @Override
            public void onUpgrade(SQLiteDatabase database, int oldVersion, int newVersion) {
                // 与 Android 的 DatabaseUtils 一致：只负责建库，升级由调用方处理。
            }
        };
        try {
            helper.getWritableDatabase();
        } finally {
            helper.close();
        }
    }
}
