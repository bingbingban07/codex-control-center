using CodexControlCenter.Core;
using System.Text.Json;
using System.Security.Cryptography;

string? Option(string key){int index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?args[index+1]:null;}
string root=Path.GetFullPath(Option("--root")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexControlCenter","wallpaper"));
Directory.CreateDirectory(root);
FileStream hostLock;
try{hostLock=new FileStream(Path.Combine(root,"host.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
catch(IOException){
    if(args.Contains("--launch")&&!args.Contains("--isolated")){
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("codex:///"){UseShellExecute=true})?.Dispose();}catch{}
    }
    return 0;
}
using var heldLock=hostLock;
var connections=new Dictionary<string,(WallpaperConnection Connection,string Revision)>();
IReadOnlyList<WallpaperWindowStatus> lastWindows=[];string? currentPreviewId=null;
void Status(string state,string message,int windows=0,string? revision=null)=>CodexWallpaperRuntime.WriteJson(CodexWallpaperRuntime.StatusPath(root),new WallpaperRuntimeStatus(state,message,windows,revision,DateTimeOffset.UtcNow){Version="0.9.0",ProtocolVersion=2,HostPid=Environment.ProcessId,PreviewLeaseId=currentPreviewId,Windows=lastWindows});
try{
    var library=new WallpaperLibrary(root);
    var previewService=new WallpaperPreviewService(library);
    WallpaperRuntimeSession? session=CodexWallpaperRuntime.ReadSession(root);
    string modeHome=Option("--codex-home")??Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
    if(!args.Contains("--isolated")&&!EnhancementMode.IsEnabled(modeHome)&&(session is null||!CodexWallpaperRuntime.IsSessionProcessAlive(session))){Status("original_mode","当前为原版模式，壁纸增强已停用。");return 0;}
    if(session is null||!CodexWallpaperRuntime.IsSessionProcessAlive(session)){
        if(!args.Contains("--launch")){Status("disconnected","Codex 尚未通过壁纸入口启动。");return 0;}
        var running=CodexWallpaperRuntime.RunningCodex();
        bool another=running.Count>0;foreach(var process in running)process.Dispose();
        if(another&&!args.Contains("--isolated")){Status("restart_required","请正常退出 Codex 后，再点击“启动带壁纸的 Codex”。当前窗口继续保留。");return 0;}
        string executable=Option("--executable")??CodexWallpaperRuntime.FindExecutable(root)??throw new InvalidOperationException("未找到 Codex。请先正常打开一次 Codex，再回管理台；或使用“定位 Codex 程序”。");
        CodexWallpaperRuntime.RememberExecutable(root,executable);
        session=CodexWallpaperRuntime.Launch(executable,args.Contains("--isolated")?Option("--profile"):null,args.Contains("--isolated")?Option("--codex-home"):null);
        // Package activation is asynchronous. Confirm a reachable endpoint owned by this
        // exact process before recording a reusable connection. Never close a user window.
        var deadline=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(15);
        while(DateTimeOffset.UtcNow<deadline&&!CodexWallpaperRuntime.OwnsLoopbackEndpoint(session)){
            if(!CodexWallpaperRuntime.IsSessionProcessAlive(session))break;
            await Task.Delay(250);
        }
        if(!CodexWallpaperRuntime.OwnsLoopbackEndpoint(session)){
            Status("connection_unavailable","Codex 已通过系统入口启动，壁纸连接暂不可用。当前窗口可正常使用；不会隐藏或关闭窗口。");return 0;
        }
        CodexWallpaperRuntime.WriteJson(CodexWallpaperRuntime.SessionPath(root),session);
    }
    Status("connecting","正在连接 Codex 外观组件…");
    int consecutiveFailures=0;string cachedRevision="",script="",committedRevision="",committedScript=WallpaperRenderer.BuildFromRegisteredImage(new(),null);var imagePayloads=new Dictionary<string,string>();
    while(CodexWallpaperRuntime.IsSessionProcessAlive(session)){
        try{
            if(!CodexWallpaperRuntime.OwnsLoopbackEndpoint(session))throw new IOException("尚未检测到 Codex 的本地壁纸连接；请通过壁纸入口重新打开。");
            var (committed,revision)=library.LoadSnapshot();var preview=previewService.ReadActive(revision);currentPreviewId=preview?.Id;
            bool enhancementEnabled=args.Contains("--isolated")||EnhancementMode.IsEnabled(modeHome);
            var settings=enhancementEnabled?(preview?.Settings??committed):committed with{Enabled=false};
            if(!enhancementEnabled){preview=null;currentPreviewId=null;}
            var imageIds=new[]{committed.Enabled?committed.ImageId:null,settings.Enabled?settings.ImageId:null}.Where(id=>id is not null).Cast<string>().Distinct().ToArray();
            foreach(string stale in imagePayloads.Keys.Except(imageIds).ToArray())imagePayloads.Remove(stale);
            foreach(string id in imageIds)if(!imagePayloads.ContainsKey(id))imagePayloads[id]=library.GetDataUri(id);
            if(committedRevision!=revision){committedScript=WallpaperRenderer.BuildFromRegisteredImage(committed,committed.ImageId);committedRevision=revision;}
            string effectiveRevision=!enhancementEnabled?revision+":original":preview is null?revision:revision+":preview:"+preview.Id+":"+Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));
            if(effectiveRevision!=cachedRevision){
                script=WallpaperRenderer.BuildFromRegisteredImage(settings,settings.ImageId);cachedRevision=effectiveRevision;
            }
            var targets=await WallpaperConnection.ListAsync(session.Port);var ids=targets.Select(t=>t.Id).ToHashSet();
            foreach(string id in connections.Keys.Where(id=>!ids.Contains(id)).ToArray()){await connections[id].Connection.DisposeAsync();connections.Remove(id);}
            int applied=0;bool loading=false;bool imageError=false;bool pageOverlay=false;int targetFailures=0;var windows=new List<WallpaperWindowStatus>();
            foreach(var target in targets){
                try{
                    if(!connections.TryGetValue(target.Id,out var item)){
                        var connection=new WallpaperConnection();
                        try{await connection.ConnectAsync(target,session.Port);item=(connection,"");connections[target.Id]=item;}
                        catch{await connection.DisposeAsync();throw;}
                    }
                    var registered=await item.Connection.CallAsync("Runtime.evaluate",new{expression=WallpaperRenderer.PruneImages(imageIds),returnByValue=true});
                    var present=registered.GetProperty("result").GetProperty("value").EnumerateArray().Select(v=>v.GetString()).ToHashSet();
                    foreach(string id in imageIds)if(!present.Contains(id))await item.Connection.CallAsync("Runtime.evaluate",new{expression=WallpaperRenderer.RegisterImage(id,imagePayloads[id]),returnByValue=true});
                    if(item.Revision!=effectiveRevision){
                        var evaluated=await item.Connection.CallAsync("Runtime.evaluate",new{expression=preview is null?script:WallpaperRenderer.BuildTrialScript(script,committedScript,preview.ExpiresAt),returnByValue=true});
                        if(evaluated.TryGetProperty("exceptionDetails",out _))throw new InvalidOperationException("Codex 当前页面拒绝应用壁纸。");
                        item=(item.Connection,effectiveRevision);connections[target.Id]=item;
                    }
                    string observation=(preview is null?"":"window.__cccWallpaperTrialV1?.renew("+preview.ExpiresAt.ToUnixTimeMilliseconds()+");")+"({...((window.__cccWallpaperV1?.diagnostic()) ?? {state:'disabled',version:1}),trialActive:!!window.__cccWallpaperTrialV1})";
                    var observed=await item.Connection.CallAsync("Runtime.evaluate",new{expression=observation,returnByValue=true});
                    if(observed.TryGetProperty("exceptionDetails",out _))throw new InvalidOperationException("Codex 当前页面不能应用壁纸。");
                    string state=observed.GetProperty("result").GetProperty("value").GetProperty("state").GetString()??"unknown";
                    windows.Add(new(target.Id,state,state switch{"applied"=>preview is null?"已应用保存的壁纸":"正在试用未保存壁纸","disabled"=>"已恢复默认外观","page_overlay"=>"全屏内容暂时遮住背景","image_error"=>"图片解码失败",_=>"等待页面准备好"},item.Connection.BytesSent,item.Connection.RequestsSent));
                    bool trialMissing=preview is not null&&!observed.GetProperty("result").GetProperty("value").GetProperty("trialActive").GetBoolean();
                    if((state is "disabled" or "detached" or "image_missing"&&settings.Enabled)||trialMissing){await item.Connection.CallAsync("Runtime.evaluate",new{expression=preview is null?script:WallpaperRenderer.BuildTrialScript(script,committedScript,preview.ExpiresAt),returnByValue=true});loading=true;}
                    else if(state=="applied")applied++;else if(state=="loading")loading=true;else if(state=="image_error")imageError=true;else if(state=="page_overlay")pageOverlay=true;
                }catch(Exception targetError){
                    if(connections.Remove(target.Id,out var dead))await dead.Connection.DisposeAsync();
                    loading=true;targetFailures++;windows.Add(new(target.Id,"error",UserFeedback.Redact(targetError.Message)));
                }
            }
            lastWindows=windows;consecutiveFailures=targetFailures>0?consecutiveFailures+1:0;
            if(targetFailures>0&&consecutiveFailures>=4)Status("error","Codex 外观连接持续失败。当前版本可能不支持壁纸连接；请正常退出后从壁纸入口重开。",applied,revision);
            else if(!settings.Enabled&&targets.Count>0&&!loading)Status(preview is null?"disabled":"preview",preview is null?"壁纸已关闭，Codex 已恢复原外观。":"正在临时试用默认外观；结束试用后恢复已应用壁纸。",revision:revision);
            else if(imageError)Status("error","Codex 未能解码图片，请重新导入。",applied,revision);
            else if(applied>0&&!loading)Status(preview is null?"applied":"preview",preview is null?$"已应用到 {applied} 个 Codex 窗口。":$"正在 {applied} 个 Codex 窗口试用；结束试用或关闭管理台后恢复。",applied,revision);
            else if(pageOverlay&&!loading)Status("ready","已连接；当前全屏页面遮住了壁纸，回到聊天后显示。",applied,revision);
            else Status("connecting","等待 Codex 页面载入；壁纸设置已保留。",applied,revision);
        }catch(Exception error){
            if(currentPreviewId is not null){
                currentPreviewId=null;foreach(string id in connections.Keys.ToArray())try{var item=connections[id];await item.Connection.CallAsync("Runtime.evaluate",new{expression=committedScript,returnByValue=true});connections[id]=(item.Connection,"");}catch{}
            }
            consecutiveFailures++;Status(consecutiveFailures<4?"connecting":"error",UserFeedback.Redact(error.Message));
        }
        if(args.Contains("--once"))break;
        await Task.Delay(2000);
    }
    if(!CodexWallpaperRuntime.IsSessionProcessAlive(session))Status("disconnected","Codex 已退出；下次通过壁纸入口启动会自动恢复。");
    return 0;
}catch(Exception error){Status("error",UserFeedback.Redact(error.Message));return 1;}
finally{foreach(var item in connections.Values)await item.Connection.DisposeAsync();}
