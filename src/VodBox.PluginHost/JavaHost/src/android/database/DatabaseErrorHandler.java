package android.database;

import android.database.sqlite.SQLiteDatabase;

/**
 * Android {@code DatabaseErrorHandler} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：数据库损坏时的回调。本宿主在**打开/首次访问**阶段发现损坏（{@code file is
 * not a database} / {@code database disk image is malformed}）时才调用它，并在回调后重试一次
 * （见 {@code SQLiteDatabase.openDatabase(..., DatabaseErrorHandler)}）；未提供处理器时采用
 * Android 的默认行为：删除损坏的数据库文件后重试一次。
 */
public interface DatabaseErrorHandler {

    void onCorruption(SQLiteDatabase database);
}
