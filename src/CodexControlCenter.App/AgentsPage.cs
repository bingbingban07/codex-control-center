using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private SubagentPolicyService? subagentPolicy;
    private StackPanel? policyStatusPanel;
    private Func<Task>? verifyPolicyDraft;
    private void RenderAgents()
    {
        var s=snapshot!;subagentPolicy=new(s.CodexHome,appRoot);var original=subagentPolicy.Load(s);
        var routing=Section("子代理模型分配");
        if(s.Providers.Any(p=>p.Id=="subscription"))routing.Children.Add(Note("账号订阅已接入来源列表。选择“官方账号订阅 [subscription]”可使用自己的订阅模型；Key 1 的模型编号带 key1/ 前缀，单独计费。新目录需正常重开 Codex 后供子代理工具读取。"));
        var enabled=new ToggleSwitch{Header="启用模型分配规则",OnContent="已启用",OffContent="未启用 · 使用默认子代理配置",IsOn=original.Enabled,FontSize=bodySize};routing.Children.Add(enabled);
        var mode=Choice("分配方式",["A / B 按并发分配","默认模型 + 失败备用"],original.Mode=="failure_fallback"?"默认模型 + 失败备用":"A / B 按并发分配");routing.Children.Add(mode);
        var autoTrust=new ToggleSwitch{Header="自动记住管理台钩子授权",OnContent="保存后自动记住",OffContent="由 Codex 手动审查",IsOn=original.AutoTrustHooks,FontSize=bodySize};
        routing.Children.Add(autoTrust);autoTrust.Toggled+=(_,_)=>MarkDirty();
        routing.Children.Add(Note("适用范围：管理台的 5 个子代理钩子。固定入口与持久信任可避免版本目录变化导致反复授权。"));
        routing.Children.Add(AsyncAction("立即修复并记住钩子授权",async()=>{
            if(dirty){ShowNotice("请先保存修改","授权会使用已保存的模型分配规则。",InfoBarSeverity.Warning);return;}
            busy=true;RefreshDraftChrome();
            try{var result=await new ManagedHookTrustService(appRoot).ReconcileAsync(configuration,ResolvePythonExecutable(),lifetime.Token);busy=false;await ReloadAsync();ShowNotice(result.Verified?"已记住钩子授权":"钩子授权待确认",result.Detail,result.Verified?InfoBarSeverity.Success:InfoBarSeverity.Warning);}
            finally{busy=false;RefreshDraftChrome();}
        }));
        var rule=Note("");routing.Children.Add(rule);
        var choices=new Grid{ColumnSpacing=24,RowSpacing=24};choices.ColumnDefinitions.Add(new());choices.ColumnDefinitions.Add(new());choices.RowDefinitions.Add(new(){Height=GridLength.Auto});choices.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var a=Section("A · 优先槽位（1 个）");var b=Section("B · 其他并发子代理");
        void UpdateMode(){bool fallback=mode.SelectedIndex==1;((TextBlock)a.Children[0]).Text=fallback?"默认子代理":"A · 优先槽位（1 个）";((TextBlock)b.Children[0]).Text=fallback?"创建或接入失败后的备用":"B · 其他并发子代理";rule.Text=fallback?"所有新建子代理先用默认来源。创建错误或关闭时确认 errored 后，主代理可使用备用重新创建一次；只是较慢不切换。Hook 校验许可，不会自行创建代理。":"首位工作的子代理使用 A，其余新建子代理使用 B。A 完成后释放槽位，已有工作不更换模型。";}
        mode.SelectionChanged+=(_,_)=>UpdateMode();UpdateMode();
        Func<SubagentModelChoice> aDraft=ChoiceFields(a,original.Primary),bDraft=ChoiceFields(b,original.Overflow);
        var aCard=Card(a);choices.Children.Add(aCard);var bCard=Card(b);Grid.SetColumn(bCard,1);choices.Children.Add(bCard);routing.Children.Add(choices);
        choices.SizeChanged+=(_,_)=>{bool narrow=choices.ActualWidth<1000;Grid.SetColumn(bCard,narrow?0:1);Grid.SetRow(bCard,narrow?1:0);Grid.SetColumnSpan(aCard,narrow?2:1);Grid.SetColumnSpan(bCard,narrow?2:1);};
        routing.Children.Add(Note("启用前请结束现有子代理。保存后按上方设置记住钩子授权，重新启动 Codex 加载规则；未加载的旧会话不在全局计数内。已有 A 再次接任务时若槽位被占用，会提示等待。"));Add(routing);
        var live=Section("槽位实时状态");policyStatusPanel=new();live.Children.Add(policyStatusPanel);Add(Card(live));UpdatePolicyStatus();
        var panel=Section("未启用分流时的默认子代理");
        var model=Choice("模型",s.Models.Where(m=>m.Mapped).Select(m=>m.Id),s.AgentModel,selectFirst:false);var effort=Choice("推理强度",EffortsFor(s.AgentModel),s.AgentEffort,selectFirst:false);
        if(model.SelectedItem is null)panel.Children.Add(new InfoBar{IsOpen=true,IsClosable=false,Severity=InfoBarSeverity.Warning,Title="默认子代理模型已失效",Message=$"当前值：{s.AgentModel}。需要修改时，请明确选择模型；只修改 A/B 分流不会替换此值。"});
        bool defaultsTouched=false;model.SelectionChanged+=(_,_)=>defaultsTouched=true;effort.SelectionChanged+=(_,_)=>defaultsTouched=true;
        model.SelectionChanged+=(_,_)=>{var old=effort.SelectedItem?.ToString();ClearControlItems(effort);foreach(var e in EffortsFor(model.SelectedItem?.ToString()??""))effort.Items.Add(e);effort.SelectedItem=old;if(effort.SelectedIndex<0&&effort.Items.Count>0)effort.SelectedIndex=0;};
        var concurrent=new NumberBox{Header="每会话子代理并发上限",Value=s.AgentConcurrency,Minimum=1,Maximum=100,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,FontSize=bodySize,MinHeight=52};double last=concurrent.Value;concurrent.ValueChanged+=(_,_)=>{if(last==concurrent.Value)return;last=concurrent.Value;defaultsTouched=true;MarkDirty();};
        panel.Children.Add(model);panel.Children.Add(effort);panel.Children.Add(concurrent);panel.Children.Add(Note("上限由 Codex 宿主控制。启用分流后，Hook 会在创建入口设置 A/B 模型；主模型不受影响。"));Add(Card(panel));
        SubagentRoutingPolicy Draft()=>new(){Enabled=enabled.IsOn,AutoTrustHooks=autoTrust.IsOn,Mode=mode.SelectedIndex==1?"failure_fallback":"concurrent",Primary=aDraft(),Overflow=bDraft()};
        string Fingerprint()=>JsonSerializer.Serialize(new{enabled=enabled.IsOn,autoTrust=autoTrust.IsOn,mode=mode.SelectedIndex,a=aDraft(),b=bDraft()});string baseline=Fingerprint();
        bool toggleInitial=enabled.IsOn;enabled.Toggled+=(_,_)=>{if(enabled.IsOn==toggleInitial)return;toggleInitial=enabled.IsOn;MarkDirty();};
        planFactory=()=>{
            var plan=new ChangePlan();string next=model.SelectedItem?.ToString()??"",nextEffort=effort.SelectedItem?.ToString()??"";
            if(defaultsTouched&&(!double.IsFinite(concurrent.Value)||concurrent.Value!=Math.Truncate(concurrent.Value))){plan.Errors.Add("子代理并发上限应填写 1 到 100 之间的整数。");return plan;}
            bool defaultsChanged=defaultsTouched&&(next!=s.AgentModel||nextEffort!=s.AgentEffort||(int)concurrent.Value!=s.AgentConcurrency);
            if(defaultsChanged){
                var def=configuration.PlanDefaults(s,s.MainModel,s.MainEffort,next,nextEffort,(int)concurrent.Value);plan.Files.AddRange(def.Files);plan.Errors.AddRange(def.Errors);
                if(File.Exists(s.AgentsPath)){
                    string text=File.ReadAllText(s.AgentsPath);text=text.Replace($"`{s.AgentModel}` / `{s.AgentEffort}`",$"`{next}` / `{nextEffort}`").Replace($"`{s.AgentModel}`，推理强度为 `{s.AgentEffort}`",$"`{next}`，推理强度为 `{nextEffort}`").Replace($"model: {s.AgentModel}",$"model: {next}").Replace($"reasoning_effort: {s.AgentEffort}",$"reasoning_effort: {nextEffort}");
                    var instructions=configuration.PlanText(s,s.AgentsPath,text,"同步默认子代理配置指令");plan.Files.AddRange(instructions.Files);plan.Errors.AddRange(instructions.Errors);
                }
            }
            if(Fingerprint()!=baseline){
                var policy=subagentPolicy.Plan(s,Draft(),enabled.IsOn?ResolvePythonExecutable():"",plan.Files.FirstOrDefault(f=>f.Path==s.ConfigPath)?.Content,plan.Files.FirstOrDefault(f=>f.Path==s.AgentsPath)?.Content);
                foreach(var edit in policy.Files){plan.Files.RemoveAll(f=>string.Equals(f.Path,edit.Path,StringComparison.OrdinalIgnoreCase));plan.Files.Add(edit);}plan.Errors.AddRange(policy.Errors);
            }
            return plan;
        };
        verifyPolicyDraft=async()=>{
            enabled.IsOn=!original.Enabled;await Task.Delay(100);
            var plan=planFactory();if(original.Primary.Model==original.Overflow.Model&&plan.Errors.Count==0&&enabled.IsOn)throw new InvalidOperationException("同一 A/B 接入模型未被拒绝");
            enabled.IsOn=original.Enabled;await Task.Delay(100);dirty=false;
        };
        var tools=Section("状态维护");tools.Children.Add(AsyncAction("清理已确认结束的残留记录",async()=>{
            if(dirty){ShowNotice("存在未保存的草稿","请先应用或放弃后再维护状态。",InfoBarSeverity.Warning);return;}
            var dlg=new ContentDialog{Title="确认所有受策略管理的子代理均已结束",Content="仅当它们已全部完成或关闭时才能清理。仍在工作的子代理不会因清理而停止；错误清理可能造成同一个 A 接入被并行使用。",PrimaryButtonText="我已确认全部结束，清理记录",CloseButtonText="取消",XamlRoot=Root.XamlRoot};
            if(await ShowDialogAsync(dlg)!=ContentDialogResult.Primary)return;
            var psi=new ProcessStartInfo(ResolvePythonExecutable()){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in new[]{subagentPolicy.HookPath,"--codex-home",s.CodexHome,"--reset"})psi.ArgumentList.Add(arg);
            using var process=Process.Start(psi)??throw new InvalidOperationException("无法启动状态维护脚本");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();await output;if(process.ExitCode!=0)throw new InvalidOperationException(await error);await error;UpdatePolicyStatus();
        }));Add(tools);
        Func<SubagentModelChoice> ChoiceFields(StackPanel section,SubagentModelChoice current)
        {
            var providers=s.Providers.Where(p=>s.Models.Any(m=>m.Mapped&&m.Provider==p.Id)).ToList();
            string Label(ProviderEntry p)=>ProviderName(p)+" ["+p.Id+"]";
            var provider=Choice("接入来源 / Key",providers.Select(Label),providers.FirstOrDefault(p=>p.Id==current.Provider) is {} selected?Label(selected):"",selectFirst:false);
            string ProviderId()=>providers.FirstOrDefault(p=>Label(p)==provider.SelectedItem?.ToString())?.Id??"";
            var route=Choice("模型",s.Models.Where(m=>m.Mapped&&m.Provider==ProviderId()).Select(m=>m.Id),current.Model,selectFirst:false);
            var reasoning=Choice("推理强度",EffortsFor(route.SelectedItem?.ToString()??""),current.Effort,selectFirst:false);
            if(provider.SelectedItem is null||route.SelectedItem is null)section.Children.Add(Note("原来源或模型已不可用，请明确重新选择；保存前会校验。"));
            void UpdateEffort(){string? old=reasoning.SelectedItem?.ToString();ClearControlItems(reasoning);foreach(var e in EffortsFor(route.SelectedItem?.ToString()??""))reasoning.Items.Add(e);reasoning.SelectedItem=old;if(reasoning.SelectedIndex<0&&reasoning.Items.Count>0)reasoning.SelectedIndex=0;}
            object? providerValue=provider.SelectedItem;provider.SelectionChanged+=(_,_)=>{if(Equals(providerValue,provider.SelectedItem))return;providerValue=provider.SelectedItem;ClearControlItems(route);foreach(var m in s.Models.Where(m=>m.Mapped&&m.Provider==ProviderId()))route.Items.Add(m.Id);if(route.Items.Count>0)route.SelectedIndex=0;UpdateEffort();};
            object? routeValue=route.SelectedItem;route.SelectionChanged+=(_,_)=>{if(Equals(routeValue,route.SelectedItem))return;routeValue=route.SelectedItem;UpdateEffort();};
            section.Children.Add(provider);section.Children.Add(route);section.Children.Add(reasoning);return ()=>new(){Provider=ProviderId(),Model=route.SelectedItem?.ToString()??"",Effort=reasoning.SelectedItem?.ToString()??""};
        }
    }
    private void UpdatePolicyStatus()
    {
        if(subagentPolicy is null||policyStatusPanel is null)return;ClearPanelChildren(policyStatusPanel);
        var status=subagentPolicy.ReadStatus();var policy=subagentPolicy.Load(snapshot!);
        if(!policy.Enabled){policyStatusPanel.Children.Add(Note("全局分流未启用；默认子代理配置保持生效。"));return;}
        if(status is null||string.IsNullOrEmpty(status["last_hook_event_at"]?.ToString())){policyStatusPanel.Children.Add(Note("配置已保存，等待 Codex 新会话加载钩子。尚未观测到实际子代理事件；可使用上方按钮核对持久授权。"));return;}
        if(status["policy_revision"]?.ToString()!=policy.Revision)policyStatusPanel.Children.Add(Note("以下为上一版策略的观测记录；当前配置尚待 Hook 加载。"));
        policyStatusPanel.Children.Add(Row("受策略追踪的工作子代理",status["last_hook_event_at"]?.ToString()??"",$"{status["active_count"]??0} 个工作中 · {status["pending_count"]??0} 个待绑定"));
        policyStatusPanel.Children.Add(policy.Mode=="failure_fallback"?Note("当前为默认模型 + 失败备用。并发增加不切换来源，已有分配继续保持原模型。"):Row("A 优先槽位",status["primary_owner"]?.ToString()??"",string.IsNullOrEmpty(status["primary_owner"]?.ToString())?"空闲":"占用"));
        if(status["allocations"] is JsonArray allocations)foreach(var node in allocations.Take(10)){
            string slot=policy.Mode=="failure_fallback"?(node?["slot"]?.ToString()=="primary"?"默认":"备用"):node?["slot"]?.ToString()=="primary"?"A 优先":"B 并发";
            string state=node?["status"]?.ToString() switch{"pending"=>"创建中","active"=>"工作中","idle"=>"已完成","closed"=>"已关闭",_=>"待确认"};
            policyStatusPanel.Children.Add(Row(node?["agent_id"]?.ToString()??"待绑定",$"{node?["provider"]} · {node?["model"]} · {node?["effort"]}",$"{slot} · {state}"));
        }
    }
}
