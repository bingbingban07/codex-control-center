using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task RememberManagedHooksOnStartupAsync()
    {
        if(snapshot is null||dirty||HasDrafts||!EnhancementMode.IsEnabled(snapshot.CodexHome))return;
        try{
            var policy=new SubagentPolicyService(snapshot.CodexHome,appRoot).Load(snapshot);
            if(!policy.Enabled||!policy.AutoTrustHooks)return;
            busy=true;RefreshDraftChrome();SetFooter("正在确认管理台钩子的持久授权…");
            var result=await new ManagedHookTrustService(appRoot).ReconcileAsync(configuration,ResolvePythonExecutable(),lifetime.Token);
            busy=false;await ReloadAsync();
            if(result.Verified)SetFooter("管理台钩子授权已记住 · 配置已读取");
            else ShowNotice("未完成本次钩子授权检查",result.Detail,InfoBarSeverity.Warning);
        }catch(Exception error){UiCrashLog.Exception("managed-hooks.startup",error);ShowNotice("未完成本次钩子授权检查","已有授权不因此失效。原因："+Safe(error.Message)+"\n可在子代理页使用“立即修复并记住钩子授权”重新检查。",InfoBarSeverity.Warning);}
        finally{busy=false;RefreshDraftChrome();}
    }
}
