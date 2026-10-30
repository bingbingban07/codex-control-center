using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private Func<Task>? verifySettingsDraft;
    private void RenderSettings()
    {
        var s=snapshot!;var prefs=ReadPreferences();
        RenderEnhancementSettings();
        var appearance=Section("显示与监测");
        var density=Choice("界面密度",["舒适 · 18 DIP","更大 · 20 DIP"],bodySize>18?"更大 · 20 DIP":"舒适 · 18 DIP");
        var theme=Choice("颜色主题",["跟随系统","浅色","深色"],Root.RequestedTheme switch{ElementTheme.Light=>"浅色",ElementTheme.Dark=>"深色",_=>"跟随系统"});
        var interval=Choice("前台本地状态刷新",["3 秒","5 秒","10 秒"],$"{monitorSeconds} 秒");
        foreach(var c in new UIElement[]{density,theme,interval})appearance.Children.Add(c);
        appearance.Children.Add(Note("后台检查自动放慢至至少 15 秒。仅检查本地服务；不会自动调用模型或生图。"));Add(Card(appearance));
        var paths=Section("环境与工具");var home=Input("Codex 配置目录（CODEX_HOME）",s.CodexHome);var python=Input("Python 可执行文件（留空自动发现）",prefs["pythonExecutable"]?.ToString()??"");
        paths.Children.Add(home);paths.Children.Add(python);paths.Children.Add(Note("所有设置统一使用底部“保存设置”。切换 CODEX_HOME 前请先处理其他页面草稿。"));
        paths.Children.Add(Row("模型目录",s.CatalogPath,"Codex 启动时加载"));paths.Children.Add(Row("本地路由",s.RouterPath,"运行同步状态见概览"));Add(Card(paths));
        localSave=async()=>{
            string nextHome=Path.GetFullPath(home.Text.Trim());string nextPython=python.Text.Trim();
            if(nextPython.Length>0&&(!Path.IsPathRooted(nextPython)||!File.Exists(nextPython)))throw new InvalidOperationException("Python 路径不存在，请填写 python.exe 的绝对路径。");
            bool homeChanged=!string.Equals(nextHome,s.CodexHome,StringComparison.OrdinalIgnoreCase);
            if(homeChanged&&(imageOperationRunning||drafts.Any(d=>d.Value.HasChanges)))throw new InvalidOperationException("请先保存其他页面草稿，并等待或取消图片任务，再切换配置目录。");
            var service=homeChanged?new ConfigurationService(nextHome,stateHome):configuration;
            if(homeChanged)await Task.Run(()=>service.LoadAsync());
            var next=ReadPreferences();next["density"]=density.SelectedIndex==1?"large":"comfortable";next["theme"]=theme.SelectedIndex switch{1=>"light",2=>"dark",_=>"system"};next["pythonExecutable"]=nextPython;next["codexHome"]=nextHome;next["monitorSeconds"]=interval.SelectedIndex switch{1=>5,2=>10,_=>3};
            WritePreferences(next);bodySize=density.SelectedIndex==1?20:18;monitorSeconds=interval.SelectedIndex switch{1=>5,2=>10,_=>3};Root.RequestedTheme=theme.SelectedIndex switch{1=>ElementTheme.Light,2=>ElementTheme.Dark,_=>ElementTheme.Default};
            if(homeChanged){configuration=service;watcher?.Dispose();watcher=null;}
            return true;
        };
        var history=Section("配置备份");history.Children.Add(Note("恢复前会再次备份当前状态。外部配置已变更时拒绝覆盖，可先查看差异。密钥只以加密文件存在于备份。"));
        var filter=new TextBox{PlaceholderText="按日期或变更内容筛选备份",FontSize=bodySize,MinHeight=52};history.Children.Add(filter);var list=new StackPanel{Spacing=14};history.Children.Add(list);Add(history);
        void Populate(){ClearPanelChildren(list);var values=configuration.ListBackups().Where(b=>(b.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")+b.Summary).Contains(filter.Text,StringComparison.OrdinalIgnoreCase)).Take(30).ToList();
            if(values.Count==0){list.Children.Add(Note("暂无匹配的配置备份。首次应用变更后自动生成。"));return;}
            foreach(var entry in values){var item=Section(entry.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));item.Children.Add(Note(entry.Summary));item.Children.Add(AsyncAction("查看并恢复",async()=>{
                if(HasDrafts){ShowNotice("请先处理草稿","恢复配置前需要保存或放弃所有页面草稿。",InfoBarSeverity.Warning);return;}
                var dialog=new ContentDialog{Title="恢复此配置版本",Content=new ScrollViewer{Content=Text(entry.Summary+"\n\n"+entry.Directory+"\n\n恢复前备份当前状态，并检查外部修改冲突。相关服务可能需要重启。"),MaxHeight=400},PrimaryButtonText="恢复并备份当前状态",CloseButtonText="取消",XamlRoot=Root.XamlRoot};if(await ShowDialogAsync(dialog)!=ContentDialogResult.Primary)return;
                // The confirmation dialog awaits user input; recheck drafts created while it was open.
                if(HasDrafts){ShowNotice("请先处理草稿","恢复配置前需要保存或放弃所有页面草稿。",InfoBarSeverity.Warning);return;}
                busy=true;operationPhase="write";RefreshDraftChrome();try{await Task.Run(()=>configuration.RestoreAsync(entry.Directory));RecordSave(["已恢复所选备份；请按配置的生效范围重新加载对应服务。"]);}finally{busy=false;operationPhase="";RefreshDraftChrome();}
                if(await ReloadAsync())ShowNotice("已恢复配置","请根据生效范围重新加载相关服务。",InfoBarSeverity.Success);
            }));list.Children.Add(Card(item));}
        }var owner=pageInstance;filter.TextChanged+=(_,_)=>QueueUiUpdate("settings.backups",owner,Populate);Populate();
        var about=Section("Codex 管理台 0.9.0");about.Children.Add(Note("原生 WinUI 3 · Windows x64 · 手绘几何图标"));
        about.Children.Add(AsyncAction("导出脱敏配置摘要",async()=>{
            string path=Path.Combine(stateHome,"exports","codex-config-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{s.MainModel,s.MainEffort,s.AgentModel,s.AgentEffort,s.AgentConcurrency,models=s.Models,providers=s.Providers.Select(p=>new{p.Id,p.Name,p.BaseUrl,p.AuthMode}),skills=s.Skills.Select(k=>new{k.Name,k.Source}),s.Warnings},new JsonSerializerOptions{WriteIndented=true}));SetFooter("已导出："+path);
        }));Add(about);
        verifySettingsDraft=async()=>{density.SelectedIndex=density.SelectedIndex==0?1:0;await Task.Delay(100);if(!dirty||localSave is null||planFactory is not null)throw new InvalidOperationException("统一设置草稿未建立");dirty=false;};
    }
}
