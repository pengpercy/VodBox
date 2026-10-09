using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VodBox.Infrastructure;

/// <summary>显式启动的回环遥控入口。只监听127.0.0.1，校验Host/Origin，拒绝跨站请求。</summary>
public sealed class LocalControlServer : IAsyncDisposable
{
    private WebApplication? _app;
    public string? PairingCode { get; private set; }
    public bool LanEnabled { get; private set; }
    public IReadOnlyList<string> LanAddresses { get; private set; }=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,DateTimeOffset> _sessions=new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,(int Count,DateTimeOffset Since)> _pairAttempts=new();
    public MediaProxy Proxy { get; }
    public string RegisterMedia(string url, IReadOnlyDictionary<string, string>? headers = null) => Address is null
        ? throw new InvalidOperationException("请先启动本机服务。") : Address + Proxy.Register(url, headers);
    public string RegisterCachedMedia(string url,IReadOnlyDictionary<string,string>? headers=null)=>Address is null
        ?throw new InvalidOperationException("请先启动本机服务。"):Address+Proxy.Register(url,headers).Replace("/proxy/","/cache/",StringComparison.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    public string? Address { get; private set; }
    private readonly string? _uploadDirectory;
    private readonly int _maxUploadBytes;
    private readonly SemaphoreSlim _uploads=new(1,1);
    public LocalControlServer(string? uploadDirectory=null,int maxUploadBytes=64*1024*1024)
    {
        _uploadDirectory=uploadDirectory;_maxUploadBytes=Math.Clamp(maxUploadBytes,1,64*1024*1024);
        Proxy=new MediaProxy(uploadDirectory is null?null:Path.Combine(Path.GetDirectoryName(Path.GetFullPath(uploadDirectory))!,"media-cache"));
    }

    public async Task ClearInboxAsync(CancellationToken ct=default)
    {
        await _uploads.WaitAsync(ct);
        try
        {
            if(_uploadDirectory is null||!Directory.Exists(_uploadDirectory))return;
            foreach(var path in Directory.EnumerateFiles(_uploadDirectory))
            {
                ct.ThrowIfCancellationRequested();
                var name=Path.GetFileNameWithoutExtension(path);
                // 只删除服务自己创建的随机名文件，不清用户手动放入的其他文件。
                if(Guid.TryParseExact(name,"N",out _))File.Delete(path);
            }
        }
        finally{_uploads.Release();}
    }

    public async Task StartAsync(Func<string, Task> action, Func<string, Task> play, int port = 0, CancellationToken ct = default,bool allowLan=false,Func<string,double,Task>? adjust=null,Func<string,Task>? configure=null,Func<Task<VodBox.Core.RemotePlaybackStatus>>? status=null)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
        if (_app is not null)
        {if(LanEnabled!=allowLan)throw new InvalidOperationException("请先关闭现有服务，再切换监听模式。");return;}
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Limits.MaxRequestBodySize=_maxUploadBytes;
            options.Listen(allowLan?System.Net.IPAddress.Any:System.Net.IPAddress.Loopback, port);
        });
        PairingCode=allowLan?System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000,1000000).ToString():null;
        var pairingExpires=DateTimeOffset.UtcNow.AddMinutes(10);
        var allowedHosts=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"127.0.0.1"};
        if(allowLan)
            foreach(var network in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(network=>network.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up))
                foreach(var address in network.GetIPProperties().UnicastAddresses.Where(address=>address.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork))allowedHosts.Add(address.Address.ToString());
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!allowedHosts.Contains(context.Request.Host.Host) ||
                context.Request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != "http://" + context.Request.Host ||
                context.Request.Method == "POST" && context.Request.Headers["X-VodBox-Control"] != "1")
            { context.Response.StatusCode = 403; return; }
            var path=context.Request.Path.Value??"";
            var localProxy=System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress??System.Net.IPAddress.None)&&(path.StartsWith("/proxy/")||path.StartsWith("/cache/"));
            if(allowLan&&path!="/"&&path!="/pair"&&!localProxy)
            {
                var token=context.Request.Cookies["vodbox-session"];
                if(token is null||!_sessions.TryGetValue(token,out var expiry)||expiry<=DateTimeOffset.UtcNow){context.Response.StatusCode=401;return;}
            }
            await next();
        });
        app.MapPost("/pair",async context=>
        {
            if(!allowLan||PairingCode is null||DateTimeOffset.UtcNow>pairingExpires){context.Response.StatusCode=403;return;}
            var ip=context.Connection.RemoteIpAddress?.ToString()??"unknown";
            var attempts=_pairAttempts.AddOrUpdate(ip,_=>(1,DateTimeOffset.UtcNow),(_,old)=>DateTimeOffset.UtcNow-old.Since>TimeSpan.FromMinutes(10)?(1,DateTimeOffset.UtcNow):(old.Count+1,old.Since));
            if(attempts.Count>5){context.Response.StatusCode=429;return;}
            var supplied=context.Request.Query["code"].ToString();
            if(supplied.Length!=PairingCode.Length||!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied),System.Text.Encoding.UTF8.GetBytes(PairingCode)))
            {context.Response.StatusCode=403;return;}
            if(_sessions.Count>=32){context.Response.StatusCode=429;return;}
            _pairAttempts.TryRemove(ip,out _);
            foreach(var stale in _sessions.Where(session=>session.Value<=DateTimeOffset.UtcNow).Select(session=>session.Key).ToArray())_sessions.TryRemove(stale,out _);
            var token=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            _sessions[token]=DateTimeOffset.UtcNow.AddHours(4);
            context.Response.Cookies.Append("vodbox-session",token,new CookieOptions{HttpOnly=true,SameSite=SameSiteMode.Strict,MaxAge=TimeSpan.FromHours(4),Path="/"});
            context.Response.StatusCode=204;await Task.CompletedTask;
        });
        app.MapPost("/unpair",context=>
        {
            var token=context.Request.Cookies["vodbox-session"];
            if(token is not null)_sessions.TryRemove(token,out _);
            context.Response.Cookies.Delete("vodbox-session",new CookieOptions{Path="/",SameSite=SameSiteMode.Strict,HttpOnly=true});
            context.Response.StatusCode=204;return Task.CompletedTask;
        });
        app.MapGet("/proxy/{id}", async (HttpContext context) =>
        {
            try { await Proxy.ServeAsync(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or NotSupportedException or System.Xml.XmlException or UriFormatException or TaskCanceledException)
            {
                if (!context.Response.HasStarted) { context.Response.Clear(); context.Response.StatusCode = 502; }
                else context.Abort();
            }
        });
        app.MapGet("/cache/{id}",async(HttpContext context)=>
        {
            try { await Proxy.ServeCachedAsync(context); }
            catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){}
            catch(Exception error)when(error is HttpRequestException or InvalidDataException or IOException or TaskCanceledException)
            {if(!context.Response.HasStarted){context.Response.Clear();context.Response.StatusCode=502;}else context.Abort();}
        });
        app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(Page, context.RequestAborted);
        });
        app.MapGet("/status",async context=>
        {
            if(status is null){context.Response.StatusCode=404;return;}
            var snapshot=await status();
            context.Response.ContentType="application/json; charset=utf-8";
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(snapshot,VodBox.Core.Json.TypeInfo<VodBox.Core.RemotePlaybackStatus>()),context.RequestAborted);
        });
        app.MapPost("/action", async context =>
        {
            var command = context.Request.Query["command"].ToString();
            if (command is not ("toggle" or "stop" or "previous" or "next" or "back" or "forward"))
            { context.Response.StatusCode = 400; return; }
            await action(command); context.Response.StatusCode = 204;
        });
        app.MapPost("/adjust",async context=>
        {
            var command=context.Request.Query["command"].ToString();
            var text=context.Request.Query["value"].ToString();
            if(adjust is null){context.Response.StatusCode=404;return;}
            if(!double.TryParse(text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||
               command switch {"volume"=>value<0||value>100,"rate"=>value<.25||value>4,"seek"=>value<0||value>86400,_=>true})
            {context.Response.StatusCode=400;return;}
            await adjust(command,value);context.Response.StatusCode=204;
        });
        app.MapPost("/upload",async context=>
        {
            if(_uploadDirectory is null){context.Response.StatusCode=404;return;}
            var name=context.Request.Query["name"].ToString();
            var extension=Path.GetExtension(name).ToLowerInvariant();
            if(extension is not (".mp4" or ".mkv" or ".webm" or ".mp3" or ".m4a" or ".wav" or ".ts")||name!=Path.GetFileName(name)||name.Contains('\\'))
            {context.Response.StatusCode=400;return;}
            if(context.Request.ContentLength>_maxUploadBytes){context.Response.StatusCode=413;return;}
            await _uploads.WaitAsync(context.RequestAborted);
            try
            {
            Directory.CreateDirectory(_uploadDirectory);
            if(new DirectoryInfo(_uploadDirectory).EnumerateFiles().Sum(file=>file.Length)>=256L*1024*1024){context.Response.StatusCode=429;return;}
            if(Directory.EnumerateFiles(_uploadDirectory).Take(101).Count()>=100){context.Response.StatusCode=429;return;}
            var path=Path.Combine(_uploadDirectory,Guid.NewGuid().ToString("N")+extension);
            try
            {
                await using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,true))
                {
                    var buffer=new byte[8192];long total=0;int count;
                    while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0)
                    {
                        total+=count;
                        if(total>_maxUploadBytes||new DirectoryInfo(_uploadDirectory).EnumerateFiles().Where(file=>file.FullName!=path).Sum(file=>file.Length)+total>256L*1024*1024){context.Response.StatusCode=413;return;}
                        await output.WriteAsync(buffer.AsMemory(0,count),context.RequestAborted);
                    }
                    if(total==0){context.Response.StatusCode=400;return;}
                }
                context.Response.ContentType="text/plain; charset=utf-8";
                await context.Response.WriteAsync(Path.GetFileName(path),context.RequestAborted);
                path="";
            }
            catch(Microsoft.AspNetCore.Http.BadHttpRequestException error)when(error.StatusCode==413)
            {context.Response.StatusCode=413;}
            finally{if(path.Length>0&&File.Exists(path))File.Delete(path);}
            }
            finally{_uploads.Release();}
        });
        app.MapPost("/config",async context=>
        {
            if(configure is null){context.Response.StatusCode=404;return;}
            var value=context.Request.Query["url"].ToString();
            if(value.Length>8192||!Uri.TryCreate(value,UriKind.Absolute,out var address)||address.Scheme is not ("http" or "https"))
            {context.Response.StatusCode=400;return;}
            await configure(address.AbsoluteUri);context.Response.StatusCode=204;
        });
        app.MapPost("/media", async context =>
        {
            var url = context.Request.Query["url"].ToString();
            if (url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            { context.Response.StatusCode = 400; return; }
            await play(uri.AbsoluteUri); context.Response.StatusCode = 204;
        });
        try
        {
            await app.StartAsync(ct);
            _app = app;LanEnabled=allowLan;
            var bound=app.Urls.Single();
            var actualPort=new Uri(bound).Port;
            Address="http://127.0.0.1:"+actualPort;
            LanAddresses=allowLan?allowedHosts.Where(host=>host!="127.0.0.1").Select(host=>"http://"+host+":"+actualPort).ToArray():[];
        }
        catch
        {
            PairingCode=null;LanAddresses=[];LanEnabled=false;_sessions.Clear();_pairAttempts.Clear();
            await app.DisposeAsync();throw;
        }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
        var app = _app; _app = null; Address = null;LanEnabled=false; PairingCode=null;LanAddresses=[];_sessions.Clear();_pairAttempts.Clear();Proxy.Clear();
        if (app is null) return;
        await app.StopAsync(); await app.DisposeAsync();
        }
        finally { _lifecycle.Release(); }
    }

    private const string Page = """
    <meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>VodBox 本地遥控</title>
    <style>body{background:#181818;color:#eee;font:16px system-ui;max-width:650px;margin:40px auto;padding:20px}button,input{padding:12px;margin:6px;border-radius:8px}input{width:90%}</style>
    <h1>VodBox 遥控</h1><p id="playing"></p><p>局域网模式请先输入桌面显示的配对码。仅在可信网络使用。</p><input id="code" placeholder="配对码" maxlength="6"><button onclick="pair()">配对</button><button onclick="req('/unpair')">解除配对</button>
    <button onclick="act('toggle')">播放/暂停</button><button onclick="act('stop')">停止</button>
    <button onclick="act('previous')">上一集</button><button onclick="act('next')">下一集</button>
    <button onclick="act('back')">后退10秒</button><button onclick="act('forward')">前进10秒</button>
    <p>音量 <input id="volume" type="number" min="0" max="100" value="80"><button onclick="adjust('volume')">设置音量</button></p>
    <p>倍速 <input id="rate" type="number" min="0.25" max="4" step="0.25" value="1"><button onclick="adjust('rate')">设置倍速</button></p>
    <p>跳转秒数 <input id="seek" type="number" min="0" max="86400" value="0"><button onclick="adjust('seek')">跳转</button></p>
    <input type="file" id="file" accept="video/*,audio/*"><button onclick="upload()">上传到媒体收件箱（64 MiB）</button>
    <input id="config" placeholder="TVBox 配置地址"><button onclick="config()">发送到桌面确认</button>
    <input id="url" placeholder="HTTP(S) 媒体地址"><button onclick="send()">推送播放</button><p id="status"></p>
    <script>async function req(path){let r=await fetch(path,{method:'POST',headers:{'X-VodBox-Control':'1'}});document.getElementById('status').textContent=r.ok?'已发送':'操作失败：'+r.status}
    async function upload(){let f=document.getElementById('file').files[0];if(!f)return;let r=await fetch('/upload?name='+encodeURIComponent(f.name),{method:'POST',headers:{'X-VodBox-Control':'1'},body:f});document.getElementById('status').textContent=r.ok?'已上传：'+await r.text():'上传失败：'+r.status}
    async function pair(){await req('/pair?code='+encodeURIComponent(document.getElementById('code').value))}
    async function refresh(){try{let r=await fetch('/status');if(!r.ok)return;let s=await r.json();document.getElementById('playing').textContent=s.title+' · '+s.state+' · '+Math.floor(s.positionSeconds)+' / '+Math.floor(s.durationSeconds)+' 秒'}catch{}}setInterval(refresh,2000);
    function config(){req('/config?url='+encodeURIComponent(document.getElementById('config').value))}
    function adjust(c){req('/adjust?command='+c+'&value='+encodeURIComponent(document.getElementById(c).value))}
    function act(c){req('/action?command='+c)}function send(){req('/media?url='+encodeURIComponent(document.getElementById('url').value))}</script>
    """;
}
