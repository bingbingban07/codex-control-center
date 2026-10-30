using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyModernInteractionsAsync(string directory)
    {
        dirty=false;drafts.Clear();Navigate("models");await FlushUiUpdatesAsync();
        var initial=snapshot!.Models.First(m=>m.Id==snapshot.MainModel);
        var alternate=snapshot.Models.FirstOrDefault(m=>m.Id!=initial.Id&&m.Mapped);
        if(alternate is null)throw new InvalidOperationException("Need two mapped models for multi-editor diagnostics");
        TextBox InputField(string header)=>Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()==header);
        ListView ModelList()=>Descendants<ListView>(PageBody).First();
        async Task Expand(){Descendants<Expander>(PageBody).First(e=>e.Header?.ToString()=="模型能力与高级设置").IsExpanded=true;await Task.Delay(180);}
        async Task Select(string id){
            ModelList().SelectedItem=ModelList().Items.OfType<ListViewItem>().First(i=>(i.Tag as ModelEntry)?.Id==id);
            await FlushUiUpdatesAsync();
        }
        string initialName=InputField("显示名称").Text;
        await Expand();
        InputField("显示名称").Text=initialName+" · 第一模型草稿";
        if(!dirty)throw new InvalidOperationException("Synchronous draft capture did not precede navigation");
        InputField("上下文长度（Token）").Text="invalid raw value";await FlushUiUpdatesAsync();
        await Select(alternate.Id);
        string otherName=InputField("显示名称").Text;
        InputField("显示名称").Text=otherName+" · 第二模型草稿";await FlushUiUpdatesAsync();
        if(planFactory!().Errors.Count==0)throw new InvalidOperationException("Invalid first model was lost after switching");
        await Select(initial.Id);
        await Expand();
        if(InputField("上下文长度（Token）").Text!="invalid raw value"||InputField("显示名称").Text!=initialName+" · 第一模型草稿")
            throw new InvalidOperationException("Model editor failed to preserve exact raw draft");
        InputField("上下文长度（Token）").Text=initial.ContextWindow.ToString();await FlushUiUpdatesAsync();
        var plan=planFactory!();if(plan.Errors.Count!=0||plan.Files.Select(f=>f.Path).Distinct().Count()!=plan.Files.Count)throw new InvalidOperationException("Batch model plan failed");
        var catalog=System.Text.Json.Nodes.JsonNode.Parse(plan.Files.Single(f=>f.Path==snapshot.CatalogPath).Content)!["models"]!.AsArray();
        if(!catalog.Any(n=>n!["display_name"]?.ToString()==initialName+" · 第一模型草稿")||!catalog.Any(n=>n!["display_name"]?.ToString()==otherName+" · 第二模型草稿"))throw new InvalidOperationException("Batch omitted a model draft");
        Navigate("help");await FlushUiUpdatesAsync();
        await OpenNavigationTargetAsync(new(initial.DisplayName,"fixture","models",initial.Id));
        if(InputField("显示名称").Text!=initialName+" · 第一模型草稿"||!dirty)throw new InvalidOperationException("Quick navigation lost model drafts");
        var palette=(Microsoft.UI.Xaml.Media.SolidColorBrush)ToneBrush("ok");var oldTheme=Root.RequestedTheme;
        Root.RequestedTheme=Root.ActualTheme==Microsoft.UI.Xaml.ElementTheme.Dark?Microsoft.UI.Xaml.ElementTheme.Light:Microsoft.UI.Xaml.ElementTheme.Dark;
        await Task.Delay(150);if(!ReferenceEquals(palette,ToneBrush("ok"))||palette.Color!=ToneColor("ok"))throw new InvalidOperationException("Theme change left retained draft status colors stale");
        Root.RequestedTheme=oldTheme;await Task.Delay(100);
        await CaptureAsync(Path.Combine(directory,"models-batch-drafts.png"));
        dirty=false;drafts.Clear();Navigate("overview");await FlushUiUpdatesAsync();
        var arrived=new TaskCompletionSource<ContentDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        diagnosticDialogShown=dialog=>arrived.TrySetResult(dialog);
        var show=ShowQuickNavigationAsync();var dialog=await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try{
            await Task.Delay(200);var query=Descendants<TextBox>(dialog).First();query.Text="模型列表";await Task.Delay(150);
            var list=Descendants<ListView>(dialog).First();if(list.Items.Count==0||(list.SelectedItem as ListViewItem)?.Tag is not NavigationTarget{Page:"models"})throw new InvalidOperationException("Quick search failed to rank page");
            list.UpdateLayout();await WaitForUiConditionAsync(()=>list.ContainerFromIndex(0) is Microsoft.UI.Xaml.FrameworkElement row&&row.ActualHeight>0,"Quick search result not rendered");
            await CaptureAsync(Path.Combine(directory,"quick-search.png"),dialog);
            query.Text="no-results-local-fixture";await Task.Delay(100);if(list.Items.Count!=0||dialog.IsPrimaryButtonEnabled)throw new InvalidOperationException("Quick search empty state is actionable");
        }finally{dialog.Hide();await show;diagnosticDialogShown=null;}
        if(dirty||HasDrafts)throw new InvalidOperationException("Read-only quick search created a draft");
    }
}
