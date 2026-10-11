namespace VodBox.PluginHost.JarPlugin;

/// <summary>一个宿主会话的启动上下文（每个源一个进程，实现站点隔离）。</summary>
public sealed record JarPluginSessionDescriptor(string SourceKey, JarPluginSourceLayout Layout, string JarPath, string WorkingDirectory);

/// <summary>
/// 宿主命令通道抽象：真实实现是外部进程，测试用假实现注入超时/退出/失败各分支，
/// 使生命周期逻辑无需真的启动 JVM。
/// </summary>
public interface IJarPluginHostChannel : IAsyncDisposable
{
    bool IsRunning { get; }

    /// <summary>启动并等待就绪；返回 ready 信封（可能携带错误，由调用方分类）。</summary>
    Task<JarPluginEnvelope> StartAsync(CancellationToken ct);

    /// <summary>发送一条命令并等待其响应文件。超时/取消由调用方的令牌控制。</summary>
    Task<JarPluginEnvelope> SendAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct);
}

/// <summary>创建通道的工厂；真实实现返回外部进程通道，测试返回假通道。</summary>
public interface IJarPluginHostChannelFactory
{
    IJarPluginHostChannel Create(JarPluginSessionDescriptor descriptor);
}
