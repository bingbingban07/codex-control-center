using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyUiLifecycleAsync()
    {
        dirty=false;drafts.Clear();Navigate("models");Navigate("settings");Navigate("help");await FlushUiUpdatesAsync();
        if(page!="help")throw new InvalidOperationException("快速导航没有保留最后目标页");
        Navigate("models");await FlushUiUpdatesAsync();await Task.Delay(50);
        var list=Descendants<ListView>(PageBody).First();var last=list.Items.OfType<ListViewItem>().Last();
        list.SelectedIndex=0;list.SelectedIndex=Math.Min(1,list.Items.Count-1);list.SelectedItem=last;await FlushUiUpdatesAsync();
        string expected=((CodexControlCenter.Core.ModelEntry)last.Tag).Id;
        if(Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="Codex 模型编号").Text!=expected)throw new InvalidOperationException("连续模型选择被旧回调覆盖");
        bool staleRan=false;var previous=pageInstance;QueueUiUpdate("test.stale-page",previous,()=>staleRan=true);Render();await FlushUiUpdatesAsync();
        if(staleRan)throw new InvalidOperationException("已销毁页面的回调仍然执行");
        var name=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称");name.Text+=" · 生命周期草稿";await Task.Delay(50);
        if(!dirty)throw new InvalidOperationException("生命周期保护丢失输入事件");
        var draftBody=PageBody;Navigate("settings");await FlushUiUpdatesAsync();
        if(!PageBodies.Children.Contains(draftBody)||draftBody.Visibility!=Microsoft.UI.Xaml.Visibility.Collapsed)throw new InvalidOperationException("草稿页面被拆出界面树");
        Navigate("models");await FlushUiUpdatesAsync();
        if(!ReferenceEquals(draftBody,PageBody))throw new InvalidOperationException("草稿恢复重新挂载了页面内容");
        if(!dirty||!name.Text.Contains("生命周期草稿")||!ReferenceEquals(name,Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称")))throw new InvalidOperationException("排队导航没有保留草稿控件");
        dirty=false;drafts.Clear();Navigate("settings");await FlushUiUpdatesAsync();
        var filter=Descendants<TextBox>(PageBody).First(t=>t.PlaceholderText.Contains("筛选备份"));filter.Text="2";filter.Text="20";filter.Text="no-such-backup";await FlushUiUpdatesAsync();
        if(!Descendants<TextBlock>(PageBody).Any(t=>t.Text.Contains("暂无匹配")))throw new InvalidOperationException("备份筛选没有使用最后一次输入");
        var panel=new StackPanel();for(int i=0;i<20;i++)panel.Children.Add(new TextBox{Text=i.ToString()});ClearPanelChildren(panel);
        if(panel.Children.Count!=0)throw new InvalidOperationException("逐项移除没有清空面板");
        GC.Collect(2,GCCollectionMode.Forced,false,false);await Task.Delay(250);
    }
}
