using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>ASSRT字幕API；凭据仅随请求发送，不写入搜索URL或日志。</summary>
public sealed class AssrtSubtitles
{
    private readonly Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<byte[]>> _fetch;
    public AssrtSubtitles(DefaultHttp http):this((url,headers,ct)=>http.GetBoundedAsync(url,2*1024*1024,ct,headers)){}
    public AssrtSubtitles(Func<string,IReadOnlyDictionary<string,string>,CancellationToken,Task<byte[]>> fetch)=>_fetch=fetch;

    private async Task<JsonDocument> RequestAsync(string endpoint,string token,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(token))throw new InvalidOperationException("请配置ASSRT字幕服务凭据。");
        if(token.IndexOfAny(['\r','\n','\0'])>=0)throw new InvalidDataException("字幕凭据含非法字符。");
        var bytes=await _fetch("https://api.assrt.net/v1/sub/"+endpoint,new Dictionary<string,string>{{"Authorization","Bearer "+token}},ct);
        var document=JsonDocument.Parse(bytes);
        if(document.RootElement.ValueKind!=JsonValueKind.Object){document.Dispose();throw new InvalidDataException("字幕响应结构无效。");}
        if(document.RootElement.TryGetProperty("status",out var status)&&status.ValueKind==JsonValueKind.Number&&status.GetInt32()!=0)
        {document.Dispose();throw new InvalidOperationException("字幕服务返回失败状态，请检查凭据或稍后重试。");}
        return document;
    }

    public async Task<IReadOnlyList<OnlineSubtitle>> SearchAsync(string query,string token,CancellationToken ct=default)
    {
        if(string.IsNullOrWhiteSpace(query)||query.Length>200)throw new InvalidDataException("字幕关键词为空或过长。");
        using var document=await RequestAsync("search?q="+Uri.EscapeDataString(query.Trim()),token,ct);
        var result=new List<OnlineSubtitle>();
        if(!document.RootElement.TryGetProperty("sub",out var sub)||sub.ValueKind!=JsonValueKind.Object||!sub.TryGetProperty("subs",out var items)||items.ValueKind!=JsonValueKind.Array)return result;
        foreach(var item in items.EnumerateArray().Take(100))
        {
            if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("id",out var id)||!item.TryGetProperty("native_name",out var title)||title.ValueKind!=JsonValueKind.String)continue;
            var key=id.ValueKind==JsonValueKind.Number?id.GetRawText():id.ValueKind==JsonValueKind.String?id.GetString():null;
            if(key is null||!long.TryParse(key,out var value)||value<=0)continue;
            result.Add(new OnlineSubtitle(key,title.GetString()??key));
        }
        return result;
    }

    public async Task<IReadOnlyList<OnlineSubtitleFile>> FilesAsync(string id,string token,CancellationToken ct=default)
    {
        if(!long.TryParse(id,out var number)||number<=0)throw new InvalidDataException("字幕编号无效。");
        using var document=await RequestAsync("detail?id="+id,token,ct);
        var result=new List<OnlineSubtitleFile>();
        if(!document.RootElement.TryGetProperty("sub",out var sub)||sub.ValueKind!=JsonValueKind.Object||!sub.TryGetProperty("subs",out var items)||items.ValueKind!=JsonValueKind.Array)return result;
        foreach(var item in items.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("filelist",out var files)||files.ValueKind!=JsonValueKind.Array)continue;
            foreach(var file in files.EnumerateArray().Take(100))
            {
                if(file.ValueKind!=JsonValueKind.Object||!file.TryGetProperty("url",out var url)||url.ValueKind!=JsonValueKind.String||!file.TryGetProperty("f",out var name)||name.ValueKind!=JsonValueKind.String)continue;
                if(!Uri.TryCreate(url.GetString(),UriKind.Absolute,out var address)||address.Scheme!="https")continue;
                var filename=Path.GetFileName(name.GetString()??"");
                if(Path.GetExtension(filename).ToLowerInvariant() is not (".srt" or ".ass" or ".ssa" or ".vtt"))continue;
                result.Add(new OnlineSubtitleFile(filename,address.AbsoluteUri));
            }
        }
        return result;
    }
}
