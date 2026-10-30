using System.Text.Json.Nodes;
using CodexControlCenter.Core;
using Tomlyn;
using Tomlyn.Model;

internal static class ManagedHookTrustTests
{
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    public static async Task RunAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();
        string firstApp=Path.Combine(f.Root,"release","v1"),secondApp=Path.Combine(f.Root,"release","v2");
        foreach(string app in new[]{firstApp,secondApp}){
            string scriptSource=Path.Combine(app,"adapters","subagent-policy","policy_hook.py");
            Directory.CreateDirectory(Path.GetDirectoryName(scriptSource)!);await File.WriteAllTextAsync(scriptSource,"# synthetic reviewed hook\n");
        }
        var snapshot=await f.Service.LoadAsync();
        Require(!new SubagentRoutingPolicy().AutoTrustHooks,"Public defaults automatically trust hooks");
        var policy=new SubagentRoutingPolicy{Enabled=true,AutoTrustHooks=true,Primary=new(){Provider="a",Model="model-a",Effort="high"},Overflow=new(){Provider="b",Model="model-b",Effort="max"}};
        var service=new SubagentPolicyService(f.CodexHome,firstApp);
        string python=Environment.ProcessPath!;
        var plan=service.Plan(snapshot,policy,python);
        Require(plan.Errors.Count==0,"Stable hook plan invalid");
        Require(plan.Files.Any(file=>file.Path==service.HookPath),"Stable hook installation missing");
        var saved=await f.Service.ApplyAsync(snapshot,plan);
        snapshot=await f.Service.LoadAsync();
        Require(await File.ReadAllTextAsync(service.HookPath)=="# synthetic reviewed hook\n","Stable script not installed");
        string hooksPath=Path.Combine(f.CodexHome,"hooks.json");
        string originalHooks=await File.ReadAllTextAsync(hooksPath);
        Require(!originalHooks.Contains(firstApp,StringComparison.OrdinalIgnoreCase),"Hook command contains release version path");
        var nextService=new SubagentPolicyService(f.CodexHome,secondApp);
        var repeat=nextService.Plan(snapshot,nextService.Load(snapshot),python);
        Require(repeat.Files.Count==0,"Unchanged policy or version upgrade rewrites files and invalidates trust");
        string upgradedSource=nextService.SourceHookPath;
        await File.WriteAllTextAsync(upgradedSource,"# revised synthetic reviewed hook\n");
        var upgrade=nextService.Plan(snapshot,nextService.Load(snapshot),python);
        Require(upgrade.Files.Count==1&&upgrade.Files[0].Path==service.HookPath,"Script upgrade changed hook definitions or policy revision");

