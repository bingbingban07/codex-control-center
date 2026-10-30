using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    // Called only inside VerifyHomeSwitchAsync's isolated, synthetic CODEX_HOME.
    private async Task VerifyKnowledgeDocumentsAsync(string directory)
    {
        var s=snapshot!;string agents=s.AgentsPath;
        string role=Path.Combine(s.CodexHome,"agents","local-reviewer.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(role)!);await File.WriteAllTextAsync(role,"name = \"Local reviewer\"\n# keep role comment\n");
        dirty=false;drafts.Clear();Navigate("global");await FlushUiUpdatesAsync();await ReloadAsync();await FlushUiUpdatesAsync();
        if(page!="documents"||Navigation.MenuItems.OfType<NavigationViewItem>().Count(i=>Equals(i.Tag,"documents"))!=1||Navigation.MenuItems.OfType<NavigationViewItem>().Any(i=>i.Tag?.ToString() is "global" or "skills"))throw new InvalidOperationException("文档导航没有唯一入口");
        TextBox Editor()=>Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="原文");
        var editor=Editor();selectDocumentPath!(agents);editor.Text+="\r- 合并指令草稿。\r";await FlushUiUpdatesAsync();
        Navigate("skills");await FlushUiUpdatesAsync();
        if(page!="documents"||documentsCategory!=DocumentCategory.Skills||!ReferenceEquals(editor,Editor()))throw new InvalidOperationException("旧技能入口未定位分类或重建编辑控件");
        verifyCreateSkill!("merged-local-skill");string skill=Path.Combine(s.CodexHome,"skills","merged-local-skill","SKILL.md");editor.Text+="\r合并技能草稿。\r";await FlushUiUpdatesAsync();
        selectDocumentPath!(role);editor.Text+="\rdescription = \"Merged role draft\"\r";await FlushUiUpdatesAsync();
        var plan=planFactory!();if(plan.Errors.Count>0||plan.Files.Count!=3||File.Exists(skill))throw new InvalidOperationException("三个分类未合成同一内存草稿计划："+string.Join(";",plan.Errors));
        string validRole=editor.Text;editor.Text="name = [";await FlushUiUpdatesAsync();
        if(planFactory!().Errors.Count==0)throw new InvalidOperationException("无效 TOML 未阻止全部分类保存");
        editor.Text=validRole;await FlushUiUpdatesAsync();
        if(!ApplyButton.Content.ToString()!.Contains("全部草稿"))throw new InvalidOperationException("保存范围未清楚说明");
        var texts=plan.Files.ToDictionary(f=>f.Path,f=>f.Content,StringComparer.OrdinalIgnoreCase);
        var preservedPlan=planFactory;Navigate("help");await FlushUiUpdatesAsync();
        if(DraftTargets().Count(k=>k=="documents")!=1)throw new InvalidOperationException("文档分类被重复计入草稿清单");
        Navigate("documents");await FlushUiUpdatesAsync();
        if(!dirty||!ReferenceEquals(editor,Editor())||!ReferenceEquals(preservedPlan,planFactory)||!editor.Text.Contains("Merged role draft"))throw new InvalidOperationException("跨页恢复未保留分类、草稿和控件");
        var search=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="搜索文件");search.Text="no-such-document";await FlushUiUpdatesAsync();
        if(Descendants<ListView>(PageBody).First().Items.Count!=0||!editor.IsReadOnly||planFactory!().Files.Count!=3)throw new InvalidOperationException("空搜索结果未清空编辑器或丢失草稿");
        search.Text="";await FlushUiUpdatesAsync();selectDocumentPath!(role);
        string? previewTitle=null;
        diagnosticDialogShown=dialog=>{previewTitle=dialog.Title?.ToString();DispatcherQueue.TryEnqueue(async()=>{await Task.Delay(150);dialog.Hide();});};
        try{if(await ApplyCurrentAsync())throw new InvalidOperationException("取消预览仍报告保存成功");}
        finally{diagnosticDialogShown=null;}
        if(previewTitle!="确认保存 3 个文件"||!dirty||planFactory!().Files.Count!=3||File.Exists(skill))throw new InvalidOperationException("取消统一预览未保留所有草稿");
        int switches=0,restores=0;
        for(int i=0;i<40;i++){
            foreach(string path in new[]{agents,skill,role}){selectDocumentPath!(path);await FlushUiUpdatesAsync();if(!ReferenceEquals(editor,Editor()))throw new InvalidOperationException("切换分类重建编辑器");switches++;}
            if(i%5==0){Navigate("help");await FlushUiUpdatesAsync();Navigate("documents");await FlushUiUpdatesAsync();if(!ReferenceEquals(editor,Editor())||!dirty)throw new InvalidOperationException("压力切换丢失草稿");restores++;}
        }
        foreach(var file in planFactory!().Files)if(texts[file.Path]!=file.Content)throw new InvalidOperationException("切换导致草稿串入其他文档");
        foreach(var category in Enum.GetValues<DocumentCategory>()){
            selectDocumentCategory!(category);await FlushUiUpdatesAsync();PageScroll.ChangeView(null,0,null,true);await Task.Delay(100);
            await CaptureAsync(Path.Combine(directory,"documents-"+category.ToString().ToLowerInvariant()+".png"));
        }
        plan=planFactory!();var applied=await configuration.ApplyAsync(editSnapshot!,plan);dirty=false;drafts.Remove("documents");await ReloadAsync();
        foreach(var file in plan.Files)if(await File.ReadAllTextAsync(file.Path)!=file.Content)throw new InvalidOperationException("统一保存内容未精确写入");
        await configuration.RestoreAsync(applied.BackupDirectory);await ReloadAsync();
        if(File.Exists(skill)||(await File.ReadAllTextAsync(role)).Contains("Merged role draft"))throw new InvalidOperationException("统一备份未恢复全部分类");
        await File.WriteAllTextAsync(Path.Combine(directory,"documents-switch-stress.json"),JsonSerializer.Serialize(new{success=true,switches,draftRestores=restores,sharedEditor=true,cancelPreviewPreservesAllDrafts=true,allCategorySaveAndRestore=true}));
        dirty=false;drafts.Clear();
    }
}
