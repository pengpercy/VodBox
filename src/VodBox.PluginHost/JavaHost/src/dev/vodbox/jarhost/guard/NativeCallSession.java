package dev.vodbox.jarhost.guard;

/**
 * 一次插件会话的**原生接线**：把插件里去 native 化后的 JNI 调用转发到**同一进程内**的 ARM64 模拟后端，
 * 由 {@link NativeMethodBridge} 作为静态门面持有。
 *
 * <p>实现方是同进程的 {@link DynarmicGuardBackend}（Dynarmic 真跑 guest 代码）。
 * 本接口只描述**契约**，不含任何实现——因此没有后端时调用方必须明确失败（{@link NativeGuards.UnsupportedLibrary}），
 * 绝不返回伪造的默认值。
 *
 * <p>方法语义：
 * <ul>
 *   <li>{@link #invoke}：调用 guest 导出的 native 方法入口。{@code receiver} 为 null 表示静态方法，
 *       否则为实例方法；{@code nameAndDescriptor} 是「方法名 + JNI 描述符」，如 {@code decrypt(Ljava/lang/String;)Ljava/lang/String;}。</li>
 *   <li>{@link #load}/{@link #loadLibrary}：装载插件归档里的 ARM64 库（guest 只进模拟器，绝不进宿主进程）。</li>
 *   <li>{@link #hostPath}：把 guest 文件系统里写的路径映射回宿主（guest 原生代码写文件的场景）。</li>
 * </ul>
 */
public interface NativeCallSession {

    /** 调用 guest 导出的 native 方法入口（静态或实例）。 */
    Object invoke(String owner, String nameAndDescriptor, Object receiver, Object[] arguments);

    /** 装载归档里某个具体路径的 ARM64 库（对齐插件自解包后再装载的用法）。 */
    void load(String path);

    /** 按库名装载归档里的 ARM64 库（对齐按名装载的用法）。 */
    void loadLibrary(String name);

    /** guest 写的路径 → 宿主可读路径；无映射时原样返回。 */
    String hostPath(String path);

    /** 后端是否已就绪（真加载过 guest 库）。 */
    boolean available();

    /** 诊断描述（库名/是否执行过 JNI_OnLoad 等）。 */
    String describe();
}
