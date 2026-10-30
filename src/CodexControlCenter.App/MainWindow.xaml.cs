using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Windows.UI;

namespace CodexControlCenter.App;

public sealed partial class MainWindow : Window
{
    private ConfigurationService configuration=new();
    private readonly RuntimeService runtime=new();
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim refreshGate=new(1);
    private readonly SemaphoreSlim loadGate=new(1);
    private WorkspaceSnapshot? snapshot;
    private ImageService? images;
    private Func<ChangePlan>? planFactory;
    private string page="overview";
    private DocumentCategory? pendingDocumentCategory;
    private bool dirty,busy,selecting;
    private NavigationViewItem? selectedNavigation;
    private double bodySize=18;
    private StackPanel? healthPanel,requestPanel;
    private StackPanel? providerPanel;
    private StackPanel? lastHealthPanel,lastProviderPanel;
    private string? healthVisualFingerprint,providerVisualFingerprint;
    private readonly Dictionary<string,TextBlock> modelLiveLabels=[];
    private TextBlock? healthHeadline;
    private readonly string stateHome=ResolveStateHome();
    private readonly string appRoot=AppContext.BaseDirectory;
    private WorkspaceChangeMonitor? watcher;
    private bool active=true;
    private int monitorSeconds=3;
    private bool externalRefreshPending;
    private readonly Microsoft.UI.Xaml.Input.KeyboardAccelerator saveShortcut=new(){Key=Windows.System.VirtualKey.S,Modifiers=Windows.System.VirtualKeyModifiers.Control};

