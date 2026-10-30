using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyImmediateDraftsAsync(string directory)
    {
        dirty=false;drafts.Clear();Navigate("providers");await FlushUiUpdatesAsync();
        var name=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称");
        name.Text+=" · 即时输入检查";
        if(!dirty)throw new InvalidOperationException("Provider text was not captured synchronously");
        Navigate("help");await FlushUiUpdatesAsync();Navigate("providers");await FlushUiUpdatesAsync();
        if(!dirty||!ReferenceEquals(name,Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称")))throw new InvalidOperationException("Immediate provider navigation lost the editor");
        dirty=false;drafts.Clear();Navigate("help");await FlushUiUpdatesAsync();Navigate("providers");await FlushUiUpdatesAsync();
        var password=Descendants<PasswordBox>(PageBody).First();
        // This is synthetic, remains in memory, and no save or network action follows it.
        password.Password="synthetic-native-draft-only";
        if(!dirty)throw new InvalidOperationException("Credential entry did not immediately protect its page draft");
        Navigate("help");await FlushUiUpdatesAsync();Navigate("providers");await FlushUiUpdatesAsync();
        if(!dirty||!ReferenceEquals(password,Descendants<PasswordBox>(PageBody).First()))throw new InvalidOperationException("Immediate credential navigation discarded the in-memory draft");
        password.Password="";dirty=false;drafts.Clear();

        Navigate("documents");await FlushUiUpdatesAsync();selectDocumentPath!(snapshot!.AgentsPath);await FlushUiUpdatesAsync();
        var editor=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="原文");
        editor.Text+="\r即时切页时保留文档草稿。\r";
        if(!dirty)throw new InvalidOperationException("Document draft was not captured before navigation");
        Navigate("help");await FlushUiUpdatesAsync();Navigate("documents");await FlushUiUpdatesAsync();
        if(!dirty||!ReferenceEquals(editor,Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="原文")))throw new InvalidOperationException("Immediate document navigation lost the draft");
        dirty=false;drafts.Clear();

        Navigate("images");await FlushUiUpdatesAsync();var prompt=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="测试提示词");
        string originalPrompt=prompt.Text;prompt.Text="即时切页提示词检查，不生成图片。";
        Navigate("help");await FlushUiUpdatesAsync();Navigate("images");await FlushUiUpdatesAsync();
        var restored=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="测试提示词");
        if(restored.Text!="即时切页提示词检查，不生成图片。"||dirty)throw new InvalidOperationException("Immediate image prompt navigation lost text or marked configuration dirty");
        restored.Text=originalPrompt;

        // A new user with no configured sources must get an operable form and an empty-state guide.
        dirty=false;drafts.Clear();Navigate("providers");await FlushUiUpdatesAsync();
        var s=snapshot!;var sources=s.Providers;
        try{
            s.Providers=[];Render();await FlushUiUpdatesAsync();
            var tiles=Descendants<Button>(PageBody).Where(b=>AutomationProperties.GetAutomationId(b).StartsWith("provider-tile-",StringComparison.Ordinal)).ToList();
            if(tiles.Count!=1||!Descendants<TextBlock>(PageBody).Any(t=>t.Text=="还没有接入任何来源"))throw new InvalidOperationException("First-use provider guide is missing");
            var url=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="接口地址（Base URL）");url.Text="https://example.invalid/v1";
            if(!dirty||planFactory!().Errors.Count==0)throw new InvalidOperationException("First-use draft failed to require a credential");
            await CaptureAsync(Path.Combine(directory,"providers-first-use.png"));
        }finally{s.Providers=sources;dirty=false;drafts.Clear();Render();}
    }
}
