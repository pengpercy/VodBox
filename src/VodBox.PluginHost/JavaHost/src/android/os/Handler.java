package android.os;

/**
 * Android {@code Handler} 最小子集（VodBox 自有实现）。
 *
 * 绑定一个 {@link Looper}，把 {@link Runnable}/ {@link Message} 投递到该 Looper 的队列并按时间执行。
 * 行为与 Android 一致：无参构造要求当前线程已有 Looper，否则抛 {@code RuntimeException}；
 * 携带 {@code callback} 的消息优先执行 callback，其次 {@link Callback#handleMessage}，最后 {@link #handleMessage}。
 */
public class Handler {

    public interface Callback {
        boolean handleMessage(Message msg);
    }

    private final Looper looper;
    private final Callback callback;

    public Handler() {
        this(Looper.myLooper(), null, true);
    }

    public Handler(Callback callback) {
        this(Looper.myLooper(), callback, true);
    }

    public Handler(Looper looper) {
        this(looper, null, false);
    }

    public Handler(Looper looper, Callback callback) {
        this(looper, callback, false);
    }

    private Handler(Looper looper, Callback callback, boolean requireCurrentThread) {
        if (requireCurrentThread && looper == null) {
            throw new RuntimeException("无法在未调用 Looper.prepare() 的线程创建 Handler");
        }
        if (looper == null) throw new NullPointerException("looper == null");
        this.looper = looper;
        this.callback = callback;
    }

    public final Looper getLooper() {
        return looper;
    }

    /** 子类覆写以处理消息。 */
    public void handleMessage(Message msg) {
    }

    public void dispatchMessage(Message msg) {
        if (msg.callback != null) {
            msg.callback.run();
            return;
        }
        if (callback != null && callback.handleMessage(msg)) return;
        handleMessage(msg);
    }

    public final boolean post(Runnable runnable) {
        return sendMessageDelayed(getPostMessage(runnable), 0);
    }

    public final boolean postDelayed(Runnable runnable, long delayMillis) {
        return sendMessageDelayed(getPostMessage(runnable), delayMillis);
    }

    public final boolean postAtTime(Runnable runnable, long uptimeMillis) {
        return sendMessageAtTime(getPostMessage(runnable), uptimeMillis);
    }

    public final boolean sendMessage(Message msg) {
        return sendMessageDelayed(msg, 0);
    }

    public final boolean sendEmptyMessage(int what) {
        return sendEmptyMessageDelayed(what, 0);
    }

    public final boolean sendEmptyMessageDelayed(int what, long delayMillis) {
        Message msg = Message.obtain();
        msg.what = what;
        return sendMessageDelayed(msg, delayMillis);
    }

    public final boolean sendEmptyMessageAtTime(int what, long uptimeMillis) {
        Message msg = Message.obtain();
        msg.what = what;
        return sendMessageAtTime(msg, uptimeMillis);
    }

    public final boolean sendMessageDelayed(Message msg, long delayMillis) {
        if (delayMillis < 0) delayMillis = 0;
        return sendMessageAtTime(msg, SystemClock.uptimeMillis() + delayMillis);
    }

    public boolean sendMessageAtTime(Message msg, long uptimeMillis) {
        if (msg == null) throw new NullPointerException("msg == null");
        msg.target = this;
        msg.when = uptimeMillis;
        looper.enqueue(msg);
        return true;
    }

    public final boolean sendMessageAtFrontOfQueue(Message msg) {
        // 本实现没有「队首」优先级：按当前时间入队即可（消息队列按 when 排序）。
        return sendMessageAtTime(msg, SystemClock.uptimeMillis());
    }

    public final Message obtainMessage() {
        return Message.obtain(this);
    }

    public final Message obtainMessage(int what) {
        return Message.obtain(this, what);
    }

    public final Message obtainMessage(int what, Object obj) {
        return Message.obtain(this, what, obj);
    }

    public final Message obtainMessage(int what, int arg1, int arg2) {
        return Message.obtain(this, what, arg1, arg2);
    }

    public final Message obtainMessage(int what, int arg1, int arg2, Object obj) {
        return Message.obtain(this, what, arg1, arg2, obj);
    }

    public final boolean hasMessages(int what) {
        return looper.hasMessages(this, what, null);
    }

    public final boolean hasMessages(int what, Object object) {
        return looper.hasMessages(this, what, object);
    }

    public final void removeMessages(int what) {
        looper.removeMessages(this, what, null);
    }

    public final void removeMessages(int what, Object object) {
        looper.removeMessages(this, what, object);
    }

    public final void removeCallbacks(Runnable runnable) {
        looper.removeCallbacks(this, runnable);
    }

    public final void removeCallbacksAndMessages(Object token) {
        looper.removeCallbacksAndMessages(this, token);
    }

    private Message getPostMessage(Runnable runnable) {
        return Message.obtain(this, runnable);
    }
}
