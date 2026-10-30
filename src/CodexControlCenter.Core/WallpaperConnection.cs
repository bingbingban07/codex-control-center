using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CodexControlCenter.Core;

public sealed record WallpaperTarget(string Id,string Url,string WebSocketDebuggerUrl);

/// <summary>Private, bounded CDP transport for our local Codex appearance companion.</summary>
public sealed class WallpaperConnection : IAsyncDisposable
{
    private readonly ClientWebSocket socket=new();
    private int requestId;
    public long BytesSent {get;private set;}
    public int RequestsSent {get;private set;}
    public static bool IsCodexPage(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="app"&&uri.Host=="-"&&uri.AbsolutePath=="/index.html";
    public static bool IsLocalSocket(string value,int port)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="ws"&&uri.Host=="127.0.0.1"&&uri.Port==port&&uri.AbsolutePath.StartsWith("/devtools/page/",StringComparison.Ordinal)&&string.IsNullOrEmpty(uri.UserInfo);

    public static async Task<IReadOnlyList<WallpaperTarget>> ListAsync(int port,CancellationToken cancellationToken=default)
    {
        if(port is <1024 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(3)};
        using var response=await client.GetAsync($"http://127.0.0.1:{port}/json/list",HttpCompletionOption.ResponseHeadersRead,cancellationToken);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>1024*1024)throw new InvalidDataException("本地壁纸连接返回内容过大。");
        await using var stream=await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bounded=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,cancellationToken))>0){if(bounded.Length+count>1024*1024)throw new InvalidDataException("本地壁纸连接返回内容过大。");bounded.Write(buffer,0,count);}
        using var document=JsonDocument.Parse(bounded.ToArray());var values=new List<WallpaperTarget>();
        foreach(var row in document.RootElement.EnumerateArray()){
            if(row.TryGetProperty("type",out var type)&&type.GetString()=="page"&&row.TryGetProperty("url",out var url)&&IsCodexPage(url.GetString())&&
                row.TryGetProperty("id",out var id)&&row.TryGetProperty("webSocketDebuggerUrl",out var ws)&&IsLocalSocket(ws.GetString()??"",port))
                values.Add(new(id.GetString()!,url.GetString()!,ws.GetString()!));
        }
        return values;
    }
    public async Task ConnectAsync(WallpaperTarget target,int port,CancellationToken cancellationToken=default)
    {
        if(!IsCodexPage(target.Url)||!IsLocalSocket(target.WebSocketDebuggerUrl,port))throw new InvalidOperationException("拒绝连接非本机 Codex 页面。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(5));
        socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(15);
        await socket.ConnectAsync(new Uri(target.WebSocketDebuggerUrl),timeout.Token);
    }
    public async Task<JsonElement> CallAsync(string method,object parameters,CancellationToken cancellationToken=default)
    {
        // Production only uses these appearance/lifecycle commands. Never evaluate arbitrary callers' scripts.
        if(method!="Runtime.evaluate")throw new InvalidOperationException("壁纸连接不支持此操作。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        int id=++requestId;byte[] payload=JsonSerializer.SerializeToUtf8Bytes(new{id,method,@params=parameters},new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
        if(payload.Length>36*1024*1024)throw new InvalidOperationException("壁纸连接请求超过大小上限，请换一张较小的图片。");
        await socket.SendAsync(payload,WebSocketMessageType.Text,true,timeout.Token);
        BytesSent+=payload.Length;RequestsSent++;
        while(true){
            using var body=new MemoryStream();var buffer=new byte[8192];WebSocketReceiveResult r;
            do{r=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token);if(r.MessageType==WebSocketMessageType.Close)throw new IOException("Codex 壁纸连接已关闭。");
                if(body.Length+r.Count>1024*1024)throw new InvalidDataException("壁纸连接响应过大。");body.Write(buffer,0,r.Count);
            }while(!r.EndOfMessage);
            using var message=JsonDocument.Parse(body.ToArray());var json=message.RootElement;
            if(!json.TryGetProperty("id",out var received)||received.GetInt32()!=id)continue;
            if(json.TryGetProperty("error",out _))throw new InvalidOperationException("Codex 当前版本拒绝壁纸外观操作。");
            return json.GetProperty("result").Clone();
        }
    }
    public async ValueTask DisposeAsync(){socket.Dispose();await Task.CompletedTask;}
}
