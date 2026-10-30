using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexControlCenter.Core;

internal static class RouterActivationTests
{
    public static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"cc-activation-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach(string scenario in new[]{"legacy","matching","stale","credential","partial","ready","array","malformed"})
            {
                using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                int port=((IPEndPoint)listener.LocalEndpoint).Port;
                string path=Path.Combine(root,"router.json");
                await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{host="127.0.0.1",port}));
                string hash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
                string payload=scenario switch {
                    "legacy"=>"{\"status\":\"ok\"}",
                    "matching"=>JsonSerializer.Serialize(new{runtime=new{config_sha256=hash,reload_error=(string?)null}}),
                    "stale"=>JsonSerializer.Serialize(new{runtime=new{config_sha256="old",reload_error=(string?)null}}),
                    "credential"=>JsonSerializer.Serialize(new{runtime=new{config_sha256=hash,reload_error="credential_load_failed"}}),
                    "partial"=>JsonSerializer.Serialize(new{runtime=new{config_sha256=hash,config_synced=true,credential_status="degraded",unavailable_providers=new[]{new{id="minimax",reason="credential_unavailable"}}}}),
                    "ready"=>JsonSerializer.Serialize(new{runtime=new{config_sha256=hash,config_synced=true,credential_status="ready"}}),
                    "array"=>"[]",
                    _=>"broken-json"
                };
                var responseTask=Task.Run(async()=>{
                    using var client=await listener.AcceptTcpClientAsync();
                    using var stream=client.GetStream();var one=new byte[1];var headers=new StringBuilder();
                    while(headers.Length<8192&&!headers.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)){
                        if(await stream.ReadAsync(one)==0)break;headers.Append((char)one[0]);
                    }
                    var body=Encoding.UTF8.GetBytes(payload);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(body);
                });
                var snapshot=new WorkspaceSnapshot{RouterPath=path,CodexHome=root};
                var result=await new RuntimeService().CheckRouterConfigurationAsync(snapshot);
                await responseTask;
                if(result.Online!=(scenario is "matching" or "ready"))throw new Exception("Runtime activation misreported "+scenario);
                if(scenario=="legacy"&&!result.Status.Contains("未确认"))throw new Exception("Legacy runtime incorrectly verified");
                if(scenario=="credential"&&!result.Detail.Contains("密钥加载失败"))throw new Exception("Credential error lacks guidance");
                if(scenario=="partial"&&!result.Detail.Contains("minimax"))throw new Exception("Partial credential failure lacks affected source");
                if(scenario=="ready"&&!result.Detail.Contains("15 秒"))throw new Exception("Environment credential timing not explained");
            }
            var s=new WorkspaceSnapshot{RouterPath=Path.Combine(root,"router.json"),CodexHome=root,Providers=[new(){Id="minimax"}]};
            if(!RuntimeService.ChangesAffectRouter(s,[new(s.RouterPath,"{}","","")]))throw new Exception("Route change not detected");
            if(!RuntimeService.ChangesAffectRouter(s,[new(Path.Combine(root,"secrets/control-center-minimax.dpapi"),"ciphertext","","")]))throw new Exception("Credential change not detected");
            if(RuntimeService.ChangesAffectRouter(s,[new(Path.Combine(root,"AGENTS.md"),"text","","")]))throw new Exception("Unrelated file marked as route");
            if(RuntimeService.ChangesAffectRouter(new(){CodexHome=root},[new(Path.Combine(root,"AGENTS.md"),"text","","")]))throw new Exception("A workspace without a local router cannot save documents");
        }
        finally{Directory.Delete(root,true);}
    }
}
