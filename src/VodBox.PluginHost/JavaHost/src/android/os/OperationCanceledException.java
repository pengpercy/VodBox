package android.os;

/**
 * Android {@code OperationCanceledException} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：非受检异常，由 {@link CancellationSignal#throwIfCanceled()} 抛出。
 */
public class OperationCanceledException extends RuntimeException {

    private static final long serialVersionUID = 1L;

    public OperationCanceledException() {
        super();
    }

    public OperationCanceledException(String message) {
        super(message);
    }
}
