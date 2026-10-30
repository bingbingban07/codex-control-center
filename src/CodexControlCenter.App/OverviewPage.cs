using CodexControlCenter.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;

/// 概览页：待处理 → 状态读数 → 主要操作 → 当前模型 → 运行状况 → 配置细节。
public sealed partial class MainWindow
{
    private Action? overviewRefresh;
    private void RenderOverview()
    {
        var s=snapshot!;
        int mapped=s.Models.Count(m=>m.Mapped);
        int unmapped=s.Models.Count-mapped;
        var issues=s.Warnings.Where(w=>!w.Contains("no router mapping",StringComparison.OrdinalIgnoreCase)).ToList();

        if(unmapped>0||issues.Count>0)
        {
            var attention=new StackPanel{Spacing=10};
            attention.Children.Add(SectionHeader("待处理事项",$"{unmapped} 个模型尚未连接来源，{issues.Count} 条配置提示需要确认。"));
            if(unmapped>0)
            {
                attention.Children.Add(new TextBlock{Text=$"目录中还有 {unmapped} 个模型没有可用来源。",FontSize=16,Foreground=SecondaryBrush(),TextWrapping=TextWrapping.Wrap});
                attention.Children.Add(QuietAction("前往模型列表补充",()=>Navigate("models")));
            }
            foreach(var warning in issues.Take(8))attention.Children.Add(new TextBlock{Text=Safe(warning),FontSize=15,Foreground=SecondaryBrush(),TextWrapping=TextWrapping.Wrap});
            Add(Surface(attention));
        }

        Add(SectionHeader("当前状态","读数直接来自当前配置目录，文件变化后自动刷新。"));
        Add(EvenGrid(240,4,
            Metric("模型目录",$"{mapped} / {s.Models.Count}",mapped==s.Models.Count?"路由均已配置":$"{unmapped} 个待配置",mapped==s.Models.Count?"neutral":"warn"),
            Metric("接入来源",s.Providers.Count.ToString(),s.Providers.Count>0?"已配置来源":"尚未配置来源",s.Providers.Count>0?"ok":"warn"),
            Metric("个人技能",s.Skills.Count.ToString(),"在技能与指令页维护"),
            Metric("待处理",(unmapped+issues.Count).ToString(),(unmapped+issues.Count)==0?"无需处理":"需要确认",(unmapped+issues.Count)==0?"ok":"warn")));

        var shortcuts=EvenGrid(320,4,
            QuickTask("01","接入模型服务","填写来源、接口地址和独立密钥。","providers"),
            QuickTask("02","选择默认模型","设置新会话模型与推理强度。","models"),
            QuickTask("03","检查调用结果","从真实日志查找失败和来源。","requests"),
            QuickTask("04","管理技能与指令","统一维护指令、技能与角色。","documents"));

        Add(SectionHeader("模型默认配置"));
        var main=Section("默认主模型");main.Spacing=10;
        main.Children.Add(Pill("新建会话读取","ok"));
        main.Children.Add(new TextBlock{Text=s.MainModel,FontSize=26,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        var source=s.Models.FirstOrDefault(m=>m.Id==s.MainModel);
        main.Children.Add(Note($"来源：{SourceName(source?.Provider??"")} · 推理强度：{s.MainEffort}"));
        main.Children.Add(QuietAction("更换默认主模型",()=>Navigate("models")));
        var agent=Section("子代理分配");agent.Spacing=10;
        try{
            var policy=new SubagentPolicyService(s.CodexHome,appRoot).Load(s);
            if(policy.Enabled){
                bool fallback=policy.Mode=="failure_fallback";
                agent.Children.Add(Pill(fallback?"默认模型 + 失败备用":"A / B 全局分流已配置"));
                agent.Children.Add(new TextBlock{Text=(fallback?"默认 · ":"A · ")+policy.Primary.Model,FontSize=22,TextWrapping=TextWrapping.Wrap});
                agent.Children.Add(Note((fallback?"所有新建子代理 · ":"优先 1 个工作槽位 · ")+SourceName(policy.Primary.Provider)));
                agent.Children.Add(new TextBlock{Text=(fallback?"备用 · ":"B · ")+policy.Overflow.Model,FontSize=22,TextWrapping=TextWrapping.Wrap});
                agent.Children.Add(Note((fallback?"明确创建或接入失败后一次替代 · ":"其余新建子代理 · ")+SourceName(policy.Overflow.Provider)));
            }else{
                agent.Children.Add(Pill("使用默认子代理"));
                agent.Children.Add(new TextBlock{Text=s.AgentModel,FontSize=22,TextWrapping=TextWrapping.Wrap});
                agent.Children.Add(Note($"推理强度：{s.AgentEffort} · 每会话并发上限：{s.AgentConcurrency}"));
            }
        }catch(Exception e){agent.Children.Add(Pill("策略需要检查","warn"));agent.Children.Add(Note(Safe(e.Message)));}
        agent.Children.Add(QuietAction("查看分配与槽位",()=>Navigate("agents")));
        Add(EvenGrid(420,2,Surface(main),Surface(agent)));

        Add(SectionHeader("运行状况"));
        var local=Section("本地服务与最近观测");local.Spacing=10;
        healthPanel=new();healthPanel.Children.Add(Note("正在读取本地服务状态…"));local.Children.Add(healthPanel);
        var activity=Text("");local.Children.Add(activity);
        local.Children.Add(Note("本地服务在线表示路由可达；模型是否成功调用，请看对应的真实请求结果。"));
        var providers=Section("接入来源");providers.Spacing=10;
        providerPanel=new();providers.Children.Add(providerPanel);providers.Children.Add(QuietAction("管理接口与密钥",()=>Navigate("providers")));
        Add(EvenGrid(420,2,Surface(local),Surface(providers)));
        overviewRefresh=()=>{
            var failures=recentRequests.Count(r=>IsFailure(r.Status));
            activity.Text=$"最近采样 {recentRequests.Count} 条记录 · {failures} 条错误或取消 · 不同日志来源分开计数";
        };
        overviewRefresh();

        Add(SectionHeader("快捷操作","也可按 Ctrl+K 查找页面、模型和接入来源。"));Add(shortcuts);

        Add(SectionHeader("配置细节"));
        var image=Section("默认图片服务");image.Spacing=10;
        try{image.Children.Add(new TextBlock{Text=images!.ActiveProvider=="official"?"官方 OpenAI Images API":"i2m / 114514",FontSize=22,TextWrapping=TextWrapping.Wrap});image.Children.Add(Pill("保存配置后手动生成"));}
        catch(Exception e){image.Children.Add(Pill("配置需要检查","warn"));image.Children.Add(Note(Safe(e.Message)));}
        image.Children.Add(Note("默认服务只在手动提交时使用。"));image.Children.Add(QuietAction("配置图片服务",()=>Navigate("images")));Add(Surface(image));

        if(lastSaveAt is not null){
            var saved=Section("最近一次保存");saved.Spacing=10;
            saved.Children.Add(Pill(lastSaveAt.Value.ToString("HH:mm:ss")+" · 文件已写入","ok"));
            foreach(string effect in lastSaveEffects)saved.Children.Add(Note(effect));
            if(lastSaveBackup is not null)saved.Children.Add(Note("已创建可恢复备份，可到设置查看。"));
            saved.Children.Add(QuietAction("查看设置与备份",()=>Navigate("settings")));Add(Surface(saved));
        }
    }
    private string SourceName(string id)=>snapshot?.Providers.FirstOrDefault(p=>p.Id==id) is {} p?ProviderName(p):string.IsNullOrWhiteSpace(id)?"尚未配置":id;

    /// 主要入口磁贴：编号 + 标题 + 说明 + 跳转链接。
    private Border QuickTask(string index,string title,string description,string target)
    {
        var content=new StackPanel{Spacing=8};
        var badge=new Border{Child=new TextBlock{Text=index,FontSize=13,FontWeight=FontWeights.SemiBold,Foreground=AccentBrush(),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center},Width=30,Height=30,CornerRadius=new CornerRadius(9),Background=Tint(AccentColor(),24),BorderBrush=Tint(AccentColor(),80),BorderThickness=new Thickness(1),HorizontalAlignment=HorizontalAlignment.Left};
        content.Children.Add(badge);
        content.Children.Add(new TextBlock{Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new TextBlock{Text=description,FontSize=16,Foreground=SecondaryBrush(),TextWrapping=TextWrapping.Wrap});
        content.Children.Add(QuietAction("前往"+PageName(target),()=>Navigate(target)));
        return Surface(content);
    }
}
