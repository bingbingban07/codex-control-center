using CodexControlCenter.Core;
using System.Text.Json.Nodes;

internal static class TransactionRegressionTests
{
    public static async Task RunAsync()
    {
        await BoundaryChangeAsync();await RollbackConflictAsync();await IncompleteRecoveryAsync();await NewFileConflictAsync();await DependencyChangeAsync();await WatchNewFilesAsync();
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task MustFail(Func<Task> action){try{await action();}catch(InvalidOperationException){return;}throw new InvalidOperationException("Expected conflict rejection");}
    private static async Task BoundaryChangeAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();var s=await f.Service.LoadAsync();
        var p=f.Service.PlanDefaults(s,"model-a","high","model-b","max",8);
        string external=await File.ReadAllTextAsync(f.ConfigPath)+"\n# external edit at commit\n";
        f.Service.Progress+=(_,e)=>{if(e.Stage=="before-write")File.WriteAllText(e.FilePath,external);};
        await MustFail(()=>f.Service.ApplyAsync(s,p));Require(await File.ReadAllTextAsync(f.ConfigPath)==external,"commit boundary overwrote external content");
    }
    private static async Task RollbackConflictAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();var s=await f.Service.LoadAsync();var p=f.Service.PlanDefaults(s,"model-a","high","model-b","max",8);
        string added=Path.Combine(f.CodexHome,"control-center","image-profiles.json");p.Files.AddRange(f.Service.PlanText(s,added,"{}","test new profile").Files);
        string external=await File.ReadAllTextAsync(f.ConfigPath)+"\n# external while rollback\n";
        f.Service.Progress+=(_,e)=>{if(e.Stage=="written"&&e.FilePath==f.ConfigPath){File.WriteAllText(f.ConfigPath,external);Directory.CreateDirectory(added);}};
        await MustFail(()=>f.Service.ApplyAsync(s,p));Require(await File.ReadAllTextAsync(f.ConfigPath)==external,"rollback overwrote external content");
        Require(f.Service.ListBackups().Any(x=>x.Summary.StartsWith("[未完成事务")),"partial rollback not exposed");
    }
    private static async Task IncompleteRecoveryAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();await File.WriteAllTextAsync(Path.Combine(f.CodexHome,"AGENTS.md"),"# fixture rules\n");var s=await f.Service.LoadAsync();byte[] original=await File.ReadAllBytesAsync(f.ConfigPath);string agents=await File.ReadAllTextAsync(s.AgentsPath);
        var p=f.Service.PlanDefaults(s,"model-a","high","model-b","max",8);p.Files.AddRange(f.Service.PlanText(s,s.AgentsPath,agents+"\n# new rules\n","update rules").Files);
        var result=await f.Service.ApplyAsync(s,p);await File.WriteAllBytesAsync(f.ConfigPath,original);
        string journal=Path.Combine(result.BackupDirectory,"journal.json");var data=JsonNode.Parse(await File.ReadAllTextAsync(journal))!;data["Phase"]="written";await File.WriteAllTextAsync(journal,data.ToJsonString());
        Require(f.Service.ListBackups().Any(x=>x.Summary.StartsWith("[未完成事务")),"unfinished transaction not listed");
        await f.Service.RestoreAsync(result.BackupDirectory);Require((await File.ReadAllBytesAsync(f.ConfigPath)).SequenceEqual(original),"recovery lost raw config bytes");Require(await File.ReadAllTextAsync(s.AgentsPath)==agents,"partial transaction recovery failed");
        Require(!f.Service.ListBackups().Any(x=>x.Directory==result.BackupDirectory&&x.Summary.StartsWith("[未完成事务")),"recovered journal stayed unfinished");
    }
    private static async Task NewFileConflictAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();var s=await f.Service.LoadAsync();string path=Path.Combine(f.CodexHome,"control-center","image-profiles.json");var p=f.Service.PlanText(s,path,"{}","create");
        f.Service.Progress+=(_,e)=>{if(e.Stage=="before-write"){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"{\"external\":true}");}};
        await MustFail(()=>f.Service.ApplyAsync(s,p));Require(File.ReadAllText(path).Contains("external"),"external new file was deleted");
    }
    private static async Task DependencyChangeAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();var s=await f.Service.LoadAsync();var p=f.Service.PlanDefaults(s,"model-a","high","model-b","max",8);
        await File.AppendAllTextAsync(f.RouterPath,"\n ");string before=await File.ReadAllTextAsync(f.ConfigPath);await MustFail(()=>f.Service.ApplyAsync(s,p));Require(await File.ReadAllTextAsync(f.ConfigPath)==before,"stale route dependency accepted");
    }
    private static async Task WatchNewFilesAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();string path=Path.Combine(f.CodexHome,"control-center","subagent-policy.json");using var watch=new WorkspaceChangeMonitor([path,f.ConfigPath]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,"{}");bool signalled=false;var end=DateTime.UtcNow.AddSeconds(5);while(DateTime.UtcNow<end){await Task.Delay(50);if(watch.Consume(TimeSpan.Zero)){signalled=true;break;}}
        Require(signalled,"new policy file not observed");
    }
}
