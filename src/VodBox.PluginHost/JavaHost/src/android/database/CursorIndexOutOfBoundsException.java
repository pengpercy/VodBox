package android.database;

/**
 * Android {@code CursorIndexOutOfBoundsException} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：非受检的索引越界异常，消息形状为 {@code Index N requested, with a size of M}。
 */
public class CursorIndexOutOfBoundsException extends IndexOutOfBoundsException {

    private static final long serialVersionUID = 1L;

    public CursorIndexOutOfBoundsException(int index, int size) {
        super("Index " + index + " requested, with a size of " + size);
    }

    public CursorIndexOutOfBoundsException(String message) {
        super(message);
    }
}
