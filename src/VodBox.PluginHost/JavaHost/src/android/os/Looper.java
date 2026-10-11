package android.os;

import java.util.Comparator;
import java.util.PriorityQueue;

/**
 * Android {@code Looper} 最小子集（VodBox 自有实现）。
 *
 * 每个 Looper 绑定一个线程，内部是一个按 {@code when} 排序的消息队列（{@link PriorityQueue}）。
 * 宿主的主 Looper 是一个 **daemon** 线程，按需创建（{@link #getMainLooper()}），
 * 因此不会阻止 JVM 退出。{@link #quit()} 丢弃未处理消息并让 {@link #loop()} 返回。
 */
public final class Looper {

    private static final ThreadLocal<Looper> THREAD_LOCAL = new ThreadLocal<>();
    private static volatile Looper mainLooper;

    private final Thread thread;
    private final PriorityQueue<Message> queue = new PriorityQueue<>(Comparator.comparingLong(message -> message.when));
    private boolean quitting;

    private Looper(Thread thread) {
        this.thread = thread;
    }

    public static void prepare() {
        if (THREAD_LOCAL.get() != null) throw new RuntimeException("每个线程只能有一个 Looper");
        THREAD_LOCAL.set(new Looper(Thread.currentThread()));
    }

    public static void prepareMainLooper() {
        prepare();
        synchronized (Looper.class) {
            mainLooper = myLooper();
            Looper.class.notifyAll();
        }
    }

    /** 主 Looper：首次调用时惰性创建一条 daemon 线程承载。 */
    public static Looper getMainLooper() {
        Looper existing = mainLooper;
        if (existing != null) return existing;
        synchronized (Looper.class) {
            if (mainLooper == null) {
                Thread thread = new Thread(() -> {
                    prepareMainLooper();
                    loop();
                }, "vodbox-main-looper");
                thread.setDaemon(true);
                thread.start();
                while (mainLooper == null) {
                    try {
                        Looper.class.wait();
                    } catch (InterruptedException interrupted) {
                        Thread.currentThread().interrupt();
                        break;
                    }
                }
            }
            return mainLooper;
        }
    }

    public static Looper myLooper() {
        return THREAD_LOCAL.get();
    }

    public static void loop() {
        Looper looper = myLooper();
        if (looper == null) throw new RuntimeException("当前线程没有 Looper（先调用 Looper.prepare()）");
        looper.run();
    }

    public Thread getThread() {
        return thread;
    }

    public boolean isCurrentThread() {
        return Thread.currentThread() == thread;
    }

    public void quit() {
        synchronized (this) {
            quitting = true;
            notifyAll();
        }
    }

    public void quitSafely() {
        quit();
    }

    void enqueue(Message message) {
        synchronized (this) {
            if (quitting) return;
            queue.add(message);
            notifyAll();
        }
    }

    void removeMessages(Handler handler, int what, Object object) {
        synchronized (this) {
            queue.removeIf(message -> message.target == handler && message.what == what
                    && (object == null || object.equals(message.obj)));
        }
    }

    void removeCallbacks(Handler handler, Runnable runnable) {
        synchronized (this) {
            queue.removeIf(message -> message.target == handler && message.callback == runnable);
        }
    }

    void removeCallbacksAndMessages(Handler handler, Object object) {
        synchronized (this) {
            queue.removeIf(message -> (handler == null || message.target == handler)
                    && (object == null || object.equals(message.obj)));
        }
    }

    boolean hasMessages(Handler handler, int what, Object object) {
        synchronized (this) {
            for (Message message : queue) {
                if (message.target == handler && message.what == what
                        && (object == null || object.equals(message.obj))) return true;
            }
            return false;
        }
    }

    private void run() {
        for (;;) {
            Message message;
            synchronized (this) {
                for (;;) {
                    if (quitting) return;
                    message = queue.peek();
                    if (message == null) {
                        try {
                            wait();
                        } catch (InterruptedException ignored) {
                            // 保持等待直到有消息或被 quit。
                        }
                        continue;
                    }
                    long now = SystemClock.uptimeMillis();
                    if (message.when <= now) {
                        queue.poll();
                        break;
                    }
                    try {
                        wait(Math.max(1L, message.when - now));
                    } catch (InterruptedException ignored) {
                        // 醒来后重新评估队列。
                    }
                }
            }
            dispatch(message);
        }
    }

    private static void dispatch(Message message) {
        Handler target = message.target;
        if (target != null) target.dispatchMessage(message);
        message.recycleUnchecked();
    }
}
