package android.database;

/**
 * Android {@code StaleDataException} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：访问已 {@code deactivate}/{@code close} 的游标时抛出。
 */
public class StaleDataException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    public StaleDataException() {
        super();
    }

    public StaleDataException(String message) {
        super(message);
    }
}
