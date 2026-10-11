using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace VodBox.PluginHost.JarPlugin;

/// <summary>把 JAR 引用落到本地文件（下载或直接使用本地路径），返回可交给宿主的绝对路径。</summary>
public interface IJarPluginArtifactStore
{
    Task<string> AcquireAsync(JarReference jar, CancellationToken ct);
}

/// <summary>
/// 宿主运行时：按 <c>站点 + JAR + ext</c> 维护独立会话（站点隔离），负责空闲回收与上限控制。
/// 只有 <see cref="TryCreate"/> 成功（找到 Java 与宿主 JAR）才存在实例；否则 JAR 源必须保持禁用。
/// </summary>
public sealed class JarPluginRuntime : IAsyncDisposable
{
    private readonly IJarPluginHostChannelFactory _factory;
    private readonly JarPluginHostSettings _settings;
    private readonly IJarPluginArtifactStore _artifacts;
    private readonly ConcurrentDictionary<string, JarPluginSession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public JarPluginRuntime(IJarPluginHostChannelFactory factory, JarPluginHostSettings settings, IJarPluginArtifactStore artifacts)
    {
        _factory = factory;
        _settings = settings;
        _artifacts = artifacts;
    }

    /// <summary>
    /// 宿主是否可用（Java + 宿主 JAR 齐备）。不可用时不得创建 JAR 源。
    /// <paramref name="artifacts"/> 为 null 时使用默认构件存储：HTTP 落地 + DEX→JVM 转换缓存
    /// （含 DEX 的归档会先转换再交给宿主，转换失败按 <c>dex_*</c> 明确分类）。
    /// </summary>
    public static bool TryCreate(IJarPluginArtifactStore? artifacts, out JarPluginRuntime? runtime, string? workRoot = null)
    {
        runtime = null;
        if (!JarPluginHostLocator.TryResolve(out var settings, workRoot) || settings is null) return false;
        runtime = new JarPluginRuntime(new JarPluginProcessChannelFactory(settings), settings, artifacts ?? JarPluginArtifactStores.Create(settings));
        return true;
    }

    public int ActiveSessions => _sessions.Count;

    /// <summary>向某个源发送一条命令。</summary>
    public async Task<JarPluginEnvelope> RequestAsync(JarPluginSourceLayout layout, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var jar = layout.EffectiveJar ?? throw new JarPluginException(JarPluginErrors.PluginClassMissing, JarPluginFailureKind.Unsupported,
            "该站点没有可用的插件 JAR（site.jar 与 config.spider 均为空）。");
        string jarPath;
        try
        {
            jarPath = await _artifacts.AcquireAsync(jar, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            throw new JarPluginException(JarPluginErrors.DownloadFailed, JarPluginFailureKind.Temporary, $"插件 JAR 获取失败：{error.Message}", error);
        }
        var key = layout.SessionKey + '\u0000' + jarPath;
        Evict(key);
        var session = _sessions.GetOrAdd(key, _ => new JarPluginSession(
            new JarPluginSessionDescriptor(layout.SourceKey, layout, jarPath, WorkDirectory(key)), _factory, _settings));
        return await session.RequestAsync(parameters, ct).ConfigureAwait(false);
    }

    private void Evict(string incomingKey)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, session) in _sessions)
        {
            if (key == incomingKey || session.IsBusy) continue;
            if (session.IsIdle(now))
            {
                if (_sessions.TryRemove(key, out var removed)) _ = removed.DisposeAsync();
            }
        }
        while (_sessions.Count >= Math.Max(1, _settings.MaxSessions))
        {
            var victim = _sessions.Where(pair => pair.Key != incomingKey && !pair.Value.IsBusy)
                .OrderBy(pair => pair.Value.LastUse).FirstOrDefault();
            if (victim.Key is null) break;
            if (_sessions.TryRemove(victim.Key, out var removed)) _ = removed.DisposeAsync();
        }
    }

    private string WorkDirectory(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(_settings.WorkRoot, "sessions", hash);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
        _sessions.Clear();
    }
}
