package android.database;

import java.io.Closeable;

import android.os.Bundle;

/**
 * Android {@code android.database.Cursor} 的**最小子集**（VodBox 自有实现，独立编写）。
 *
 * 只声明普通 CatVod 插件在缓存/查询场景确实用到的成员：定位（{@code moveTo*}）、列元数据
 * （{@code getColumnIndex*}）、取值（{@code getString}/{@code getLong}/…）、{@code getExtras}。
 *
 * **明确不声明**的 Android 成员（插件调用时以 {@code NoSuchMethodError} 明确失败，由宿主归类为
 * {@code unsupported_android}，不伪装可用）：
 * {@code setNotificationUri}/{@code registerContentObserver}（本宿主没有 ContentProvider 通知）、
 * {@code getWantsAllOnMoveCalls}、{@code copyStringToBuffer}。
 */
public interface Cursor extends Closeable {

    /** {@code Cursor.getType} 的取值，与 Android 一致。 */
    int FIELD_TYPE_NULL = 0;
    int FIELD_TYPE_INTEGER = 1;
    int FIELD_TYPE_FLOAT = 2;
    int FIELD_TYPE_STRING = 3;
    int FIELD_TYPE_BLOB = 4;

    int getCount();

    int getPosition();

    boolean move(int offset);

    boolean moveToPosition(int position);

    boolean moveToFirst();

    boolean moveToLast();

    boolean moveToNext();

    boolean moveToPrevious();

    boolean isFirst();

    boolean isLast();

    boolean isBeforeFirst();

    boolean isAfterLast();

    int getColumnIndex(String columnName);

    int getColumnIndexOrThrow(String columnName);

    String getColumnName(int columnIndex);

    String[] getColumnNames();

    int getColumnCount();

    boolean isNull(int columnIndex);

    int getType(int columnIndex);

    short getShort(int columnIndex);

    int getInt(int columnIndex);

    long getLong(int columnIndex);

    float getFloat(int columnIndex);

    double getDouble(int columnIndex);

    String getString(int columnIndex);

    byte[] getBlob(int columnIndex);

    /** Android 契约：关闭后释放资源；重复调用是空操作。 */
    @Override
    void close();

    boolean isClosed();

    /** Android 契约：{@code deactivate} 后游标不可用，直到 {@link #requery()}。 */
    void deactivate();

    boolean requery();

    Bundle getExtras();

    Bundle respond(Bundle extras);
}
