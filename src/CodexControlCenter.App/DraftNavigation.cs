using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CodexControlCenter.Core;

namespace CodexControlCenter.App;

public sealed partial class MainWindow
{
    private sealed record PageDraft(
        StackPanel Body, Func<ChangePlan>? Plan, Func<Task<bool>>? LocalSave,
        StackPanel? Health, StackPanel? Requests, StackPanel? Providers,
        StackPanel? PolicyStatus, SubagentPolicyService? Policy,
        Dictionary<string,TextBlock> ModelLabels, Action? RequestsRender,WorkspaceSnapshot? Snapshot,bool HasChanges,object Instance,
        Action<DocumentCategory>? SelectDocumentCategory,Action<string>? SelectDocumentPath);
    private readonly Dictionary<string,PageDraft> drafts=[];
    private Func<Task<bool>>? localSave;
    private bool navigationReady,closingConfirmed;
    private bool dialogActive;
    private WorkspaceSnapshot? editSnapshot;
    private Action<ContentDialog>? diagnosticDialogShown;
    private int DraftCount=>drafts.Count(k=>k.Key!=page&&k.Value.HasChanges)+(dirty?1:0);
    private bool HasDrafts=>dirty||drafts.Any(k=>k.Key!=page&&k.Value.HasChanges);

    private void StashCurrentDraft()
    {
        if(!dirty&&!(page=="images"&&imageOperationRunning)){drafts.Remove(page);return;}
        drafts[page]=new(PageBody,planFactory,localSave,healthPanel,requestPanel,providerPanel,
            policyStatusPanel,subagentPolicy,new(modelLiveLabels),requestRender,editSnapshot??snapshot,dirty,pageInstance,selectDocumentCategory,selectDocumentPath);
        pageInstance=new();
        PageBody.Visibility=Visibility.Collapsed;
    }
    private bool RestoreDraft(string key)
    {
        if(!drafts.Remove(key,out var draft))return false;
        pageInstance=draft.Instance;SetPageHeading();if(!ReferenceEquals(PageBody,draft.Body))RetirePageBody(PageBody);PageBody=draft.Body;PageBody.Visibility=Visibility.Visible;
        planFactory=draft.Plan;localSave=draft.LocalSave;healthPanel=draft.Health;requestPanel=draft.Requests;providerPanel=draft.Providers;
        selectDocumentCategory=draft.SelectDocumentCategory;selectDocumentPath=draft.SelectDocumentPath;
        policyStatusPanel=draft.PolicyStatus;subagentPolicy=draft.Policy;requestRender=draft.RequestsRender;
        modelLiveLabels.Clear();foreach(var item in draft.ModelLabels)modelLiveLabels[item.Key]=item.Value;
        editSnapshot=draft.Snapshot;dirty=draft.HasChanges;RefreshDraftChrome();return true;
    }
    private void RefreshDraftChrome()
    {
        PageInteraction.IsEnabled=!busy;Navigation.IsPaneToggleButtonVisible=!busy;
        ApplyButton.Content=busy?"处理中…":page=="wallpaper"?"应用壁纸":localSave is not null?"保存设置":page=="documents"?"预览并保存全部草稿":"预览并保存";
        bool imageBusy=page=="images"&&imageOperationRunning||page=="wallpaper"&&wallpaperImportCancellation is not null;
        ApplyButton.IsEnabled=!busy&&!imageBusy&&dirty&&(planFactory is not null||localSave is not null);
        saveShortcut.IsEnabled=ApplyButton.IsEnabled;
        DiscardButton.IsEnabled=!busy&&!imageBusy&&dirty;
        foreach(var nav in Navigation.MenuItems.OfType<NavigationViewItem>()){
            string key=nav.Tag?.ToString()??"";bool changed=key==page?dirty:drafts.TryGetValue(key,out var saved)&&saved.HasChanges;
            string label=PageName(key);
            nav.Content=label+(key=="images"&&imageOperationRunning?"  · 生成中":changed?"  •":"");
        }
        DraftSummary.Text=busy?"正在检查或保存，请稍候":DraftCount>0?$"{DraftCount} 页未保存":"没有未保存的修改";
        RefreshOperationChrome();
    }
    private async void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender,Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if(closingConfirmed||Environment.GetCommandLineArgs().Contains("--smoke")||Environment.GetCommandLineArgs().Contains("--stress-ui"))return;
        if(busy||dialogActive){args.Cancel=true;return;}
        if(wallpaperImportCancellation is not null){args.Cancel=true;wallpaperImportCancellation.Cancel();ShowNotice("正在取消图片导入","已完成的图片会保留，请等待当前文件处理结束再关闭。",InfoBarSeverity.Warning);return;}
        if(imageOperationRunning){args.Cancel=true;Navigate("images");ShowNotice("图片任务仍在运行","可以等待完成，或先点击“取消生成”再关闭窗口。",InfoBarSeverity.Warning);return;}
        if(!HasDrafts)return;
        args.Cancel=true;
        var dialog=new ContentDialog{Title="有未保存的修改",Content=$"{DraftCount} 个页面的草稿仍保留在本次运行中。关闭后这些草稿会丢失。",PrimaryButtonText="继续编辑",SecondaryButtonText="放弃草稿并关闭",DefaultButton=ContentDialogButton.Primary,XamlRoot=Root.XamlRoot};
        if(await ShowDialogAsync(dialog)==ContentDialogResult.Secondary){closingConfirmed=true;Close();}
    }
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if(dialogActive)return ContentDialogResult.None;
        dialog.RequestedTheme=Root.ActualTheme;dialog.FontSize=bodySize;
        Style DialogButton(bool accent){
            var style=new Style(typeof(Button));
            if(accent&&Application.Current.Resources.TryGetValue("AccentButtonStyle",out var value)&&value is Style basis)style.BasedOn=basis;
            style.Setters.Add(new Setter(Control.FontSizeProperty,bodySize));style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty,52.0));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(18,10,18,10)));return style;
        }
        dialog.PrimaryButtonStyle=DialogButton(true);dialog.SecondaryButtonStyle=DialogButton(false);dialog.CloseButtonStyle=DialogButton(false);
        dialogActive=true;RefreshOperationChrome();try{var shown=dialog.ShowAsync();diagnosticDialogShown?.Invoke(dialog);return await shown;}finally{dialogActive=false;RefreshOperationChrome();}
    }
}
