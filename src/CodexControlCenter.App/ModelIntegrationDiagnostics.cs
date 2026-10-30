using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text.Json;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyModelIntegrationAsync(string directory)
    {
        var s=snapshot??throw new InvalidOperationException("配置未加载。");
        if(!s.Models.Any(m=>m.Provider=="subscription"&&m.Mapped)||!s.Models.Any(m=>m.Provider=="minimax"&&m.Mapped)||!s.Models.Any(m=>m.Provider=="a"&&m.Id.StartsWith("key1/")))
            throw new InvalidOperationException("统一模型来源不完整。");
        dirty=false;Navigate("agents");Render();await Task.Delay(150);
        var selectors=Descendants<ComboBox>(PageBody).Where(c=>c.Header as string=="接入来源 / Key").ToList();
        if(selectors.Count!=2)throw new InvalidOperationException("A/B 来源选择器未显示。");
        var primary=selectors[0];primary.SelectedItem=primary.Items.OfType<string>().Single(t=>t.Contains("[subscription]"));await Task.Delay(100);
        var plan=planFactory!();if(plan.Errors.Count>0)throw new InvalidOperationException("订阅子代理配置未通过："+string.Join("；",plan.Errors));
        await CaptureAsync(Path.Combine(directory,"subscription-subagent-selector.png"));
        primary.SelectedItem=primary.Items.OfType<string>().Single(t=>t.Contains("[minimax]"));await Task.Delay(100);
        plan=planFactory!();if(plan.Errors.Count>0)throw new InvalidOperationException("MiniMax 子代理配置未通过："+string.Join("；",plan.Errors));
        await CaptureAsync(Path.Combine(directory,"minimax-subagent-selector.png"));
        dirty=false;drafts.Clear();Navigate("models");Render();await Task.Delay(150);
        await CaptureAsync(Path.Combine(directory,"unified-models.png"));
        var watch=Stopwatch.StartNew();var hooks=await new ManagedHookTrustService(appRoot).ReadAsync(s,lifetime.Token);
        if(hooks.Count!=5||hooks.Any(h=>h.TrustStatus!="trusted"))throw new InvalidOperationException("原生界面读取钩子持久授权未通过。");
        await File.WriteAllTextAsync(Path.Combine(directory,"integration-result.json"),JsonSerializer.Serialize(new{success=true,
            totalModels=s.Models.Count,officialModels=s.Models.Count(m=>m.Provider=="subscription"),key1Namespaced=true,
            subscriptionSubagentSelectable=true,minimaxSubagentSelectable=true,policyPlansValid=true,
            nativeGuiHookReadSeconds=watch.Elapsed.TotalSeconds,trustedHooks=hooks.Count,
            configurationSaved=false,mouseOrKeyboardControl=false,inferenceRequests=0}));
        dirty=false;drafts.Clear();
    }
}
