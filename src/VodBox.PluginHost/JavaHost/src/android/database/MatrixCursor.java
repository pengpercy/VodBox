package android.database;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import android.os.Bundle;

/**
 * Android {@code MatrixCursor} 最小子集（VodBox 自有实现，独立编写）。
 *
 * **真实内存表**（不是空壳）：写入的行按列保存，定位/取值/列元数据语义与 Android 一致，
 * 行长度不匹配时抛 {@link IllegalArgumentException}。本宿主的 SQLite 查询结果也物化成它
 * （见 {@code dev.vodbox.jarhost.android.JdbcSqliteEngine}）。
 *
 * 未实现的 Android 成员（调用即明确失败）：{@code setNotificationUri}、
 * {@code registerContentObserver}、{@code commitUpdates}。
 */
public class MatrixCursor implements Cursor {

    private final String[] columnNames;
    private final ArrayList<Object[]> rows = new ArrayList<>();
    private int position = -1;
    private boolean closed;
    private boolean deactivated;
    private Bundle extras;

    public MatrixCursor(String[] columnNames) {
        this(columnNames, 16);
    }

    public MatrixCursor(String[] columnNames, int initialCapacity) {
        if (columnNames == null) throw new IllegalArgumentException("columnNames == null");
        String[] copy = new String[columnNames.length];
        for (int i = 0; i < columnNames.length; i++) {
            if (columnNames[i] == null) throw new IllegalArgumentException("column name == null");
            copy[i] = columnNames[i];
        }
        this.columnNames = copy;
        if (initialCapacity > 0) this.rows.ensureCapacity(initialCapacity);
    }

    // ---------- 写入（MatrixCursor 特有） ----------

    /** Android 契约：{@code columnValues.length} 必须等于列数。 */
    public void addRow(Object[] columnValues) {
        if (columnValues.length != columnNames.length) {
            throw new IllegalArgumentException("columnNames.length = " + columnNames.length
                    + ", columnValues.length = " + columnValues.length);
        }
        rows.add(Arrays.copyOf(columnValues, columnValues.length));
    }

    public void addRow(Iterable<?> columnValues) {
        Object[] values = new Object[columnNames.length];
        int index = 0;
        for (Object value : columnValues) {
            if (index == columnNames.length) {
                throw new IllegalArgumentException("columnValues.size() > columnNames.length");
            }
            values[index++] = value;
        }
        if (index != columnNames.length) {
            throw new IllegalArgumentException("columnValues.size() < columnNames.length");
        }
        addRow(values);
    }

    public RowBuilder newRow() {
        return new RowBuilder(this);
    }

    /**
     * 逐列构建一行。语义与 Android 一致：**填满最后一列即写入游标**；也可以显式 {@link #finish()}。
     * 仍在构建中（列数不满且未 finish）的行不会被读出。
     */
    public static class RowBuilder {

        private final Object[] values;
        private final MatrixCursor cursor;
        private int index;
        private boolean finished;

        RowBuilder(MatrixCursor cursor) {
            this.cursor = cursor;
            this.values = new Object[cursor.columnNames.length];
        }

        public RowBuilder add(Object columnValue) {
            if (finished) throw new IllegalArgumentException("No more columns left");
            values[index++] = columnValue;
            if (index == values.length) finish();
            return this;
        }

        public RowBuilder add(String columnName, Object value) {
            if (finished) throw new IllegalArgumentException("No more columns left");
            int index = cursor.getColumnIndex(columnName);
            if (index >= 0) {
                values[index] = value;
                if (++this.index == values.length) finish();
            }
            return this;
        }

        /** 显式提交；列数不足时抛 {@link IllegalArgumentException}（与 Android 一致）。 */
        public void finish() {
            if (finished) return;
            if (index != values.length) {
                throw new IllegalArgumentException("Incomplete row: " + index + " of " + values.length + " columns");
            }
            finished = true;
            cursor.addRow(values);
        }
    }

    // ---------- Cursor ----------

    @Override
    public int getCount() {
        return closed || deactivated ? 0 : rows.size();
    }

    @Override
    public int getPosition() {
        return position;
    }

    @Override
    public boolean move(int offset) {
        return moveToPosition(position + offset);
    }

    @Override
    public boolean moveToPosition(int target) {
        int count = getCount();
        if (target < 0 || target >= count) {
            position = target < 0 ? -1 : count;
            return false;
        }
        position = target;
        return true;
    }

    @Override
    public boolean moveToFirst() {
        return moveToPosition(0);
    }

    @Override
    public boolean moveToLast() {
        return moveToPosition(getCount() - 1);
    }

    @Override
    public boolean moveToNext() {
        return moveToPosition(position + 1);
    }

    @Override
    public boolean moveToPrevious() {
        return moveToPosition(position - 1);
    }

    @Override
    public boolean isFirst() {
        return position == 0 && getCount() > 0;
    }

    @Override
    public boolean isLast() {
        int count = getCount();
        return count > 0 && position == count - 1;
    }

    @Override
    public boolean isBeforeFirst() {
        return getCount() == 0 ? position == -1 : position < 0;
    }

