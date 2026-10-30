using CodexControlCenter.Core;
using System.Text.Json.Nodes;

internal static class UsabilityRegressionTests
{
    public static Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CodexControlCenter.UsabilityTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            string path=Path.Combine(root,"preferences.json");var store=new AppPreferencesStore(path);
            var initial=store.Load();Require(initial.Count==0,"missing settings did not use defaults");
            store.Save(new(){["density"]="large",["custom"]="preserve"});var next=store.Load();next["theme"]="dark";store.Save(next);
            Require(store.Load()["custom"]?.ToString()=="preserve","unknown setting lost");Require(File.Exists(path+".bak"),"valid settings not backed up");
            string lastValid=File.ReadAllText(path+".bak");File.WriteAllText(path,"{ corrupt");bool rejected=false;try{store.Load();}catch(FormatException){rejected=true;}Require(rejected,"damaged settings swallowed");
            store.Save(new(){["theme"]="system"});Require(Directory.GetFiles(root,"*.damaged-*.bak").Length==1,"damaged bytes not preserved");Require(File.ReadAllText(path+".bak")==lastValid,"last valid backup overwritten by corrupt settings");Require(Directory.GetFiles(root,"*.tmp").Length==0,"settings temporary file leaked");
            string fake="Bearer secret-bearer-value\n\"api_key\":\"secret-api-value\"\naccess_token=secret-access-value\nsk-demo-123456789";
            string sanitized=UserFeedback.Redact(fake);Require(!sanitized.Contains("secret-bearer-value")&&!sanitized.Contains("secret-api-value")&&!sanitized.Contains("secret-access-value")&&!sanitized.Contains("sk-demo-123456789"),"technical feedback leaked credentials");
            string common=UserFeedback.Redact("API key: sample-space-secret\nAuthorization: Basic dXNlcjpwYXNz");Require(!common.Contains("sample-space-secret")&&!common.Contains("dXNlcjpwYXNz"),"common authentication forms leaked");
            var conflict=UserFeedback.Explain(new InvalidOperationException("Content hash conflict: config.toml"));Require(conflict.Action=="compare"&&conflict.NextStep.Contains("草稿"),"conflict has no safe recovery path");
            var recovery=UserFeedback.Explain(new InvalidOperationException("Apply failed and rollback was incomplete for 1 file"));Require(recovery.Action=="settings"&&!recovery.Message.Contains("没有完成"),"partial rollback was described as no changes");
            Require(UserFeedback.TranslateValidation("Provider base_url must be absolute").Contains("接口地址"),"validation not understandable");
            Require(UserFeedback.Explain(new FileNotFoundException("not found")).Title.Contains("没有找到"),"missing config is not actionable");
        }finally{string allowed=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"CodexControlCenter.UsabilityTests"))+Path.DirectorySeparatorChar;if(!Path.GetFullPath(root).StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected test cleanup path");Directory.Delete(root,true);}
        return Task.CompletedTask;
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
