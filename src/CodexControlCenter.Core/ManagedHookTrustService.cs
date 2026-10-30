using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexControlCenter.Core;

public sealed record ManagedHookDefinition(string Key,string Hash,string TrustStatus,string EventName,string SourcePath,string Command,bool Enabled,bool IsManaged);
public sealed record ManagedHookTrustResult(int Trusted,int Total,bool Verified,string Detail,string? BackupDirectory=null)
{
    public bool RolledBack { get; init; }
    public bool RecoveryRequired { get; init; }
    public IReadOnlyList<string> BackupDirectories { get; init; }=[];
    public string? Diagnostic { get; init; }
}

/// <summary>Persists exact hashes reported by Codex; scoped to our five known definitions.</summary>
public sealed class ManagedHookTrustService(string appRoot,Func<WorkspaceSnapshot,CancellationToken,Task<List<ManagedHookDefinition>>>? hookReader=null)
{
    private const string Marker="Codex 管理台 · 子代理全局分流";
    private static readonly HashSet<string> Events=["preToolUse","postToolUse","sessionEnd","subagentStart","subagentStop"];

    public async Task<ManagedHookTrustResult> ReconcileAsync(ConfigurationService configuration,string pythonPath,CancellationToken ct=default)
    {
        var snapshot=await configuration.LoadAsync();
        var policyService=new SubagentPolicyService(snapshot.CodexHome,appRoot);
        var policy=policyService.Load(snapshot);
        if(!policy.Enabled||!policy.AutoTrustHooks)return new(0,0,false,"自动信任未启用。");
        var migration=policyService.Plan(snapshot,policy,pythonPath);
        if(migration.Errors.Count>0)throw new InvalidOperationException("无法准备固定钩子入口，请检查 Python 与发布目录。");
        var backups=new List<string>();string? migrationBackup=null;bool trustCommitted=false;string phase="准备固定入口";
        try{
            ct.ThrowIfCancellationRequested();
            if(migration.Files.Count>0){var saved=await configuration.ApplyAsync(snapshot,migration);migrationBackup=saved.BackupDirectory;backups.Add(saved.BackupDirectory);snapshot=await configuration.LoadAsync();}
            string command=policyService.HookCommand(pythonPath);
            phase="读取 Codex 钩子状态";var discovered=await (hookReader?.Invoke(snapshot,ct)??ReadAsync(snapshot,ct));
            var plan=PlanTrust(configuration,snapshot,discovered,command);
            if(plan.Errors.Count>0)throw new InvalidOperationException(string.Join("；",plan.Errors));
            ct.ThrowIfCancellationRequested();
            var current=SelectExpected(discovered,snapshot,command);
            if(plan.Files.Count==0&&current.Count==5&&current.All(h=>h.TrustStatus=="trusted"))
                return new(5,5,true,"5 个管理台钩子的持久授权已确认，无需重复写入或启动第二次检查。",backups.LastOrDefault()){BackupDirectories=backups};
            phase="保存精确信任记录";if(plan.Files.Count>0){var saved=await configuration.ApplyAsync(snapshot,plan);backups.Add(saved.BackupDirectory);}
            trustCommitted=true;
            // Use another process: the second read proves trust survives a restart.
            var verifySnapshot=await configuration.LoadAsync();
            phase="通过独立进程确认持久授权";var verified=await (hookReader?.Invoke(verifySnapshot,ct)??ReadAsync(verifySnapshot,ct));
            var selected=SelectExpected(verified,snapshot,command);
            bool ok=selected.Count==5&&selected.All(h=>h.TrustStatus=="trusted");
            return new(selected.Count(h=>h.TrustStatus=="trusted"),selected.Count,ok,
                ok?"5 个管理台钩子已持久信任。修改模型分配和升级管理台不会改变固定入口。":"信任已保存，但 Codex 尚未确认；可再次检查。",backups.LastOrDefault()){BackupDirectories=backups};
        }catch(Exception ex) when(ex is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or JsonException){
            bool rolledBack=false,recovery=false;
            if(!trustCommitted&&migrationBackup is not null){
                try{await configuration.RestoreAsync(migrationBackup);rolledBack=true;}
                catch(Exception){recovery=true;}
            }
            string detail=trustCommitted?"授权已写入并备份，后续核对未完成；可稍后重新检查。":
                rolledBack?"授权检查未完成，已恢复迁移前配置；可稍后重试。":
                recovery?"授权检查未完成，自动恢复遇到外部修改。请在设置与备份中查看本次备份，原有外部修改已保留。":"未完成本次检查，未新增信任记录；已有授权不因此失效。";
            string diagnostic=phase+"："+UserFeedback.Redact(ex.Message);
            return new(0,0,false,detail+"\n"+diagnostic+"\n可到子代理页重新检查。",backups.LastOrDefault()){RolledBack=rolledBack,RecoveryRequired=recovery,BackupDirectories=backups,Diagnostic=diagnostic};
        }
    }

