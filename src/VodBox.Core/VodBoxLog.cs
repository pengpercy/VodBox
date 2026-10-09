using System.Text;

namespace VodBox.Core;

/// <summary>
/// 轻量诊断日志：无第三方依赖、AOT/裁剪安全（不使用反射与配置文件）。
/// 为什么不引入 NLog/Serilog 或 Microsoft.Extensions.Logging：
/// 1) 本项目 Release 走 NativeAOT + 全量裁剪且关闭反射序列化，NLog/Serilog 的 XML/约定式配置
///    与布局渲染依赖反射，需额外 AOT 适配并增大包体；
/// 2) Microsoft.Extensions.Logging 虽然 AOT 安全，但内置 provider 没有文件 sink，
///    写文件仍需自实现 ILoggerProvider，而本项目也没有 DI 容器（AppServices 是手写定位器），
///    把 ILoggerFactory 灌进每一层反而更重；
/// 3) 最有价值的日志来自最底层（Playback.Mpv 与全局异常钩子），那里本来就需要静态入口。
/// 因此这里只做“写文本行 + 按天分割 + 体积上限”，放在 Core 让所有层都能调用。
/// 设计约束：任何写日志的失败都不得影响播放，因此全部入口都吞掉异常。
/// </summary>
public static class VodBoxLog
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string[] SensitiveKeys =
        ["key", "key2", "authid", "auth", "token", "password", "pwd", "sign", "apikey", "api_key", "secret"];

    private static string? _directory;
    private static string? _file;
    private static StreamWriter? _writer;
    private static DateOnly _day;
    private static long _bytes;

    /// <summary>是否启用。未初始化时为 false，调用点无需判空。</summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>详细模式：额外记录 mpv 原始日志与逐帧级事件（环境变量 VODBOX_LOG_VERBOSE=1 开启）。</summary>
    public static bool Verbose { get; private set; }

    /// <summary>日志目录；未初始化时为 null。</summary>
    public static string? LogDirectory => _directory;

    public static string? CurrentFile { get { lock (Gate) return _file; } }

    /// <summary>初始化到 &lt;dataDirectory&gt;/logs。重复调用会切换到新目录。</summary>
    public static void Initialize(string dataDirectory)
    {
        try
        {
            Verbose = Environment.GetEnvironmentVariable("VODBOX_LOG_VERBOSE") == "1";
            lock (Gate)
            {
                CloseWriter();
                var directory = Path.Combine(dataDirectory, "logs");
                Directory.CreateDirectory(directory);
                _directory = directory;
                IsEnabled = true;
                OpenWriter(DateTimeOffset.Now);
            }
            Info("app", $"日志已启动 verbose={Verbose} file={CurrentFile}");
        }
        catch
        {
            IsEnabled = false;
        }
    }

    /// <summary>开关日志；关闭后不再写入（用于设置页切换，避免日常噪音）。</summary>
    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            lock (Gate) { IsEnabled = false; CloseWriter(); }
            return;
        }
        lock (Gate) { IsEnabled = _directory is not null; }
        Info("app", "日志已启用");
    }

    public static void Info(string area, string message) => Write("INFO ", area, message);

    public static void Warn(string area, string message) => Write("WARN ", area, message);

    public static void Trace(string area, string message)
    {
        if (Verbose) Write("TRACE", area, message);
    }

    public static void Error(string area, string message, Exception? error = null) =>
        Write("ERROR", area, error is null ? message : $"{message} :: {error.GetType().Name}: {error.Message}");

    /// <summary>结构化事件：按 key=value 顺序追加，值自动脱敏。</summary>
    public static void Event(string area, string name, params (string Key, string? Value)[] fields)
    {
        if (!IsEnabled && !Verbose) return;
        var builder = new StringBuilder(name);
        foreach (var (key, value) in fields)
        {
            if (value is null) continue;
            builder.Append(' ').Append(key).Append('=').Append(RedactValue(key, value));
        }
        Write("INFO ", area, builder.ToString());
    }

    /// <summary>读取末尾若干行，供设置页预览。</summary>
    public static IReadOnlyList<string> ReadTail(int lines)
    {
        try
        {
            var file = CurrentFile;
            if (file is null || !File.Exists(file)) return [];
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new Queue<string>(Math.Max(1, lines));
            while (reader.ReadLine() is { } line)
            {
                if (buffer.Count == Math.Max(1, lines)) buffer.Dequeue();
                buffer.Enqueue(line);
            }
            return buffer.ToArray();
        }
        catch { return []; }
    }

    /// <summary>删除全部日志文件（含轮转的历史文件）。</summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                var directory = _directory;
                CloseWriter();
                if (directory is not null && Directory.Exists(directory))
                {
                    foreach (var path in Directory.EnumerateFiles(directory, "vodbox-*.log*")) File.Delete(path);
                }
                if (IsEnabled) OpenWriter(DateTimeOffset.Now);
            }
            Info("app", "日志已清空");
        }
        catch { }
    }

    private static void Write(string level, string area, string message)
    {
        if (!IsEnabled) return;
        try
        {
            var stamp = DateTimeOffset.Now;
            var line = $"{stamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {area,-8} {Redact(message)}";
            lock (Gate)
            {
                if (_writer is null || _day != DateOnly.FromDateTime(stamp.DateTime))
                {
                    OpenWriter(stamp);
                }
                else if (_bytes >= MaxBytes)
                {
                    RotateCurrent(stamp);
                }
                _writer!.WriteLine(line);
                _bytes += line.Length + 1;
            }
        }
        catch
        {
            // 日志永远不能让播放失败。
        }
    }

    private static void OpenWriter(DateTimeOffset stamp)
    {
        _day = DateOnly.FromDateTime(stamp.DateTime);
        _file = Path.Combine(_directory!, $"vodbox-{_day:yyyyMMdd}.log");
        _writer = new StreamWriter(new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            AutoFlush = true,
        };
        _bytes = new FileInfo(_file).Length;
    }

    /// <summary>超过体积上限时把当前文件改名为 .1（保留一代），保证长时间运行不会无限增长。</summary>
    private static void RotateCurrent(DateTimeOffset stamp)
    {
        CloseWriter();
        if (_file is not null && File.Exists(_file))
        {
            var previous = _file + ".1";
            try { if (File.Exists(previous)) File.Delete(previous); File.Move(_file, previous); }
            catch { /* 改名失败则继续追加原文件 */ }
        }
        OpenWriter(stamp);
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>
    /// 脱敏：遮蔽查询串里 key/authid/token 一类的参数值，避免日志泄露源站凭据。
    /// 公开供调用方在自行拼接日志时先调用（例如把整条播放地址写进日志之前）。
    /// </summary>
    public static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message) || !message.Contains('=')) return message;
        var builder = new StringBuilder(message.Length);
        var index = 0;
        while (index < message.Length)
        {
            var equals = message.IndexOf('=', index);
            if (equals < 0) { builder.Append(message, index, message.Length - index); break; }
            var start = equals - 1;
            while (start >= index && (char.IsLetterOrDigit(message[start]) || message[start] is '_' or '-' or '.')) start--;
            var name = message[(start + 1)..equals];
            builder.Append(message, index, equals + 1 - index);
            var end = equals + 1;
            // '?' 也必须算分隔符：否则 "uri=http://x/a?key=v" 会把整个地址当作 uri 的值，
            // 内层查询串里的密钥就漏掉了。
            while (end < message.Length && message[end] is not ('&' or '?' or ' ' or '"' or '\'' or ',' or ';')) end++;
            builder.Append(IsSensitive(name) ? "***" : message[(equals + 1)..end]);
            index = end;
        }
        return builder.ToString();
    }

    private static string RedactValue(string key, string value) =>
        IsSensitive(key) ? "***" : Redact(value);

    private static bool IsSensitive(string name) =>
        SensitiveKeys.Contains(name.Trim().ToLowerInvariant());
}
