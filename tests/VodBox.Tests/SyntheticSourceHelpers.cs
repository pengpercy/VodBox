using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Tests;

internal static class SyntheticSourceHelpers
{
    /// <summary>记录请求并返回合成响应的 HTTP 委托。</summary>
    internal sealed class Recorder
    {
        private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<string>>? _respond;
        private readonly Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>>? _respondCt;
        private readonly Func<string, bool> _fail;
        public List<string> Urls { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<(string Url, IReadOnlyDictionary<string, string>? Headers)> Calls { get; } = [];

        public Recorder(Func<string, IReadOnlyDictionary<string, string>?, Task<string>> respond, Func<string, bool>? fail = null)
        {
            _respond = respond;
            _fail = fail ?? (_ => false);
        }

        public Recorder(Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<string>> respond, Func<string, bool>? fail = null)
        {
            _respondCt = respond;
            _fail = fail ?? (_ => false);
        }

        public Task<string> Fetch(string url, CancellationToken ct) => Fetch(url, null, ct);

        public Task<string> Fetch(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            Urls.Add(url);
            Calls.Add((url, headers));
            if (_fail(url)) throw new HttpRequestException("synthetic non-200");
            return _respondCt is not null ? _respondCt(url, headers, ct) : _respond!(url, headers);
        }

        public Task<string> Post(string url, string body, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            Urls.Add(url);
            Bodies.Add(body);
            if (_fail(url)) throw new HttpRequestException("synthetic non-200");
            return _respondCt is not null ? _respondCt(url, null, ct) : _respond!(url, null);
        }
    }

    /// <summary>固定合成响应的 HttpMessageHandler。</summary>
    internal sealed class StatusHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _run;
        public StatusHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) => _run = run;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _run(request, ct);
    }

    internal static SourceInfo Site(string key, string api, string? ext = null) =>
        new() { Key = key, Name = key, Api = api, Runtime = SourceRuntime.NativeSpider, Ext = ext };

    internal static CancellationToken CancelSoon(TimeSpan? delay = null, CancellationToken? parent = null)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(parent ?? default);
        source.CancelAfter(delay ?? TimeSpan.FromMilliseconds(40));
        return source.Token;
    }

    internal static TvBoxSite TvSite(string key, string api, string? ext)
    {
        var site = new TvBoxSite { Key = key, Name = key, Api = api, Type = 3 };
        if (ext is not null)
        {
            using var parsed = JsonDocument.Parse(EncodeJsonString(ext));
            site.Ext = parsed.RootElement.Clone();
        }
        return site;
    }

    /// <summary>把字符串编码为 JSON 字符串字面量，避免反射式 <see cref="JsonSerializer"/> 触发裁剪告警。</summary>
    private static string EncodeJsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char character in value)
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        return builder.Append('"').ToString();
    }
}
