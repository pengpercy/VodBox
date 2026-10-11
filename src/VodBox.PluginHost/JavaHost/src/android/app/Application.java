package android.app;

import android.content.Context;
import android.content.ContextWrapper;

/**
 * Android {@code Application} 最小子集（VodBox 自有实现）。
 *
 * 宿主在插件 {@code init} 前构造一个 {@code Application}，把真实目录/偏好等实现附着一个
 * {@code Context} 上去（{@link #attachBaseContext(Context)}）。继承关系与 Android 一致：
 * {@code Application} → {@code ContextWrapper} → {@code Context}，因此插件对
 * {@code ContextWrapper}/{@code Application} 的 {@code instanceof}/强制转换都能成立。
 */
public class Application extends ContextWrapper {

    public Application() {
        super(null);
    }

    /**
     * 加宽为 public 以便宿主（不同包）在构造后附着具体实现；同时把自身登记给
     * {@link ActivityThread}，让插件经 {@code ActivityThread.currentApplication()} 拿到的是
     * 这个真实实例，而不是空壳或 {@code null}。
     */
    @Override
    public void attachBaseContext(Context base) {
        super.attachBaseContext(base);
        ActivityThread.attach(this);
    }

    /** Android 契约：应用上下文本体即 Application。 */
    @Override
    public Context getApplicationContext() {
        return this;
    }
}