    public ChangePlan PlanTrust(ConfigurationService service,WorkspaceSnapshot snapshot,IEnumerable<ManagedHookDefinition> hooks,string expectedCommand)
    {
        var plan=new ChangePlan();
        var selected=SelectExpected(hooks,snapshot,expectedCommand);
        if(selected.Count!=5||selected.Select(h=>h.EventName).Distinct().Count()!=5){plan.Errors.Add("未找到完整的 5 个管理台钩子，已保留现有信任设置。");return plan;}
        string original=service.ReadDocument(snapshot,snapshot.ConfigPath),updated=original;
        foreach(var hook in selected){
            if(!Regex.IsMatch(hook.Hash,"^sha256:[a-f0-9]{64}$",RegexOptions.CultureInvariant)||!hook.Key.StartsWith(hook.SourcePath+":",StringComparison.OrdinalIgnoreCase)){
                plan.Errors.Add("Codex 返回的钩子标识或指纹无效，已保留现有信任设置。");return plan;
            }
            updated=TomlEditor.SetNamedTableValue(updated,["hooks","state",hook.Key],"trusted_hash",hook.Hash,snapshot.ConfigPath);
        }
        if(updated!=original){var edit=service.PlanText(snapshot,snapshot.ConfigPath,updated,"记住管理台子代理钩子的精确信任指纹");plan.Files.AddRange(edit.Files);plan.Errors.AddRange(edit.Errors);}
        return plan;
    }

    private static List<ManagedHookDefinition> SelectExpected(IEnumerable<ManagedHookDefinition> hooks,WorkspaceSnapshot snapshot,string command)
    {
        string source=Path.GetFullPath(Path.Combine(snapshot.CodexHome,"hooks.json"));
        return hooks.Where(h=>h.Enabled&&!h.IsManaged&&Events.Contains(h.EventName)&&
            string.Equals(h.SourcePath,source,StringComparison.OrdinalIgnoreCase)&&h.Command==command).ToList();
    }

    public async Task<List<ManagedHookDefinition>> ReadAsync(WorkspaceSnapshot snapshot,CancellationToken ct=default)
    {
        string exe=FindCodexExecutable()??throw new FileNotFoundException("未找到 Codex 命令行，请确认已安装 Codex。");
        // WinExe has no console codepage (Console.InputEncoding.CodePage can be 0).
        // Never inherit its legacy encoding for the JSON-RPC stdio transport.
        var utf8=new UTF8Encoding(false,true);
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=utf8,StandardOutputEncoding=utf8,StandardErrorEncoding=utf8,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=snapshot.CodexHome};
        foreach(string arg in new[]{"app-server","--listen","stdio://","-c","analytics.enabled=false"})info.ArgumentList.Add(arg);
        info.Environment["CODEX_HOME"]=snapshot.CodexHome;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process=Process.Start(info)??throw new InvalidOperationException("无法启动钩子状态检查。");
        var drain=process.StandardError.BaseStream.CopyToAsync(Stream.Null,timeout.Token);
        try{
            using var initialized=await Rpc(1,"initialize",new{clientInfo=new{name="codex_control_center",version="0.8.4",title="Codex Control Center"},capabilities=new{experimentalApi=true}});
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}".AsMemory(),timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            using var response=await Rpc(2,"hooks/list",new{cwds=new[]{snapshot.CodexHome}});
            var result=new List<ManagedHookDefinition>();
            foreach(var group in response.RootElement.GetProperty("result").GetProperty("data").EnumerateArray())
                foreach(var hook in group.GetProperty("hooks").EnumerateArray()){
                    string Value(string key)=>hook.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
                    if(Value("statusMessage")!=Marker||Value("handlerType")!="command")continue;
                    result.Add(new(Value("key"),Value("currentHash"),Value("trustStatus"),Value("eventName"),Value("sourcePath"),Value("command"),hook.GetProperty("enabled").GetBoolean(),hook.GetProperty("isManaged").GetBoolean()));
                }
            return result;
        }catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new InvalidOperationException("钩子信任检查超时；已保存文件保留，可稍后重试。");}
        finally{
            if(!process.HasExited)process.Kill(entireProcessTree:true);
            try{await drain;}catch(OperationCanceledException){}catch(IOException){}
        }

        async Task<JsonDocument> Rpc(int id,string method,object args){
            string request=JsonSerializer.Serialize(new{id,method,@params=args});
            await process.StandardInput.WriteLineAsync(request.AsMemory(),timeout.Token);await process.StandardInput.FlushAsync(timeout.Token);
            for(int i=0;i<2000;i++){
                string? line=await process.StandardOutput.ReadLineAsync(timeout.Token);
                if(line is null)throw new InvalidOperationException("Codex 钩子检查进程已退出。");
                if(line.Length>2_000_000)throw new InvalidOperationException("钩子状态响应超出限制。");
                JsonDocument message;try{message=JsonDocument.Parse(line);}catch(JsonException){continue;}
                if(message.RootElement.TryGetProperty("id",out var value)&&value.ValueKind==JsonValueKind.Number&&value.GetInt32()==id){
                    if(message.RootElement.TryGetProperty("error",out _)){message.Dispose();throw new InvalidOperationException("Codex 不支持当前钩子状态检查，请更新后重试。");}
                    return message;
                }
                message.Dispose();
            }
            throw new InvalidOperationException("无法读取钩子状态。");
        }
    }

    private static string? FindCodexExecutable(){
        foreach(string folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries)){
            try{string path=Path.Combine(folder.Trim('"'),"codex.exe");if(File.Exists(path))return path;}catch(ArgumentException){}
        }
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
        if(Directory.Exists(root))return Directory.EnumerateDirectories(root).OrderByDescending(Directory.GetLastWriteTimeUtc).Select(d=>Path.Combine(d,"codex.exe")).FirstOrDefault(File.Exists);
        return null;
    }
}