    @Override
    public boolean isAfterLast() {
        int count = getCount();
        return count == 0 ? position == 0 : position >= count;
    }

    @Override
    public int getColumnIndex(String columnName) {
        if (columnName == null) return -1;
        int dot = columnName.lastIndexOf('.');
        if (dot != -1) columnName = columnName.substring(dot + 1);
        for (int i = 0; i < columnNames.length; i++) {
            if (columnName.equals(columnNames[i])) return i;
        }
        return -1;
    }

    @Override
    public int getColumnIndexOrThrow(String columnName) {
        int index = getColumnIndex(columnName);
        if (index < 0) throw new IllegalArgumentException("column '" + columnName + "' does not exist");
        return index;
    }

    @Override
    public String getColumnName(int columnIndex) {
        return columnNames[columnIndex];
    }

    @Override
    public String[] getColumnNames() {
        return Arrays.copyOf(columnNames, columnNames.length);
    }

    @Override
    public int getColumnCount() {
        return columnNames.length;
    }

    @Override
    public boolean isNull(int columnIndex) {
        return valueAt(columnIndex) == null;
    }

    @Override
    public int getType(int columnIndex) {
        return typeOf(valueAt(columnIndex));
    }

    @Override
    public short getShort(int columnIndex) {
        Object value = valueAt(columnIndex);
        return value == null ? 0 : ((Number) toNumber(value, columnIndex)).shortValue();
    }

    @Override
    public int getInt(int columnIndex) {
        Object value = valueAt(columnIndex);
        return value == null ? 0 : ((Number) toNumber(value, columnIndex)).intValue();
    }

    @Override
    public long getLong(int columnIndex) {
        Object value = valueAt(columnIndex);
        return value == null ? 0L : ((Number) toNumber(value, columnIndex)).longValue();
    }

    @Override
    public float getFloat(int columnIndex) {
        Object value = valueAt(columnIndex);
        return value == null ? 0f : ((Number) toNumber(value, columnIndex)).floatValue();
    }

    @Override
    public double getDouble(int columnIndex) {
        Object value = valueAt(columnIndex);
        return value == null ? 0d : ((Number) toNumber(value, columnIndex)).doubleValue();
    }

    @Override
    public String getString(int columnIndex) {
        Object value = valueAt(columnIndex);
        if (value == null) return null;
        if (value instanceof byte[] bytes) return new String(bytes, java.nio.charset.StandardCharsets.UTF_8);
        return String.valueOf(value);
    }

    @Override
    public byte[] getBlob(int columnIndex) {
        Object value = valueAt(columnIndex);
        if (value == null) return null;
        if (value instanceof byte[] bytes) return Arrays.copyOf(bytes, bytes.length);
        throw new android.database.SQLException("getBlob() called on a non-blob column");
    }

    @Override
    public void close() {
        closed = true;
        rows.clear();
    }

    @Override
    public boolean isClosed() {
        return closed;
    }

    @Override
    public void deactivate() {
        deactivated = true;
    }

    @Override
    public boolean requery() {
        deactivated = false;
        return true;
    }

    @Override
    public Bundle getExtras() {
        return extras;
    }

    @Override
    public Bundle respond(Bundle extras) {
        this.extras = extras;
        return extras == null ? new Bundle() : extras;
    }

    // ---------- 内部 ----------

    private Object valueAt(int columnIndex) {
        if (deactivated || closed || position < 0) {
            throw new android.database.StaleDataException("Attempt to access a cursor that has already been deactivated/closed");
        }
        if (position >= rows.size()) {
            throw new android.database.CursorIndexOutOfBoundsException(position, rows.size());
        }
        return rows.get(position)[columnIndex];
    }

    /** 与 Android 一致：未知类型当字符串处理（这里用 {@code getString} 的字符串化语义）。 */
    private Number toNumber(Object value, int columnIndex) {
        if (value instanceof Number number) return number;
        try {
            return Double.valueOf(String.valueOf(value).trim());
        } catch (NumberFormatException error) {
            throw new android.database.SQLException(
                    "Couldn't read row " + position + ", col " + columnIndex + " from CursorWindow");
        }
    }

    private static int typeOf(Object value) {
        if (value == null) return Cursor.FIELD_TYPE_NULL;
        if (value instanceof Long || value instanceof Integer || value instanceof Short || value instanceof Byte) {
            return Cursor.FIELD_TYPE_INTEGER;
        }
        if (value instanceof Double || value instanceof Float) return Cursor.FIELD_TYPE_FLOAT;
        if (value instanceof byte[]) return Cursor.FIELD_TYPE_BLOB;
        if (value instanceof CharSequence || value instanceof Character) return Cursor.FIELD_TYPE_STRING;
        return Cursor.FIELD_TYPE_STRING;
    }

    /** 供调试/诊断：当前行数（不依赖游标定位）。 */
    public int rawRowCount() {
        return rows.size();
    }

    /** 供调试/诊断：把某一行按列序读出（不改变 {@link #getPosition()}）。 */
    public List<Object> rawRow(int row) {
        return new ArrayList<>(Arrays.asList(rows.get(row)));
    }
}
