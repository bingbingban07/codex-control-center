using CodexControlCenter.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class WallpaperConnectionTests
{
    public static async Task RunAsync()
    {
        void Check(bool condition,string error){if(!condition)throw new InvalidOperationException(error);}
        Check(WallpaperConnection.IsCodexPage("app://-/index.html?window=main"),"Codex app target rejected");
        foreach(string url in new[]{"https://example.com/index.html","app://evil/index.html","app://-/login","app://-/index.html/other","file:///index.html"})Check(!WallpaperConnection.IsCodexPage(url),"Non-Codex origin accepted: "+url);
        Check(WallpaperConnection.IsLocalSocket("ws://127.0.0.1:19387/devtools/page/valid",19387),"Local socket rejected");
        foreach(string url in new[]{"ws://0.0.0.0:19387/devtools/page/x","ws://localhost:19387/devtools/page/x","ws://127.0.0.1:19388/devtools/page/x","ws://user@127.0.0.1:19387/devtools/page/x","wss://127.0.0.1:19387/devtools/page/x"})Check(!WallpaperConnection.IsLocalSocket(url,19387),"Untrusted socket accepted");
        string disabled=WallpaperRenderer.BuildScript(new(),null);
        int starts=0,opens=0;
        await CodexOpenCoordinator.OpenAsync(false,true,()=>false,()=>starts++,()=>opens++,_=>Task.CompletedTask);
        Check(starts==0&&opens==1,"Original mode was swallowed by the wallpaper helper");
        starts=0;opens=0;
        string fallback=await CodexOpenCoordinator.OpenAsync(true,true,()=>false,()=>starts++,()=>opens++,_=>Task.CompletedTask,attempts:2);
        Check(fallback=="official-fallback"&&starts==1&&opens==1,"A silent helper failure did not reach official activation");
        starts=0;opens=0;
        await CodexOpenCoordinator.OpenAsync(true,true,()=>starts>0,()=>starts++,()=>opens++,_=>Task.CompletedTask);
        Check(starts==1&&opens==0,"A successful wallpaper launch opened a second application");
        opens=0;await CodexOpenCoordinator.OpenAsync(true,true,()=>false,()=>throw new IOException("helper unavailable"),()=>opens++,_=>Task.CompletedTask);
        Check(opens==1,"Companion exception blocked official activation");
        Check(disabled.Contains("dispose()")&&disabled.Contains("window.top!==window")&&disabled.Contains("pointer-events:none"),"Renderer lifecycle safeguards missing");
        string modeRoot=Path.Combine(Path.GetTempPath(),"ccc-enhancement-mode-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(modeRoot);
        try{
            File.WriteAllText(Path.Combine(modeRoot,"config.toml"),"[plugins.\"codex-enhancements@codex-control-center\"]\nenabled = false\n");
            Check(!EnhancementMode.IsEnabled(modeRoot),"Disabled native plugin still enables the wallpaper companion");
            File.WriteAllText(Path.Combine(modeRoot,"config.toml"),"[plugins.\"codex-enhancements@codex-control-center\"]\nenabled = true\n");
            Check(EnhancementMode.IsEnabled(modeRoot),"Enabled native plugin did not permit the companion");
            File.WriteAllText(Path.Combine(modeRoot,"config.toml"),"{invalid");Check(!EnhancementMode.IsEnabled(modeRoot),"Malformed mode config enabled enhancements");
        }finally{Directory.Delete(modeRoot,true);}
        string launchRoot=Path.Combine(Path.GetTempPath(),"ccc-package-launch-"+Guid.NewGuid().ToString("N"));
        try{
            string package=Path.Combine(launchRoot,"WindowsApps","OpenAI.Codex_26.1027.3384.0_x64__2p2nqsd0c76g0");
            string app=Path.Combine(package,"app");Directory.CreateDirectory(Path.Combine(app,"resources"));
            File.WriteAllText(Path.Combine(app,"resources","app.asar"),"fixture");string exe=Path.Combine(app,"ChatGPT.exe");File.WriteAllText(exe,"");
            File.WriteAllText(Path.Combine(package,"AppxManifest.xml"),"<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'><Applications><Application Id='CodexChromeNativeHost' Executable='app/native-host.exe'/><Application Id='App' Executable='app/ChatGPT.exe'/></Applications></Package>");
            var plan=CodexWallpaperRuntime.PlanLaunch(exe,null,19387);
            Check(plan.ApplicationId=="OpenAI.Codex_2p2nqsd0c76g0!App","Packaged launch bypassed the registered application identity");
            Check(plan.Arguments.Contains("--remote-debugging-address=127.0.0.1")&&plan.Arguments.Contains("--remote-debugging-port=19387"),"Package activation dropped wallpaper endpoint flags");
            File.WriteAllText(Path.Combine(package,"AppxManifest.xml"),"<Package><Applications><Application Id='Wrong' Executable='other.exe'/></Applications></Package>");
            bool missingEntryRejected=false;try{CodexWallpaperRuntime.PlanLaunch(exe,null,19387);}catch(InvalidOperationException){missingEntryRejected=true;}
            Check(missingEntryRejected,"A missing packaged entry silently used raw executable startup");
        }finally{if(Directory.Exists(launchRoot))Directory.Delete(launchRoot,true);}
        bool rejected=false;try{WallpaperRenderer.BuildScript(new(){Enabled=true,ImageId=new string('a',64)},"https://remote.invalid/image.png");}catch(InvalidOperationException){rejected=true;}Check(rejected,"Remote image accepted");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        var served=Task.Run(async()=>{
            using var socket=await listener.AcceptTcpClientAsync();await using var stream=socket.GetStream();var request=new byte[1];var requestText=new StringBuilder();
            while(!requestText.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)){if(await stream.ReadAsync(request)==0)throw new IOException("Mock request ended early");requestText.Append((char)request[0]);if(requestText.Length>8192)throw new IOException("Mock request too large");}
            string body=JsonSerializer.Serialize(new[]{new{id="correct",type="page",url="app://-/index.html",webSocketDebuggerUrl=$"ws://127.0.0.1:{port}/devtools/page/correct"},new{id="browser",type="page",url="https://private.invalid",webSocketDebuggerUrl=$"ws://127.0.0.1:{port}/devtools/page/browser"},new{id="other",type="page",url="app://-/index.html",webSocketDebuggerUrl="ws://192.0.2.2:19387/devtools/page/other"}});
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"));
        });
        try{var rows=await WallpaperConnection.ListAsync(port);Check(rows.Count==1&&rows[0].Id=="correct","Target filtering leaked a browser or remote connection");await served;}finally{listener.Stop();}
        string root=Path.Combine(Path.GetTempPath(),"ccc-wallpaper-runtime-"+Guid.NewGuid().ToString("N"));
        try{
            CodexWallpaperRuntime.WriteJson(CodexWallpaperRuntime.StatusPath(root),new WallpaperRuntimeStatus("applied","test",1,"revision",DateTimeOffset.UtcNow-TimeSpan.FromMinutes(1)));
            Check(CodexWallpaperRuntime.ReadStatus(root).State=="disconnected","Stale runtime status presented as live");
            CodexWallpaperRuntime.WriteJson(CodexWallpaperRuntime.StatusPath(root),new WallpaperRuntimeStatus("error","old error",UpdatedAt:DateTimeOffset.UtcNow-TimeSpan.FromMinutes(1)));
            Check(CodexWallpaperRuntime.ReadStatus(root).State=="disconnected","Old startup error presented as a live failure");
            Check(!CodexWallpaperRuntime.IsCodexExecutable(Environment.ProcessPath),"Arbitrary executable accepted as Codex");
            Check(QuickNavigation.Search(null,"壁纸").Any(t=>t.Page=="wallpaper"),"Wallpaper entry missing from quick navigation");
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
