package android.os;

/**
 * Android {@code CancellationSignal} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 真实语义：{@link #cancel()} 幂等；重复 {@link #setOnCancelListener} 抛
 * {@link IllegalStateException}；{@link #throwIfCanceled()} 在已取消时抛
 * {@link android.os.OperationCanceledException}。宿主目前在查询前后各检查一次
 * （不中断正在执行的 SQL），这一点与 Android 的「尽力而为」一致。
 */
public final class CancellationSignal {

    private boolean canceled;
    private OnCancelListener listener;

    public boolean isCanceled() {
        synchronized (this) {
            return canceled;
        }
    }

    public void throwIfCanceled() {
        if (isCanceled()) throw new OperationCanceledException();
    }

    public void cancel() {
        OnCancelListener current;
        synchronized (this) {
            if (canceled) return;
            canceled = true;
            current = listener;
            listener = null;
        }
        if (current != null) current.onCancel();
    }

    public void setOnCancelListener(OnCancelListener listener) {
        synchronized (this) {
            if (canceled) {
                if (listener != null) listener.onCancel();
                return;
            }
            if (this.listener != null) {
                throw new IllegalStateException("Cannot have two cancel listeners registered at once");
            }
            this.listener = listener;
        }
    }

    public interface OnCancelListener {
        void onCancel();
    }
}
