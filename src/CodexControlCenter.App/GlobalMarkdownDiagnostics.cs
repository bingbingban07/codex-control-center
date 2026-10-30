using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyGlobalMarkdownAsync(string directory)
    {
        var s=snapshot!;string agents=Path.Combine(s.CodexHome,"AGENTS.md"),overridePath=Path.Combine(s.CodexHome,"AGENTS.override.md"),newPath=Path.Combine(s.CodexHome,"instructions","补充说明.md");
        byte[]? original=File.Exists(agents)?await File.ReadAllBytesAsync(agents):null;
        try{
            await File.WriteAllTextAsync(agents,"# 本地全局指令验证\r\n\r\n只用于隔离目录。\r\n");dirty=false;drafts.Clear();Navigate("global");await ReloadAsync();await Task.Delay(100);
            if(dirty||File.Exists(overridePath)||planFactory!().Files.Count!=0)throw new InvalidOperationException("全局文件初始化创建了文件或产生草稿");
            var editor=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="原文");
            if(MarkdownLineCount(editor.Text)<3)throw new InvalidOperationException("Markdown 原文加载后丢失换行");
            var find=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="在原文中查找");find.Text="隔离目录";verifyFindMarkdown!();
            if(dirty||editor.SelectionLength==0)throw new InvalidOperationException("查找文本改变配置或没有选中匹配内容");
            editor.Text+="\r- 第一份草稿。\r";await Task.Delay(50);
            var list=Descendants<ListView>(PageBody).First();list.SelectedItem=list.Items.OfType<ListViewItem>().First(i=>(i.Tag as KnowledgeDocumentEntry)?.Name=="AGENTS.override.md");await Task.Delay(50);
            editor.Text="# 本地临时覆盖\r\r- 第二份草稿。\r";await Task.Delay(50);
            if(!Descendants<TextBlock>(PageBody).Any(t=>t.Text.Contains("AGENTS.override.md 非空")))throw new InvalidOperationException("没有解释覆盖文件的优先级");
            verifyCreateMarkdown!("补充说明.md","instructions");await Task.Delay(50);if(MarkdownLineCount(editor.Text)<3)throw new InvalidOperationException("新建 Markdown 草稿丢失空行");editor.Text+="\r查找测试：第三份草稿。\r";await Task.Delay(50);
            if(File.Exists(newPath)||planFactory!().Files.Count!=3)throw new InvalidOperationException("新文件提前落盘或多文件草稿丢失");
            const string expectedNewContent="# 补充说明\n\n\n查找测试：第三份草稿。\n";
            if(planFactory!().Files.Single(f=>f.Path==newPath).Content!=expectedNewContent)throw new InvalidOperationException("编辑 Markdown 压缩了空行");
            var currentPlan=planFactory;Navigate("help");await FlushUiUpdatesAsync();Navigate("global");await Task.Delay(100);
            if(!dirty||!ReferenceEquals(currentPlan,planFactory)||!editor.Text.Contains("第三份草稿"))throw new InvalidOperationException("跨页丢失全局文件草稿");
            PageScroll.ChangeView(null,0,null,true);await Task.Delay(100);await CaptureAsync(Path.Combine(directory,"global-editor-drafts.png"));
            var plan=planFactory!();if(plan.Errors.Count>0||plan.Files.Any(f=>!f.Path.EndsWith(".md",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("全局编辑计划包含非 Markdown 文件");
            if(plan.Files.Single(f=>f.Path==newPath).Content!=expectedNewContent)throw new InvalidOperationException("跨页后 Markdown 空行不一致");
            busy=true;RefreshDraftChrome();ApplyResult result;
            try{result=await Task.Run(()=>configuration.ApplyAsync(editSnapshot!,plan));}finally{busy=false;RefreshDraftChrome();}
            dirty=false;drafts.Remove("documents");await ReloadAsync();
            if(!(await File.ReadAllTextAsync(newPath)).Contains("第三份草稿")||!(await File.ReadAllTextAsync(overridePath)).Contains("第二份草稿"))throw new InvalidOperationException("隔离 Markdown 文件没有按原文保存");
            if(await File.ReadAllTextAsync(newPath)!=expectedNewContent)throw new InvalidOperationException("Markdown 保存没有精确保留空行");
            string persisted=await File.ReadAllTextAsync(agents);if(!persisted.Contains("\r\n")||persisted.Replace("\r\n","").Contains('\n'))throw new InvalidOperationException("编辑 Markdown 改变了原文件的换行风格");
            await configuration.RestoreAsync(result.BackupDirectory);await ReloadAsync();
            if(File.Exists(newPath)||File.Exists(overridePath))throw new InvalidOperationException("恢复全局文件备份没有移除新文件");
        }finally{if(original is null)File.Delete(agents);else await File.WriteAllBytesAsync(agents,original);dirty=false;drafts.Clear();await ReloadAsync();}
    }
}
