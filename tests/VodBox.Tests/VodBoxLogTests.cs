using VodBox.Core;
using Xunit;

namespace VodBox.Tests;

public class VodBoxLogTests
{
    [Fact]
    public void RedactsSecretQueryParametersButKeepsOthers()
    {
        // 免费 IPTV 源常把密钥放在查询串里，日志不能泄露这些值。
        var line = "uri=http://host:9901/a.m3u8?key=txiptv&playlive=1&authid=0&token=abc";
        var redacted = VodBoxLog.Redact(line);
        Assert.DoesNotContain("txiptv", redacted);
        Assert.DoesNotContain("authid=0", redacted);
        Assert.DoesNotContain("token=abc", redacted);
        Assert.Contains("key=***", redacted);
        Assert.Contains("authid=***", redacted);
        Assert.Contains("token=***", redacted);
        // 非敏感参数必须保留，否则日志就没有排查价值了。
        Assert.Contains("playlive=1", redacted);
        Assert.Contains("http://host:9901/a.m3u8", redacted);
    }

    [Fact]
    public void RedactLeavesTextWithoutAssignmentsUntouched()
    {
        Assert.Equal("没有等号的普通文本", VodBoxLog.Redact("没有等号的普通文本"));
        Assert.Equal("", VodBoxLog.Redact(""));
    }

    [Fact]
    public void WritesAndReadsBackTail()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vodbox-log-{Guid.NewGuid():N}");
        try
        {
            VodBoxLog.Initialize(directory);
            Assert.True(VodBoxLog.IsEnabled);
            VodBoxLog.Info("test", "第一行");
            VodBoxLog.Event("test", "事件", ("channels", "52"), ("key", "secret-value"));
            var tail = VodBoxLog.ReadTail(20);
            Assert.NotEmpty(tail);
            Assert.Contains(tail, line => line.Contains("第一行"));
            // 结构化事件的键值同样脱敏。
            Assert.Contains(tail, line => line.Contains("channels=52"));
            Assert.Contains(tail, line => line.Contains("key=***"));
            Assert.DoesNotContain(tail, line => line.Contains("secret-value"));
            Assert.True(File.Exists(VodBoxLog.CurrentFile));
        }
        finally
        {
            VodBoxLog.SetEnabled(false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisableStopsWritingAndClearRemovesFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vodbox-log-{Guid.NewGuid():N}");
        try
        {
            VodBoxLog.Initialize(directory);
            VodBoxLog.Info("test", "写入一行");
            var file = VodBoxLog.CurrentFile!;
            Assert.True(new FileInfo(file).Length > 0);
            VodBoxLog.SetEnabled(false);
            var length = new FileInfo(file).Length;
            VodBoxLog.Info("test", "关闭后不应写入");
            Assert.Equal(length, new FileInfo(file).Length);
            VodBoxLog.SetEnabled(true);
            VodBoxLog.Clear();
            var remaining = Directory.GetFiles(directory, "vodbox-*.log*");
            Assert.All(remaining, path => Assert.DoesNotContain("关闭后不应写入", File.ReadAllText(path)));
        }
        finally
        {
            VodBoxLog.SetEnabled(false);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WritingBeforeInitializeIsSafeAndSilent()
    {
        // 未初始化（例如单元测试或极早启动阶段）不得抛异常。
        VodBoxLog.Info("test", "初始化前写入");
        VodBoxLog.Error("test", "初始化前错误", new InvalidOperationException("boom"));
        Assert.Empty(VodBoxLog.ReadTail(5));
    }
}
