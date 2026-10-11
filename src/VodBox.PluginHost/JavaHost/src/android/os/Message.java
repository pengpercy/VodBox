package android.os;

/**
 * Android {@code Message} 最小子集（VodBox 自有实现）。
 *
 * 可携带 {@code what}/{@code arg1}/{@code arg2}/{@code obj} 与一个 {@link Bundle}。
 * {@link #obtain} 系列在 Android 里从对象池取；本实现直接新建（语义等价，便于离线确定测试）。
 */
public final class Message {

    public int what;
    public int arg1;
    public int arg2;
    public Object obj;

    long when;
    Handler target;
    Runnable callback;
    private Bundle data;

    public Message() {
    }

    public static Message obtain() {
        return new Message();
    }

    public static Message obtain(Handler target) {
        Message message = new Message();
        message.target = target;
        return message;
    }

    public static Message obtain(Handler target, Runnable callback) {
        Message message = new Message();
        message.target = target;
        message.callback = callback;
        return message;
    }

    public static Message obtain(Handler target, int what) {
        Message message = new Message();
        message.target = target;
        message.what = what;
        return message;
    }

    public static Message obtain(Handler target, int what, Object obj) {
        Message message = obtain(target, what);
        message.obj = obj;
        return message;
    }

    public static Message obtain(Handler target, int what, int arg1, int arg2) {
        Message message = obtain(target, what);
        message.arg1 = arg1;
        message.arg2 = arg2;
        return message;
    }

    public static Message obtain(Handler target, int what, int arg1, int arg2, Object obj) {
        Message message = obtain(target, what, arg1, arg2);
        message.obj = obj;
        return message;
    }

    public Handler getTarget() {
        return target;
    }

    public void setTarget(Handler target) {
        this.target = target;
    }

    public Runnable getCallback() {
        return callback;
    }

    public void setCallback(Runnable callback) {
        this.callback = callback;
    }

    public long getWhen() {
        return when;
    }

    public void setData(Bundle data) {
        this.data = data;
    }

    public Bundle getData() {
        if (data == null) data = new Bundle();
        return data;
    }

    public Bundle peekData() {
        return data;
    }

    public void sendToTarget() {
        if (target == null) throw new IllegalStateException("Message 没有 target");
        target.sendMessage(this);
    }

    public void recycle() {
        recycleUnchecked();
    }

    void recycleUnchecked() {
        what = 0;
        arg1 = 0;
        arg2 = 0;
        obj = null;
        when = 0;
        target = null;
        callback = null;
        data = null;
    }

    @Override
    public String toString() {
        return "Message{what=" + what + ", arg1=" + arg1 + ", arg2=" + arg2 + ", obj=" + obj + "}";
    }
}
