using System.Diagnostics;
using VodBox.PluginHost;

// 手动原生冒烟；必须断言实际输出，不覆盖调用方的库路径。
var passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine($"PASS {++passed}: {name}");
}
void Equal(string want, string got)
{
    if (got != want) throw new InvalidOperationException($"Expected {want}, actual {got}");
}
using var engine = new QuickJsEngine();
await Check("sync eval and log", () =>
{
    Equal("7", engine.Evaluate("console.log('QuickJS smoke'); 1+2*3"));
    return Task.CompletedTask;
});
await Check("timer completion", async () =>
{
    await engine.EvaluateAsync("globalThis.done='no'; setTimeout(()=>{done='fired'},30)");
    Equal("fired", engine.Evaluate("done"));
});
await Check("promise chain and both resolver tables cleaned", async () =>
{
    await engine.EvaluateAsync("__hostapi.__host_async('setTimeout','20').then(v=>globalThis.chain='resolved:'+v)");
    Equal("resolved:", engine.Evaluate("chain"));
    Equal("0:0", engine.Evaluate("Object.values(__resolves).filter(x=>typeof x==='function').length+':'+Object.values(__rejects).filter(x=>typeof x==='function').length"));
});
await Check("dynamic and nested static module exports", async () =>
{
    engine.AddModule("mathlib", "export function twice(x){return x*2}; export default 'default-ok';");
    engine.AddModule("entry", "import d,{twice} from 'mathlib'; globalThis.mod=twice(21)+':'+d;");
    await engine.EvaluateAsync("import('entry')");
    Equal("42:default-ok", engine.Evaluate("mod"));
});
await Check("native loop cancellation within deadline", async () =>
{
    using var looping = new QuickJsEngine();
    using var ct = new CancellationTokenSource(150);
    var sw = Stopwatch.StartNew();
    try { await looping.EvaluateAsync("while(true){}", ct.Token); throw new Exception("loop was not canceled"); }
    catch (OperationCanceledException) { }
    if (sw.Elapsed > TimeSpan.FromSeconds(2)) throw new Exception($"cancellation took {sw.Elapsed}");
});
await Check("dispose during pending timer and late completion", async () =>
{
    var retiring = new QuickJsEngine();
    var work = retiring.EvaluateAsync("setTimeout(()=>globalThis.late=true,1000)");
    retiring.Dispose();
    try { await work; throw new Exception("disposed operation succeeded"); }
    catch (OperationCanceledException) { }
    catch (ObjectDisposedException) { }
    retiring.Dispose();
    await Task.Delay(1100);
});
await Check("microtask-only loop observes cancellation", async () =>
{
    using var looping = new QuickJsEngine();
    using var ct = new CancellationTokenSource(150);
    var sw = Stopwatch.StartNew();
    try
    {
        await looping.EvaluateAsync("function spin(){Promise.resolve().then(spin)}; spin()", ct.Token);
        throw new Exception("microtask loop was not canceled");
    }
    catch (OperationCanceledException) { }
    if (sw.Elapsed > TimeSpan.FromSeconds(2)) throw new Exception($"microtask cancellation took {sw.Elapsed}");
});
await Check("queued cancellation leaves active evaluation alive", async () =>
{
    using var serial = new QuickJsEngine();
    var first = serial.EvaluateAsync("globalThis.value='waiting'; setTimeout(()=>value='finished',250)");
    using var ct = new CancellationTokenSource(20);
    try { await serial.EvaluateAsync("value='wrong'", ct.Token); throw new Exception("queued evaluation succeeded"); }
    catch (OperationCanceledException) { }
    await first;
    Equal("finished", serial.Evaluate("value"));
});
await Check("asynchronous rejection is delivered and cleaned", async () =>
{
    await engine.EvaluateAsync("__hostapi.__host_async('missing-op','').catch(e=>globalThis.rejected=String(e))");
    if (!engine.Evaluate("rejected").Contains("missing-op")) throw new Exception("host rejection missing");
    Equal("0:0", engine.Evaluate("Object.keys(__resolves).length+':'+Object.keys(__rejects).length"));
});
await Check("synchronous req sends object data and decodes GBK", async () =>
{
    using var server = new LoopbackServer();
    var response = server.RespondAsync(async context =>
    {
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        Equal("a=x%26y&empty=", body);
        Equal("application/x-www-form-urlencoded", context.Request.ContentType!.Split(';')[0]);
        Equal("last-UA", context.Request.UserAgent!);
        context.Response.ContentType = "text/plain; charset=gbk";
        context.Response.Headers["X-Test"] = "present";
        var bytes = System.Text.CodePagesEncodingProvider.Instance.GetEncoding("gbk")!.GetBytes("中文");
        await context.Response.OutputStream.WriteAsync(bytes);
    });
    var payload = "{\"url\":\"" + server.Url + "\",\"obj\":{\"method\":\"POST\",\"data\":{\"a\":\"x&y\",\"empty\":\"\"},\"headers\":{\"User-Agent\":\"first-UA\",\"user-agent\":\"last-UA\"}}}";
    using var writerStream = new MemoryStream();
    using (var writer = new System.Text.Json.Utf8JsonWriter(writerStream)) writer.WriteStringValue(payload);
    var literal = System.Text.Encoding.UTF8.GetString(writerStream.ToArray());
    using var requestEngine = new QuickJsEngine();
    var result = await requestEngine.EvaluateAsync("__hostapi.__host('req'," + literal + ")");
    await response.WaitAsync(TimeSpan.FromSeconds(2));
    using var doc = System.Text.Json.JsonDocument.Parse(result);
    Equal("中文", doc.RootElement.GetProperty("content").GetString()!);
    Equal("present", doc.RootElement.GetProperty("headers").GetProperty("x-test").GetString()!);
});
await Check("caught host failure remains visible to managed caller", async () =>
{
    using var failing = new QuickJsEngine();
    try
    {
        await failing.EvaluateAsync("try { __hostapi.__host('req', '{\"url\":\"http://127.0.0.1/\",\"obj\":{\"data\":[]}}') } catch(e) {}; 'swallowed'");
        throw new Exception("host failure was swallowed");
    }
    catch (InvalidOperationException error) when (error.InnerException is InvalidDataException) { }
});
await Check("relative URL joining preserves scheme, query and dot segments", async () =>
{
    using var urls = new QuickJsEngine();
    Equal("http://cdn.example/b", await urls.EvaluateAsync("__hostapi.__host('joinUrl', JSON.stringify({base:'http://example.org/a',rel:'//cdn.example/b'}))"));
    Equal("https://example.org/a?x=1", await urls.EvaluateAsync("__hostapi.__host('joinUrl', JSON.stringify({base:'https://example.org/a?old=1',rel:'?x=1'}))"));
    Equal("https://example.org/b", await urls.EvaluateAsync("__hostapi.__host('joinUrl', JSON.stringify({base:'https://example.org/a/c',rel:'../b'}))"));
});
Console.WriteLine($"QUICKJS SMOKE {passed}/{passed}");
