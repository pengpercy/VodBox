using System.Net;
using System.Net.Sockets;

internal sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Url { get; }

    public LoopbackServer()
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        Url = $"http://127.0.0.1:{number}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
    }

    public async Task RespondAsync(Func<HttpListenerContext, Task> respond)
    {
        var context = await _listener.GetContextAsync();
        try { await respond(context); }
        finally { context.Response.Close(); }
    }

    public void Dispose() => _listener.Close();
}
