using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyMarkdownSourcesAsync(string directory)
    {
        var s=snapshot!;string personal=Path.Combine(s.CodexHome,"skills","example","SKILL.md"),managed=Path.Combine(s.CodexHome,"skills",".system","example","SKILL.md");
        foreach(string file in new[]{personal,managed}){Directory.CreateDirectory(Path.GetDirectoryName(file)!);await File.WriteAllTextAsync(file,"# 本地技能来源验证\n");}
        string project=Path.Combine(directory,"项目指令验证"),root=Path.Combine(project,"AGENTS.md"),nested=Path.Combine(project,"src","AGENTS.md");
        foreach(string file in new[]{root,nested}){Directory.CreateDirectory(Path.GetDirectoryName(file)!);await File.WriteAllTextAsync(file,"# 本地项目指令\n");}
        dirty=false;drafts.Clear();Navigate("global");await FlushUiUpdatesAsync();await ReloadAsync();await Task.Delay(50);
        selectDocumentCategory!(DocumentCategory.Skills);await FlushUiUpdatesAsync();
        var source=Descendants<ComboBox>(PageBody).First(c=>c.Header?.ToString()=="文件来源");
        if(!source.Items.Contains("个人技能")||!source.Items.Contains("系统技能"))throw new InvalidOperationException("没有显示个人/系统技能来源");
        source.SelectedItem="系统技能";await FlushUiUpdatesAsync();var list=Descendants<ListView>(PageBody).First();
        list.SelectedIndex=0;await FlushUiUpdatesAsync();var editor=Descendants<TextBox>(PageBody).First(c=>c.Header?.ToString()=="原文");
        if(!editor.IsReadOnly||dirty||planFactory!().Files.Count!=0)throw new InvalidOperationException("系统技能文档没有保持只读");
        if(verifyAddMarkdownFolder is null)throw new InvalidOperationException("没有项目目录导入入口");
        await verifyAddMarkdownFolder(project);await FlushUiUpdatesAsync();
        list=Descendants<ListView>(PageBody).First();if(list.Items.Count!=2||list.Items.OfType<ListViewItem>().Select(i=>((KnowledgeDocumentEntry)i.Tag).Name).Distinct().Count()!=2)throw new InvalidOperationException("同名项目 AGENTS 文件未同时列出");
        list.SelectedIndex=1;await FlushUiUpdatesAsync();
        if(editor.IsReadOnly||!editor.Text.Contains("本地项目指令"))throw new InvalidOperationException("项目指令没有可编辑原文");
        await CaptureAsync(Path.Combine(directory,"markdown-project-sources.png"));
        await ReloadAsync();selectDocumentCategory!(DocumentCategory.Instructions);source=Descendants<ComboBox>(PageBody).First(c=>c.Header?.ToString()=="文件来源");
        if(!source.Items.Contains("项目指令"))throw new InvalidOperationException("刷新后丢失已添加项目来源");
    }
}
