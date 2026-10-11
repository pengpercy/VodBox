package android.database.sqlite;

/**
 * Android {@code SQLiteClosable} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 引用计数语义与 Android 一致：初次创建即持有 1 个引用；{@link #acquireReference()} 递增，
 * {@link #releaseReference()} 递减，归零时回调 {@link #onAllReferencesReleased()}；
 * {@link #close()} 等价于释放一个引用。
 */
public abstract class SQLiteClosable implements java.io.Closeable {

    private int references = 1;

    protected abstract void onAllReferencesReleased();

    /** 已废弃但仍在插件里出现：本实现与 {@link #releaseReference()} 等价。 */
    @Deprecated
    protected void onAllReferencesReleasedFromContainer() {
        onAllReferencesReleased();
    }

    public void acquireReference() {
        synchronized (this) {
            if (references <= 0) throw new IllegalStateException("attempt to re-open an already-closed object: " + this);
            references++;
        }
    }

    public void releaseReference() {
        boolean released;
        synchronized (this) {
            released = --references == 0;
        }
        if (released) onAllReferencesReleased();
    }

    @Deprecated
    public void releaseReferenceFromContainer() {
        boolean released;
        synchronized (this) {
            released = --references == 0;
        }
        if (released) onAllReferencesReleasedFromContainer();
    }

    @Override
    public void close() {
        releaseReference();
    }

    /** 供诊断/断言：当前引用计数。 */
    public int referenceCount() {
        synchronized (this) {
            return references;
        }
    }
}
