package android.database.sqlite;

/**
 * Android {@code SQLiteTransactionListener} 最小子集（VodBox 自有实现）。
 * 与 Android 一致：事务开始/提交/回滚时回调；{@code beginTransactionWithListener} 传入的监听器
 * 只对外层事务触发一次。
 */
public interface SQLiteTransactionListener {

    void onBegin();

    void onCommit();

    void onRollback();
}
