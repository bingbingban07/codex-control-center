using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyVisualOperationsAsync(string directory)
    {
        dirty=false;drafts.Clear();Navigate("models");await Task.Delay(100);
        var input=Descendants<TextBox>(PageBody).First(c=>c.Header?.ToString()=="显示名称");input.Text+=" · 操作状态检查";await Task.Delay(100);
        if(!OperationLabel.Text.Contains("草稿未保存")||WorkflowPanel.Visibility!=Visibility.Visible||!ApplyButton.IsEnabled||!DraftTargets().Contains("models"))throw new InvalidOperationException("编辑状态/步骤或草稿清单与表单不同步");
        Navigate("settings");await Task.Delay(100);var density=Descendants<ComboBox>(PageBody).First(c=>c.Header?.ToString()=="界面密度");density.SelectedIndex=density.SelectedIndex==0?1:0;await Task.Delay(100);
        if(DraftTargets().Count!=2||!DraftsButton.IsEnabled)throw new InvalidOperationException("跨页草稿清单未显示全部草稿");
        await CaptureAsync(Path.Combine(directory,"draft-workflow.png"));
        var (draftDialog,draftOptions)=BuildDraftDialog();var shown=ShowDialogAsync(draftDialog);
        try{await Task.Delay(300);if(draftOptions.Items.Count!=2||draftDialog.ActualWidth<=0||draftDialog.ActualTheme!=Root.ActualTheme)throw new InvalidOperationException("草稿弹窗条目数、主题或布局不符");await CaptureAsync(Path.Combine(directory,"draft-list.png"),draftDialog);}
        finally{draftDialog.Hide();await shown;}
        var before=snapshot;dialogActive=true;
        try{
            if(await ReloadAsync()||!ReferenceEquals(before,snapshot))throw new InvalidOperationException("弹窗打开期间仍替换了页面快照");
            Navigate("help");if(page!="settings")throw new InvalidOperationException("弹窗期间页面仍可切换");
        }finally{dialogActive=false;dirty=false;drafts.Clear();RefreshDraftChrome();}

        Navigate("requests");await Task.Delay(100);await refreshGate.WaitAsync();var saved=recentRequests;
        try{
            recentRequests=[new("本地演示模型 A","a","200","120 ms","本地合成记录","界面验证数据，非真实调用",RequestId:"demo-success"),
                new("本地演示模型 B","b","401","80 ms","本地合成记录","界面验证数据，非真实调用",RequestId:"demo-auth"),
                new("本地演示模型 C","a","429","90 ms","本地合成记录","界面验证数据，非真实调用",RequestId:"demo-limit"),
                new("本地演示模型 D","a","未采集","未采集","未采集","界面验证数据，非真实调用")];
            requestRender?.Invoke();await Task.Delay(100);
            var row=Descendants<Expander>(PageBody).First();row.IsExpanded=true;await Task.Delay(100);requestRender?.Invoke();
            if(!ReferenceEquals(row,Descendants<Expander>(PageBody).First())||!row.IsExpanded)throw new InvalidOperationException("不变的请求数据重建控件或丢失展开状态");
            if(!Descendants<TextBlock>(PageBody).Any(t=>t.Text=="1 条成功")||!Descendants<TextBlock>(PageBody).Any(t=>t.Text=="2 条错误或取消"))throw new InvalidOperationException("采样统计与记录不符");
            var search=Descendants<TextBox>(PageBody).First(c=>c.Header?.ToString()=="搜索模型或接入");search.Text="no-such-record";await Task.Delay(100);search.Text="";await Task.Delay(100);
            if(!Descendants<Expander>(PageBody).First().IsExpanded)throw new InvalidOperationException("筛选往返丢失请求详情展开状态");
            if(dirty)throw new InvalidOperationException("请求查看操作错误地创建草稿");
            await Task.Delay(400);await CaptureAsync(Path.Combine(directory,"requests-demo-data.png"));
        }finally{recentRequests=saved;requestRender?.Invoke();refreshGate.Release();}
        Navigate("models");await Task.Delay(100);var name=Descendants<TextBox>(PageBody).First(c=>c.Header?.ToString()=="显示名称");
        name.Text+=" · 筛选草稿";await Task.Delay(100);var searchModels=Descendants<TextBox>(PageBody).First(c=>c.PlaceholderText.StartsWith("搜索模型"));
        searchModels.Text="no-visible-models";await Task.Delay(100);searchModels.Text="";await Task.Delay(100);
        if(!dirty||!name.Text.Contains("筛选草稿"))throw new InvalidOperationException("模型筛选丢失当前编辑内容");
        dirty=false;drafts.Clear();Navigate("overview");Render();await Task.Delay(100);
        Navigate("images");await Task.Delay(100);var prompt=Descendants<TextBox>(PageBody).First(c=>c.Header?.ToString()=="测试提示词");
        prompt.Text="本地提示词保留验证";await Task.Delay(50);Navigate("help");await FlushUiUpdatesAsync();Navigate("images");await Task.Delay(100);
        if(dirty||Descendants<TextBox>(PageBody).First(c=>c.Header?.ToString()=="测试提示词").Text!="本地提示词保留验证")throw new InvalidOperationException("切页丢失图片提示词或误标记配置草稿");
    }

    private async Task VerifyReloadIdentityAsync(string fakeHome)
    {
        var original=configuration;var before=snapshot;var arrived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Gate(object? sender,ConfigurationProgress progress){if(progress.Stage=="snapshot-read"){arrived.TrySetResult();release.Task.GetAwaiter().GetResult();}}
        original.Progress+=Gate;Task<bool>? loading=null;
        try{
            loading=ReloadAsync();await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            configuration=new ConfigurationService(fakeHome,stateHome);release.TrySetResult();
            if(await loading||!ReferenceEquals(before,snapshot))throw new InvalidOperationException("旧配置服务完成读取后覆盖了已切换的目录");
        }finally{release.TrySetResult();if(loading is not null)await loading;original.Progress-=Gate;configuration=original;}
    }
}
