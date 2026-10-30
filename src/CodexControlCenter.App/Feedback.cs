using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private void ShowProblem(string context,Exception error)
    {
        var problem=UserFeedback.Explain(error);
        ShowNotice(problem.Title,problem.Message+"\n"+problem.NextStep,InfoBarSeverity.Error);
        Banner.ActionButton=AsyncAction("查看详情与处理",async()=>{
            var panel=Section();panel.Children.Add(Text(problem.Message));panel.Children.Add(Note(problem.NextStep));
            panel.Children.Add(new TextBox{Header="技术详情（敏感内容已隐藏）",Text=problem.Detail,IsReadOnly=true,AcceptsReturn=true,TextWrapping=Microsoft.UI.Xaml.TextWrapping.Wrap,MaxHeight=260,FontSize=15});
            var dialog=new ContentDialog{Title=problem.Title,Content=panel,CloseButtonText="关闭",XamlRoot=Root.XamlRoot};
            if(problem.Action=="settings")dialog.PrimaryButtonText="前往设置与备份";
            if(problem.Action=="compare")dialog.PrimaryButtonText="对比最新内容";
            if(await ShowDialogAsync(dialog)==ContentDialogResult.Primary){if(problem.Action=="settings")Navigate("settings");else await CompareCurrentDraftAsync();}
        });
    }
    private async Task CompareCurrentDraftAsync()
    {
        if(planFactory is null){ShowNotice("当前页面没有配置草稿","可以到“设置与备份”查看配置目录。");return;}
        var plan=planFactory();var panel=Section();panel.Children.Add(Note("以下仅用于对比，不会写入文件。草稿与最新配置存在冲突时，需重新载入后调整。"));
        foreach(var edit in plan.Files){var item=Section(Path.GetFileName(edit.Path));item.Children.Add(Note(edit.Path));item.Children.Add(Note(edit.Path.EndsWith(".dpapi",StringComparison.OrdinalIgnoreCase)?"加密凭据内容不显示。":DescribeDifference(File.Exists(edit.Path)?File.ReadAllText(edit.Path):"",edit.Content)));panel.Children.Add(item);}
        if(plan.Files.Count==0)panel.Children.Add(Note("尚未生成可对比内容，请先修正表单中的必填项。"));
        var dialog=new ContentDialog{Title="草稿与磁盘最新内容",Content=new ScrollViewer{Content=panel,MaxHeight=600},CloseButtonText="继续编辑",SecondaryButtonText="放弃本页并载入最新",XamlRoot=Root.XamlRoot};
        if(await ShowDialogAsync(dialog)==ContentDialogResult.Secondary)await DiscardCurrentDraftAsync();
    }
    private void ShowValidationErrors(IEnumerable<string> errors)
    {
        var messages=errors.Select(UserFeedback.TranslateValidation).Distinct().ToList();ShowNotice("还需要调整这些内容",string.Join("\n",messages.Select((m,i)=>$"{i+1}. {m}")),InfoBarSeverity.Error);
    }
    private static string ChangeName(FileEdit edit)
    {
        string file=Path.GetFileName(edit.Path);
        if(file.EndsWith(".dpapi"))return "更新加密密钥";
        return file switch{"config.toml"=>"更新 Codex 默认配置","AGENTS.md"=>"更新任务行为说明","router.json"=>"更新服务接入或模型连接","hooks.json"=>"更新自动分配规则入口","image-profiles.json"=>"更新默认生图来源","subagent-policy.json"=>"更新子代理 A/B 分配", "SKILL.md"=>"更新个人技能",_=>UserFeedback.Redact(edit.Description.Replace("Update model catalog entry","更新模型目录：").Replace("Update route for","更新模型连接："))};
    }
    private static string ChangeEffect(FileEdit edit)
    {
        string file=Path.GetFileName(edit.Path);
        if(file.EndsWith(".dpapi"))return "本地路由自动加载或下次生图时读取；保存后另行确认运行状态。";
        if(file=="config.json"&&edit.Path.Contains("i2m-imagegen"))return "下次手动生图时使用。";
        return file switch{"config.toml"=>"以后新建会话读取；已打开的会话可能有自己的选择。","router.json"=>"支持自动加载的服务在后续请求前生效；保存后核对运行配置，旧服务需要重启。","hooks.json"=>"Codex 重新加载并信任新规则后生效。","image-profiles.json"=>"下次手动生图时使用。","subagent-policy.json"=>"规则被加载后用于新建子代理，已有任务保持原模型。","AGENTS.md"=>"新任务读取；正在工作的任务保持原指令。",_=>edit.Effect.Contains("重启")?"重新启动对应程序后生效。":UserFeedback.Redact(edit.Effect)};
    }
}