    public MainWindow()
    {
        InitializeComponent();Title="Codex 管理台";
        AppVersionLabel.Text="Codex 管理台 "+typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        InitializeQuickNavigation();
        Root.ActualThemeChanged+=(_,_)=>RefreshThemePalette();
        Navigation.DisplayModeChanged+=(_,_)=>UpdateShellLayout();
        Root.KeyboardAccelerators.Add(saveShortcut);saveShortcut.Invoked+=(_,args)=>{args.Handled=true;OnApply(this,new RoutedEventArgs());};
        configuration=new ConfigurationService(stateHome:stateHome);
        AppWindow.Closing+=OnWindowClosing;
        string iconPath=Path.Combine(appRoot,"assets","codex-control-center.ico");if(File.Exists(iconPath))AppWindow.SetIcon(iconPath);
        ExtendsContentIntoTitleBar=true;SetTitleBar(TitleBar);
        var area=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        uint dpi=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        double scale=Math.Max(1,dpi/96.0);
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Min(1920*scale,area.Width*.9),(int)Math.Min(1180*scale,area.Height*.9)));
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X+(area.Width-AppWindow.Size.Width)/2,area.Y+(area.Height-AppWindow.Size.Height)/2));
        Activated+=(_,e)=>active=e.WindowActivationState!=WindowActivationState.Deactivated;
        Closed+=(_,_)=>{if(!Environment.GetCommandLineArgs().Contains("--smoke")&&!Environment.GetCommandLineArgs().Contains("--stress-ui"))SaveWindowPlacement();UiCrashLog.Write("window.closed",page);wallpaperImportCancellation?.Cancel();_ = StopWallpaperTrialAsync();lifetime.Cancel();retirementTimer?.Stop();watcher?.Dispose();};
        Root.Loaded+=async(_,_)=>{LoadPreferences();await ReloadAsync();Navigation.SelectedItem=Navigation.MenuItems[0];navigationReady=true;ApplyPendingDocumentNavigation();var args=Environment.GetCommandLineArgs();var stress=Array.IndexOf(args,"--stress-ui");var smoke=Array.IndexOf(args,"--smoke");if(stress>=0&&stress+1<args.Length){await InitializeWallpaperSafelyAsync();await RunUiStressAsync(args[stress+1]);}else if(smoke>=0&&smoke+1<args.Length){await InitializeWallpaperSafelyAsync();await RunDiagnosticsAsync(args[smoke+1]);}else {_ = InitializeWallpaperSafelyAsync();await RememberManagedHooksOnStartupAsync();_ = MonitorAsync();}};
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private async Task<bool> ReloadAsync(bool forceRender=true,bool discardCurrent=false)
    {
        if(busy||dialogActive||!await loadGate.WaitAsync(0))return false;
        loadingCount++;RefreshOperationChrome();var service=configuration;
        if(discardCurrent){busy=true;RefreshDraftChrome();}
        try {
            var latest=await Task.Run(()=>service.LoadAsync());
            if(!ReferenceEquals(service,configuration)||lifetime.IsCancellationRequested||!discardCurrent&&(busy||dialogActive))return false;
            if(!forceRender&&snapshot is not null&&snapshot.FileHashes.Count==latest.FileHashes.Count&&snapshot.FileHashes.All(p=>latest.FileHashes.TryGetValue(p.Key,out string? hash)&&hash==p.Value))return true;
            bool preserveCurrent=(!discardCurrent&&dirty)||(page=="images"&&imageOperationRunning);
            var accentMatch=Regex.Match(File.ReadAllText(latest.ConfigPath),"(?m)^accent\\s*=\\s*\"(#[0-9a-fA-F]{6})\"");
            snapshot=latest;images=new ImageService(snapshot.CodexHome,appRoot);
            if(accentMatch.Success){var hex=accentMatch.Groups[1].Value;var color=Color.FromArgb(255,Convert.ToByte(hex.Substring(1,2),16),Convert.ToByte(hex.Substring(3,2),16),Convert.ToByte(hex.Substring(5,2),16));Root.Resources["AccentFillColorDefaultBrush"]=new SolidColorBrush(color);Root.Resources["SystemAccentColor"]=color;}
            NavStatus.Text=$"{snapshot.Models.Count} 个模型 · {snapshot.Providers.Count} 个接入";
            SampleTime.Text=$"配置读取 {DateTime.Now:HH:mm:ss} · 自动检查本地状态";
            if(preserveCurrent)StashCurrentDraft();else if(discardCurrent)drafts.Remove(page);dirty=false;if(!preserveCurrent||!RestoreDraft(page))Render();SetFooter(HasDrafts?"配置已更新 · 草稿仍保留，应用时重新检查冲突":"配置已读取 · 修改后检查并应用");
            watcher?.Dispose();watcher=new WorkspaceChangeMonitor(snapshot.FileHashes.Keys);
            await RefreshHealthAsync();
            return true;
        }catch(Exception e){if(lifetime.IsCancellationRequested)return false;if(snapshot is null)RenderStartupRecovery();ShowError("读取失败",e);return false;}finally{loadingCount--;if(discardCurrent)busy=false;if(!lifetime.IsCancellationRequested)RefreshDraftChrome();loadGate.Release();}
    }
    private async Task MonitorAsync()
    {
        int ticks=0;
        while(!lifetime.IsCancellationRequested){
            try{await Task.Delay((active?monitorSeconds:Math.Max(15,monitorSeconds))*1000,lifetime.Token);await RefreshHealthAsync();ticks++;
                if(watcher?.Consume(TimeSpan.FromMilliseconds(500))==true)externalRefreshPending=true;
                if(externalRefreshPending&&!busy&&!dialogActive){bool pending=HasDrafts;bool loaded=await ReloadAsync(false);if(loaded){externalRefreshPending=false;if(pending)ShowNotice("外部配置已更新","所有页面草稿继续保留；应用时检查文件冲突。",InfoBarSeverity.Warning);}}
                if(ticks%20==0&&snapshot is not null&&!busy)await ReloadAsync(false);
            }catch(OperationCanceledException){break;}catch(Exception e){SetFooter("本轮监测失败，将自动重试："+Safe(e.Message));}
        }
    }
    private async Task RefreshHealthAsync()
    {
        if(snapshot is null||!await refreshGate.WaitAsync(0))return;
        try {
            var observed=snapshot;var results=await runtime.CheckLocalAsync(observed,lifetime.Token);if(!ReferenceEquals(observed,snapshot)||lifetime.IsCancellationRequested)return;
            string healthSignature=JsonSerializer.Serialize(results.Select(h=>new{h.Name,h.Detail,h.Status,h.Online}));
            if(healthPanel is not null&&(!ReferenceEquals(healthPanel,lastHealthPanel)||healthVisualFingerprint!=healthSignature)){
                ClearPanelChildren(healthPanel);foreach(var h in results)healthPanel.Children.Add(Row(h.Name,h.Detail,h.Status,h.Status.Contains("未测试")||h.Status.Contains("待验")?"neutral":h.Online?"ok":"warn"));
                lastHealthPanel=healthPanel;healthVisualFingerprint=healthSignature;
            }
            if(healthHeadline is not null)healthHeadline.Text=results.FirstOrDefault()?.Status??"未采样";
            var recent=await Task.Run(()=>runtime.ReadRecentRequests(observed,100));if(!ReferenceEquals(observed,snapshot)||lifetime.IsCancellationRequested)return;recentRequests=recent;
            var providerValues=snapshot.Providers.Select(p=>{var last=recent.FirstOrDefault(r=>r.Provider==p.Id);return new{Name=ProviderName(p),p.BaseUrl,Status=last is null?p.CredentialStatus:"最近记录 "+last.Status};}).ToArray();
            string providerSignature=JsonSerializer.Serialize(providerValues);
            if(providerPanel is not null&&(!ReferenceEquals(providerPanel,lastProviderPanel)||providerVisualFingerprint!=providerSignature)){
                ClearPanelChildren(providerPanel);foreach(var p in providerValues)providerPanel.Children.Add(Row(p.Name,p.BaseUrl,p.Status));
                lastProviderPanel=providerPanel;providerVisualFingerprint=providerSignature;
            }
            foreach(var pair in modelLiveLabels){var last=recent.FirstOrDefault(r=>r.Model==pair.Key);if(last is not null)pair.Value.Text="最近记录 "+last.Status;}
            if(requestPanel is not null){ClearPanelChildren(requestPanel);var records=recent;if(records.Count==0)requestPanel.Children.Add(Note("尚无可读取的请求记录。实际请求出现后自动更新；旧日志没有采集耗时时显示“未采集”。"));
                foreach(var r in records.Take(12))requestPanel.Children.Add(Row(r.Model,$"{r.Provider} · {r.Time}",$"{r.StatusText} · {r.Duration}",IsFailure(r.Status)?"bad":"neutral"));}
            SampleTime.Text=$"本地状态 {DateTime.Now:HH:mm:ss} · 上游调用状态独立验证";requestRender?.Invoke();
            UpdatePolicyStatus();
            overviewRefresh?.Invoke();UpdateWallpaperStatus();
        }catch(OperationCanceledException){}catch(Exception e){SetFooter("状态检查："+Safe(e.Message));}finally{refreshGate.Release();}
    }
    private void OnNavigationChanged(NavigationView sender,NavigationViewSelectionChangedEventArgs args)
    {
        if(selecting||args.SelectedItem is not NavigationViewItem item)return;
        if((busy||dialogActive)&&selectedNavigation!=item){selecting=true;Navigation.SelectedItem=selectedNavigation;selecting=false;return;}
        string next=CanonicalPageKey(item.Tag?.ToString()??"overview");
        if(next!="documents"){pendingDocumentCategory=null;CancelUiUpdate("documents.category");}
        if(next==page){CancelUiUpdate("navigation");selectedNavigation=item;if(pendingDocumentCategory is not null)QueueUiUpdate("documents.category",pageInstance,ApplyPendingDocumentNavigation);return;}
        QueueUiUpdate("navigation",null,()=>CommitNavigation(item));
    }
    private void CommitNavigation(NavigationViewItem item)
    {
        if(busy||dialogActive||!ReferenceEquals(Navigation.SelectedItem,item))return;
        string next=CanonicalPageKey(item.Tag?.ToString()??"overview");
        UiCrashLog.Write("navigation.commit",page+" -> "+next);
        if(page=="wallpaper"&&next!=page)OnWallpaperPageLeaving();
        if(navigationReady&&next!=page)StashCurrentDraft();
        selectedNavigation=item;page=next;if(navigationReady)Banner.IsOpen=false;dirty=false;pageReadFailed=false;overviewRefresh=null;
        if(!RestoreDraft(page)){if(page=="documents")documentsCategory=DocumentCategory.Instructions;Render();}
        ApplyPendingDocumentNavigation();
        if(page=="wallpaper"&&PageBody.Tag is WallpaperWorkspace workspace)workspace.Resume();
        SetFooter(dirty?"本页草稿已恢复 · 尚未写入配置":"可切换页面，未保存草稿保留在本次运行中");RefreshDraftChrome();_ = RefreshHealthAsync();
    }
    private void Render()
    {
        try{UiCrashLog.Write("page.render.begin",page);pageInstance=new();BeginPageBody();pageReadFailed=false;RenderPage();ApplyPendingDocumentNavigation();UiCrashLog.Write("page.render.complete",page);}
        catch(Exception error){
            UiCrashLog.Exception("page.render.error",error);
            ClearPanelChildren(PageBody);planFactory=null;localSave=null;dirty=false;
            pageReadFailed=true;overviewRefresh=null;
            healthPanel=null;requestPanel=null;providerPanel=null;policyStatusPanel=null;modelLiveLabels.Clear();healthHeadline=null;requestRender=null;
            var recovery=Section("此页面暂时无法读取");recovery.Children.Add(Note("配置文件可能已被外部修改或格式有误。修正文件后重新读取，或到设置检查目录与备份。"));
            recovery.Children.Add(AsyncAction("重新读取配置",async()=>{await ReloadAsync();}));
            if(page!="settings")recovery.Children.Add(Action("前往设置与备份",()=>Navigate("settings")));
            Add(Card(recovery));ShowError("读取页面失败",error);RefreshDraftChrome();
        }
    }
    private void RenderPage()
    {
        ClearPanelChildren(PageBody);healthPanel=null;requestPanel=null;providerPanel=null;policyStatusPanel=null;modelLiveLabels.Clear();healthHeadline=null;planFactory=null;localSave=null;requestRender=null;overviewRefresh=null;selectDocumentCategory=null;selectDocumentPath=null;editSnapshot=snapshot;
        if(snapshot is null){if(page=="help")RenderHelp();else if(page=="wallpaper")RenderWallpaper();else RenderStartupRecovery();RefreshDraftChrome();return;}
        SetPageHeading();
        switch(page){case "providers":RenderProviders();break;case "models":RenderModels();break;case "agents":RenderAgents();break;case "images":RenderImages();break;case "documents":RenderDocuments();break;case "wallpaper":RenderWallpaper();break;case "settings":RenderSettings();break;case "requests":RenderRequests();break;case "help":RenderHelp();break;default:RenderOverview();break;}
        RefreshDraftChrome();
    }
    private void SetPageHeading()
    {
        if(snapshot is null)return;
        (PageTitle.Text,PageSubtitle.Text)=page switch{
            "providers"=>("模型接入","供应商、认证与本地路由"),"models"=>("模型列表",$"{snapshot.Models.Count} 个目录条目 · {snapshot.Models.Count(m=>m.Mapped)} 个路由映射"),"documents"=>("技能与指令","统一管理全局与项目指令、技能和角色；系统与插件文档只读"),
            "agents"=>("子代理","选择辅助任务使用的模型，或为它们分配 A/B 来源"),"images"=>("生图接口","选择默认图片服务，保存后可手动测试"),"wallpaper"=>("壁纸","图片库、画面效果与 Codex 实时外观"),"settings"=>("设置与备份","外观、配置目录、运行工具与恢复"),"requests"=>("请求记录","查看真实调用结果；可按模型、来源和结果筛选"),"help"=>("使用指南","从接入服务到保存配置，按步骤完成"),_=>("概览","查看目前使用的模型、服务状态和需要处理的问题")};
        GuideText.Text=PageInstructions(page);
    }
    private void MarkDirty(){if(busy)return;dirty=true;RefreshDraftChrome();SetFooter("本页草稿尚未保存 · 可切换页面，应用前检查变更并备份");}
    private void SetFooter(string text)=>FooterMessage.Text=text;
    private static string Safe(string value)=>UserFeedback.Redact(value);
    private void ShowError(string title,Exception error)=>ShowProblem(title,error);
    private void ShowNotice(string title,string message,InfoBarSeverity severity=InfoBarSeverity.Informational){Banner.ActionButton=null;Banner.Title=title;Banner.Message=message;Banner.Severity=severity;Banner.IsOpen=true;}
    private async void OnRefresh(object sender,RoutedEventArgs e){if(busy||dialogActive)return;if(HasDrafts){await RefreshHealthAsync();ShowNotice("实时状态已刷新","草稿保留；要重新载入本页配置请使用“放弃本页草稿”。");return;}await ReloadAsync(false);await RefreshHealthAsync();}
    private async void OnDiscard(object sender,RoutedEventArgs e){if(!dirty||busy)return;var dlg=new ContentDialog{Title="放弃本页草稿？",Content="只放弃当前页面的修改，其他页面草稿继续保留。",PrimaryButtonText="放弃本页草稿",CloseButtonText="继续编辑",XamlRoot=Root.XamlRoot};if(await ShowDialogAsync(dlg)!=ContentDialogResult.Primary)return;await DiscardCurrentDraftAsync();}
    private async Task<bool> DiscardCurrentDraftAsync()
    {
        if(busy||imageOperationRunning&&page=="images")return false;
        if(page=="wallpaper"){wallpaperImportCancellation?.Cancel();await StopWallpaperTrialAsync();}
        bool loaded=await ReloadAsync(discardCurrent:true);
        if(!loaded){RefreshDraftChrome();SetFooter("重新读取未完成，本页草稿仍保留；修正文件后可再次放弃并载入。");}
        return loaded;
    }
    private async void OnApply(object sender,RoutedEventArgs e)
    {
        if(busy||dialogActive||!dirty||page=="images"&&imageOperationRunning)return;if(localSave is not null){await SaveLocalSettingsAsync();return;}
        await ApplyCurrentAsync();
    }
    private async Task<bool> ApplyCurrentAsync()
    {
        if(snapshot is null||planFactory is null||busy)return false;
        ApplyResult? committed=null;
        operationPhase="check";busy=true;RefreshDraftChrome();
        SetFooter("正在检查本页修改…");
        try {
            var plan=planFactory();
            if(plan.Errors.Count>0){ShowValidationErrors(plan.Errors);return false;}
            plan.Files.RemoveAll(f=>!f.Path.EndsWith(".dpapi",StringComparison.OrdinalIgnoreCase)&&File.Exists(f.Path)&&File.ReadAllText(f.Path)==f.Content);
            if(plan.Files.Count==0){dirty=false;RefreshDraftChrome();ShowNotice("没有需要应用的变更","当前配置与草稿相同。");return true;}
            var detail=new StackPanel{Spacing=18};detail.Children.Add(Note("仅保存当前页面。保存前自动备份；其他页面的草稿继续保留。"));
            foreach(var file in plan.Files){var card=Section(ChangeName(file));card.Children.Add(Text("生效方式："+ChangeEffect(file),16));
                var changes=Section();changes.Children.Add(Note(file.Path));changes.Children.Add(Note(file.Path.EndsWith(".dpapi",StringComparison.OrdinalIgnoreCase)?"加密凭据将随配置一起备份与应用，内容不显示。":DescribeDifference(File.Exists(file.Path)?File.ReadAllText(file.Path):"",file.Content)));card.Children.Add(new Expander{Header="查看文件改动（敏感字段已隐藏）",Content=changes,HorizontalAlignment=HorizontalAlignment.Stretch});detail.Children.Add(card);}
            var dlg=new ContentDialog{Title=$"确认保存 {plan.Files.Count} 个文件",Content=new ScrollViewer{Content=detail,MaxHeight=650},PrimaryButtonText="备份并保存",CloseButtonText="继续编辑",DefaultButton=ContentDialogButton.Close,XamlRoot=Root.XamlRoot};
            operationPhase="review";RefreshDraftChrome();
            if(await ShowDialogAsync(dlg)!=ContentDialogResult.Primary)return false;
            operationPhase="write";busy=true;RefreshDraftChrome();
            SetFooter("正在保存并创建备份…");var target=editSnapshot??snapshot;var result=await Task.Run(()=>configuration.ApplyAsync(target,plan));committed=result;
            // File commit and follow-up runtime checks are separate outcomes.
            dirty=false;drafts.Remove(page);
            string? hookTrustNotice=null;bool hookTrustFailed=false;
            if(plan.Files.Any(f=>Path.GetFileName(f.Path) is "hooks.json" or "subagent-policy.json" or "policy_hook.py")){
                try{var trust=await new ManagedHookTrustService(appRoot).ReconcileAsync(configuration,ResolvePythonExecutable(),lifetime.Token);if(trust.Verified||trust.Total>0||trust.BackupDirectory is not null||trust.Detail!="自动信任未启用。"){hookTrustNotice=trust.Detail;hookTrustFailed=!trust.Verified;}}
                catch(Exception){hookTrustFailed=true;hookTrustNotice="配置已保存，钩子授权未完成；可到子代理页重试记住授权。";}
            }
            HealthResult? activation=null;
            if(RuntimeService.ChangesAffectRouter(target,plan.Files))activation=await runtime.CheckRouterConfigurationAsync(target,lifetime.Token);
            RecordSave(plan.Files.Select(ChangeEffect).Concat(activation is null?[]:new[]{activation.Status+"："+activation.Detail}).Concat(hookTrustNotice is null?[]:new[]{hookTrustNotice}),result.BackupDirectory);
            busy=false;dirty=false;drafts.Remove(page);bool refreshed=await ReloadAsync();
            if(refreshed)ShowNotice(hookTrustFailed?"配置已保存 · 钩子授权待确认":activation is not null&&!activation.Online?"文件已保存 · 运行生效待确认":"配置已保存",
                (activation is null?string.Join("；",result.Effects.Distinct())+"。现有聊天可能有独立覆盖。":activation.Status+"。"+activation.Detail)+(hookTrustNotice is null?"":"\n"+hookTrustNotice),
                hookTrustFailed||activation is not null&&!activation.Online?InfoBarSeverity.Warning:InfoBarSeverity.Success);
            SetFooter(refreshed?"已创建备份："+Path.GetFileName(result.BackupDirectory):"配置已保存并备份，但显示未能刷新，请按错误提示处理。");return true;
        }catch(Exception ex){busy=false;
            if(committed is not null){
                dirty=false;drafts.Remove(page);RecordSave(committed.Effects.Append("文件保存成功，后续状态核对未完成。"),committed.BackupDirectory);
                ShowNotice("文件已保存 · 后续核对未完成","备份已创建。可刷新状态或重新读取配置；请勿因本次核对失败重复提交。",InfoBarSeverity.Warning);
                SetFooter("已保存并备份："+Path.GetFileName(committed.BackupDirectory)+" · 状态核对可稍后重试");return true;
            }
            SetFooter("保存未完成，草稿仍保留。请处理上方提示。");ShowError("未应用变更",ex);return false;}
        finally{busy=false;operationPhase="";RefreshDraftChrome();if(dirty&&FooterMessage.Text.StartsWith("正在"))SetFooter("草稿尚未保存，可以继续编辑或切换页面。");}
    }
    private static string DescribeDifference(string original,string updated)
    {
        bool Sensitive(string s)=>Regex.IsMatch(s,@"(?i)(api[_-]?key|secret|password|token|authorization|credential)");
        var oldLines=original.Split('\n').Select(s=>s.TrimEnd('\r')).ToHashSet();var newLines=updated.Split('\n').Select(s=>s.TrimEnd('\r')).ToHashSet();
        var removed=oldLines.Except(newLines).Where(s=>!string.IsNullOrWhiteSpace(s)).Take(12).Select(s=>Sensitive(s)?"− [凭据引用或敏感字段已隐藏]":"− "+Safe(s));
        var added=newLines.Except(oldLines).Where(s=>!string.IsNullOrWhiteSpace(s)).Take(12).Select(s=>Sensitive(s)?"+ [凭据引用或敏感字段已隐藏]":"+ "+Safe(s));return string.Join("\n",removed.Concat(added));
    }
    private async void OnOpenCodex(object sender,RoutedEventArgs e){try{
        await CompletePendingBindingsBeforeOpenAsync();
        bool Visible(){var running=CodexWallpaperRuntime.RunningCodex();try{return running.Any(p=>p.MainWindowHandle!=IntPtr.Zero);}finally{foreach(var process in running)process.Dispose();}}
        bool enabled=EnhancementMode.IsEnabled(snapshot?.CodexHome),wallpaper=false;
        if(enabled&&snapshot is not null&&File.Exists(Path.Combine(snapshot.CodexHome,"control-center","unified-routing.json")))await runtime.StartRouterAsync(snapshot);
        try{wallpaper=WallpaperLibrary.LoadSettings().Enabled;}catch(Exception error){UiCrashLog.Exception("codex.open.wallpaper-settings",error);}
        string route=await CodexOpenCoordinator.OpenAsync(enabled,wallpaper,Visible,()=>StartWallpaperHost(true),()=>CodexWallpaperRuntime.ActivateOfficial(WallpaperRoot),ct:lifetime.Token);
        SetFooter(route=="official-fallback"?"已通过 Windows 正式入口打开 Codex；壁纸组件未就绪，可稍后重连。":"已发送 Codex 打开请求。");
    }catch(Exception ex){ShowError("打开 Codex 失败",ex);}}
    private AppPreferencesStore Preferences=>new(Path.Combine(stateHome,"preferences.json"));
    private void LoadPreferences()
    {
        try{
            var prefs=Preferences.Load();bodySize=prefs["density"]?.ToString()=="large"?20:18;
            Root.RequestedTheme=prefs["theme"]?.ToString() switch{"light"=>ElementTheme.Light,"dark"=>ElementTheme.Dark,_=>ElementTheme.Default};
            if(int.TryParse(prefs["monitorSeconds"]?.ToString(),out int interval)&&interval is 3 or 5 or 10)monitorSeconds=interval;
            string? home=prefs["codexHome"]?.ToString();if(!string.IsNullOrWhiteSpace(home))configuration=new ConfigurationService(home,stateHome);RestoreWindowPlacement(prefs);
        }catch(Exception error){ShowError("读取管理台设置失败",error);}
    }
    private JsonObject ReadPreferences(){try{return Preferences.Load();}catch(Exception error)when(error is FormatException or IOException or UnauthorizedAccessException){return new();}}
    private void WritePreferences(JsonObject prefs)=>Preferences.Save(prefs);
    private static string ResolveStateHome()
    {
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--state-home");
        return index>=0&&index+1<args.Length?Path.GetFullPath(args[index+1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexControlCenter");
    }
    private void SaveWindowPlacement(){try{var prefs=ReadPreferences();prefs["window"]=JsonSerializer.SerializeToNode(new{x=AppWindow.Position.X,y=AppWindow.Position.Y,width=AppWindow.Size.Width,height=AppWindow.Size.Height});WritePreferences(prefs);}catch{}}
    private void RestoreWindowPlacement(JsonNode? prefs){try{var placement=prefs?["window"];if(placement is null)return;var area=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;int width=Math.Clamp((int)placement["width"]!,800,area.Width),height=Math.Clamp((int)placement["height"]!,600,area.Height);int x=Math.Clamp((int)placement["x"]!,area.X,area.X+area.Width-width),y=Math.Clamp((int)placement["y"]!,area.Y,area.Y+area.Height-height);AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x,y,width,height));}catch{}}
    private TextBlock Text(string text,double? size=null)=>new(){Text=text,FontSize=size??bodySize,TextWrapping=TextWrapping.Wrap};
    private TextBlock Note(string text){var t=Text(text,Math.Max(15,bodySize-2));t.Style=(Style)Root.Resources["MutedTextStyle"];return t;}
    private StackPanel Section(string? heading=null){var panel=new StackPanel{Spacing=16};if(heading is not null)panel.Children.Add(new TextBlock{Text=heading,FontSize=22,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});return panel;}
    private Border Card(UIElement child)=>new(){Child=child,Padding=new Thickness(24),BorderThickness=new Thickness(1),Style=(Style)Root.Resources["PanelCardStyle"],CornerRadius=new CornerRadius(12)};
    private Brush BrushResource(string key)=>Root.Resources.TryGetValue(key,out var local)&&local is Brush brush?brush:Application.Current.Resources.TryGetValue(key,out var app)&&app is Brush found?found: new SolidColorBrush(key.Contains("Text")?(Root.ActualTheme==ElementTheme.Dark?Microsoft.UI.Colors.LightGray:Microsoft.UI.Colors.DimGray):key.Contains("Background")?(Root.ActualTheme==ElementTheme.Dark?Windows.UI.Color.FromArgb(255,35,35,35):Microsoft.UI.Colors.White):Windows.UI.Color.FromArgb(40,128,128,128));
    private UIElement Row(string title,string detail,string status,string tone="neutral",Action<TextBlock>? statusCreated=null)
    {
        var grid=new Grid{ColumnSpacing=24,RowSpacing=8,Padding=new Thickness(0,15,0,15)};grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto,MaxWidth=400});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var left=new StackPanel{Spacing=5};left.Children.Add(Text(title));if(detail.Length>0)left.Children.Add(Note(detail));grid.Children.Add(left);
        var right=Text(status,16);right.VerticalAlignment=VerticalAlignment.Center;right.MaxWidth=380;right.TextAlignment=TextAlignment.Right;right.Foreground=ToneBrush(tone);Grid.SetColumn(right,1);grid.Children.Add(right);statusCreated?.Invoke(right);
        grid.SizeChanged+=(_,e)=>{bool stacked=e.NewSize.Width<680;Grid.SetColumnSpan(left,stacked?2:1);Grid.SetColumn(right,stacked?0:1);Grid.SetRow(right,stacked?1:0);Grid.SetColumnSpan(right,stacked?2:1);right.TextAlignment=stacked?TextAlignment.Left:TextAlignment.Right;right.HorizontalAlignment=stacked?HorizontalAlignment.Left:HorizontalAlignment.Right;};
        return new Border{Child=grid,BorderThickness=new Thickness(0,0,0,1),Style=(Style)Root.Resources["RowDividerStyle"]};
    }
    private TextBox Input(string header,string value,bool multiline=false,bool track=true){var owner=pageInstance;var t=new TextBox{Header=header,Text=value,FontSize=bodySize,MinHeight=bodySize>18?56:52,AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap};if(multiline){t.Height=420;t.FontFamily=new FontFamily("Cascadia Code,Consolas");}string observed=t.Text;if(track)t.TextChanging+=(_,_)=>{if(!ReferenceEquals(owner,pageInstance)||t.Text==observed)return;observed=t.Text;MarkDirty();};return t;}
    private ComboBox Choice(string header,IEnumerable<string> values,string selected,bool track=true,bool selectFirst=true){var owner=pageInstance;var combo=new ComboBox{Header=header,FontSize=bodySize,MinHeight=bodySize>18?56:52,HorizontalAlignment=HorizontalAlignment.Stretch,PlaceholderText="请选择"};foreach(var value in values.Distinct())combo.Items.Add(value);combo.SelectedItem=selected;if(selectFirst&&combo.SelectedIndex<0&&combo.Items.Count>0)combo.SelectedIndex=0;object? observed=combo.SelectedItem;if(track)combo.SelectionChanged+=(_,_)=>{if(!ReferenceEquals(owner,pageInstance)||Equals(observed,combo.SelectedItem))return;observed=combo.SelectedItem;MarkDirty();};return combo;}
    private Button Action(string label,Action handler){var b=new Button{Content=label,MinHeight=bodySize>18?56:50,FontSize=bodySize};b.Click+=(_,_)=>{try{handler();}catch(Exception e){ShowError("操作失败",e);}};return b;}
    private Button AsyncAction(string label,Func<Task> handler){var b=new Button{Content=label,MinHeight=bodySize>18?56:50,FontSize=bodySize};b.Click+=async(_,_)=>{if(busy)return;b.IsEnabled=false;try{await handler();}catch(Exception e){ShowError("操作失败",e);}finally{b.IsEnabled=true;}};return b;}
    private void Add(UIElement element)=>PageBody.Children.Add(element);
    private void Navigate(string tag)
    {
        if(busy||dialogActive)return;
        pendingDocumentCategory=tag switch{"global"=>DocumentCategory.Instructions,"skills"=>DocumentCategory.Skills,_=>null};
        string target=CanonicalPageKey(tag);
        if(target!="documents")CancelUiUpdate("documents.category");
        var item=Navigation.MenuItems.OfType<NavigationViewItem>().First(x=>CanonicalPageKey(x.Tag?.ToString()??"overview")==target);
        if(target==page){selectedNavigation=item;if(!ReferenceEquals(Navigation.SelectedItem,item))Navigation.SelectedItem=item;if(pendingDocumentCategory is not null)QueueUiUpdate("documents.category",pageInstance,ApplyPendingDocumentNavigation);return;}
        Navigation.SelectedItem=item;
    }
    private void ApplyPendingDocumentNavigation()
    {
        if(!navigationReady||page!="documents"||pendingDocumentCategory is not DocumentCategory category||selectDocumentCategory is not Action<DocumentCategory> select)return;
        select(category);pendingDocumentCategory=null;
    }
    private void AddCredential(ChangePlan plan,string path,string secret)
    {
        if(plan.Errors.Count>0||secret.Length==0)return;
        (editSnapshot??snapshot)!.FileHashes.TryAdd(path,File.Exists(path)?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))):"<missing>");
        plan.Files.Add(new FileEdit(path,CredentialVault.Protect(secret),"保存当前用户加密凭据","下一次服务启动或生图调用加载"));
    }
    private string ResolvePythonExecutable()
    {
        string? configured=ReadPreferences()["pythonExecutable"]?.ToString();
        if(!string.IsNullOrWhiteSpace(configured)){if(File.Exists(configured))return Path.GetFullPath(configured);throw new InvalidOperationException("配置的 Python 不存在，请在设置中更正。");}
        foreach(var path in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){
            try{var candidate=Path.Combine(path.Trim('"'),"python.exe");if(File.Exists(candidate)&&!candidate.Contains("WindowsApps",StringComparison.OrdinalIgnoreCase))return candidate;}catch{}
        }
        throw new InvalidOperationException("未找到 Python，请在设置中填写 python.exe 的绝对路径。");
    }
}
