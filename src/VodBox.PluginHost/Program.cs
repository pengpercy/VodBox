using System.Text.Json;
using VodBox.Core;
using VodBox.PluginHost;

if (args.Length != 1 || !File.Exists(args[0])) { Console.Error.WriteLine("Usage: VodBox.PluginHost <plugin.js>"); return 2; }
using var engine = new QuickJsEngine();
engine.Evaluate("globalThis.console = { log: (...a) => __host('log', a.join(' ')), error: (...a) => __host('log', a.join(' ')) }; globalThis.vodbox = { apiVersion: 1, fetchText: (url) => __host('fetch', url), sha256: (s) => __host('sha256', s) };");
engine.Evaluate(File.ReadAllText(args[0]));
string? line;
while ((line = Console.ReadLine()) is not null)
{
    long id = 0;
    try
    {
        var request = JsonSerializer.Deserialize(line, VodBoxJson.Default.RpcRequest) ?? throw new InvalidDataException("Empty request");
        id = request.RequestId;
        if (request.ApiVersion != 1) throw new InvalidDataException("Unsupported apiVersion");
        string method = JsonSerializer.Serialize(request.Method, VodBoxJson.Default.String);
        engine.Evaluate($"globalThis.__reply = null; Promise.resolve().then(() => {{ const f = VodBoxProvider[{method}]; if (!f) throw new Error('Unknown method'); return f.call(VodBoxProvider, {request.Params.GetRawText()}); }}).then(r => globalThis.__reply = JSON.stringify({{result:r ?? {{}}}}), e => globalThis.__reply = JSON.stringify({{error:String(e)}}));");
        string reply = engine.Evaluate("__reply === null ? '' : __reply");
        if (reply.Length == 0) throw new TimeoutException("QuickJS promise did not settle");
        using var response = JsonDocument.Parse(reply);
        Console.WriteLine(JsonSerializer.Serialize(new RpcResponse(1, id,
            response.RootElement.TryGetProperty("result", out var result) ? result.Clone() : Empty(),
            response.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null), VodBoxJson.Default.RpcResponse));
    }
    catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new RpcResponse(1, id, Empty(), ex.Message), VodBoxJson.Default.RpcResponse)); }
}
return 0;
static JsonElement Empty() => JsonSerializer.SerializeToElement(new ScriptParams(), VodBoxJson.Default.ScriptParams);
