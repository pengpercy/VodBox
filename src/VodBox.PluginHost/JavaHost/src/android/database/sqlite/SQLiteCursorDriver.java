package android.database.sqlite;

import android.database.Cursor;

/**
 * Android {@code SQLiteCursorDriver} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：作为 {@link SQLiteDatabase.CursorFactory#newCursor} 的参数出现，
 * 让自定义游标实现能驱动这条查询。未实现的 Android 内部成员（{@code editingTable}）不做伪装。
 */
public interface SQLiteCursorDriver {

    Cursor query(SQLiteDatabase.CursorFactory factory, String[] bindArgs);

    void cursorDeactivated();

    void cursorRequeried(Cursor cursor);

    void cursorClosed();

    void setBindArguments(String[] bindArgs);
}