        string command=service.HookCommand(python),source=Path.GetFullPath(hooksPath);
        string[] events=["preToolUse","postToolUse","sessionEnd","subagentStart","subagentStop"];
        var definitions=events.Select((evt,i)=>new ManagedHookDefinition(source+":"+evt+":0:0","sha256:"+new string((char)('a'+i),64),"modified",evt,source,command,true,false)).ToList();
        var trust=new ManagedHookTrustService(firstApp);
        string configBefore=await File.ReadAllTextAsync(f.ConfigPath);
        var trustPlan=trust.PlanTrust(f.Service,snapshot,definitions,command);
        Require(trustPlan.Errors.Count==0&&trustPlan.Files.Count==1,"Valid exact hashes not planned");
        var table=TomlSerializer.Deserialize<TomlTable>(trustPlan.Files[0].Content)!;
        var state=(TomlTable)((TomlTable)table["hooks"])["state"];
        foreach(var definition in definitions)Require(((TomlTable)state[definition.Key])["trusted_hash"].ToString()==definition.Hash,"Dots/backslashes split literal trust key");
        Require(trustPlan.Files[0].Content.Contains("# keep root comment"),"Trust changed comments");
        var unrelated=new ManagedHookDefinition(source+":unrelated:0:0","sha256:"+new string('f',64),"modified","preToolUse",source,"unrelated command",true,false);
        var withOther=trust.PlanTrust(f.Service,snapshot,definitions.Append(unrelated),command);
        Require(withOther.Errors.Count==0&&!withOther.Files[0].Content.Contains(unrelated.Key),"Unrelated hook trusted");
        Require(trust.PlanTrust(f.Service,snapshot,definitions.Take(4),command).Errors.Count>0,"Incomplete set partially trusted");
        Require(trust.PlanTrust(f.Service,snapshot,definitions.Select((h,i)=>i==0?h with{Hash="invalid"}:h),command).Errors.Count>0,"Malformed hash accepted");
        Require(trust.PlanTrust(f.Service,snapshot,definitions.Select((h,i)=>i==0?h with{SourcePath=Path.Combine(f.Root,"other.json")}:h),command).Errors.Count>0,"Other source accepted");
        var trustSaved=await f.Service.ApplyAsync(snapshot,trustPlan);
        snapshot=await f.Service.LoadAsync();
        Require(trust.PlanTrust(f.Service,snapshot,definitions,command).Files.Count==0,"Trust write is not idempotent");
        int trustedReads=0;
        var alreadyTrusted=new ManagedHookTrustService(firstApp,(_,_)=>{trustedReads++;return Task.FromResult(definitions.Select(h=>h with{TrustStatus="trusted"}).ToList());});
        var confirmed=await alreadyTrusted.ReconcileAsync(f.Service,python);
        Require(confirmed.Verified&&trustedReads==1&&confirmed.BackupDirectory is null,"Already-persisted trust still repeated verification or rewrote configuration");
        await f.Service.RestoreAsync(trustSaved.BackupDirectory);
        Require(await File.ReadAllTextAsync(f.ConfigPath)==configBefore,"Trust backup did not restore original bytes");
        await f.Service.RestoreAsync(saved.BackupDirectory);
        Require(!File.Exists(service.HookPath),"Restoring migration left an installed script behind");

        string initialConfig=await File.ReadAllTextAsync(f.ConfigPath);
        string? initialHooks=File.Exists(hooksPath)?await File.ReadAllTextAsync(hooksPath):null;
        string? initialInstructions=File.Exists(snapshot.AgentsPath)?await File.ReadAllTextAsync(snapshot.AgentsPath):null;
        // Enable a saved policy while forcing the independent Codex reader to fail during migration.
        Directory.CreateDirectory(Path.GetDirectoryName(service.PolicyPath)!);
        await File.WriteAllTextAsync(service.PolicyPath,"""{"version":1,"enabled":true,"auto_trust_hooks":true,"mode":"concurrent","primary":{"provider":"a","model":"model-a","effort":"high"},"overflow":{"provider":"b","model":"model-b","effort":"max"}}""");
        var failing=new ManagedHookTrustService(firstApp,(_,_)=>throw new InvalidOperationException("Synthetic protocol failure"));
        var failed=await failing.ReconcileAsync(f.Service,python);
        Require(failed.Diagnostic?.Contains("Synthetic protocol failure")==true&&failed.Detail.Contains("读取 Codex 钩子状态"),"Hook failure reason and stage were hidden");
        Require(!failed.Verified&&failed.RolledBack&&!failed.RecoveryRequired&&failed.BackupDirectories.Count>0,"Failed trust migration did not return recovery evidence");
        Require(await File.ReadAllTextAsync(f.ConfigPath)==initialConfig,"Failed protocol left modified config");
        Require((File.Exists(hooksPath)?await File.ReadAllTextAsync(hooksPath):null)==initialHooks,"Failed protocol left modified hook definitions");
        Require((File.Exists(snapshot.AgentsPath)?await File.ReadAllTextAsync(snapshot.AgentsPath):null)==initialInstructions,"Failed protocol left modified instructions");
        Require(!File.Exists(service.HookPath),"Failed protocol left a new executable installed");
        await File.WriteAllTextAsync(hooksPath,"{ invalid JSON");
        var malformed=service.Plan(await f.Service.LoadAsync(),policy,python);
        Require(malformed.Errors.Count>0&&malformed.Files.Count==0,"Invalid hooks leaked a partial plan or threw an unhandled error");
    }
}
