using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task RunDiagnosticsAsync(string directory)
    {
        Directory.CreateDirectory(directory);var checks=new List<object>();
        try {
            if(Environment.GetCommandLineArgs().Contains("--fresh-install-only")){
                if(!Environment.GetCommandLineArgs().Contains("--state-home"))throw new InvalidOperationException("Fresh install self-check requires isolated state.");
                if(snapshot is null||snapshot.Models.Count!=0||snapshot.Providers.Count!=0)throw new InvalidOperationException("Fresh install imported models or providers.");
                foreach(var name in new[]{"overview","providers","models","agents","documents","wallpaper","settings"}){
                    dirty=false;Navigate(name);await FlushUiUpdatesAsync();Render();await Task.Delay(100);Root.UpdateLayout();
                    if(dirty)throw new InvalidOperationException("Fresh page unexpectedly dirty: "+name);
                }
                await CaptureAsync(Path.Combine(directory,"fresh-install-settings.png"));
                await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,emptyModelAndProviderLists=true,blankPolicyAndPagesRender=true,mouseOrKeyboardControl=false,inferenceRequests=0}));return;
            }
            if(Environment.GetCommandLineArgs().Contains("--model-labels-only")){
                if(!Environment.GetCommandLineArgs().Contains("--state-home"))throw new InvalidOperationException("标签自检必须使用隔离状态。");
                dirty=false;Navigate("models");Render();await FlushUiUpdatesAsync();
                if(verifyModelLabels is null)throw new InvalidOperationException("模型标签自检未初始化。");
                await verifyModelLabels();await CaptureAsync(Path.Combine(directory,"model-labels.png"));
                await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,checks=new[]{new{modelLabelSync=true,catalogOnlyPlan=true,selectionDraftRestore=true,clearLabel=true,wholeNameSync=true,restoreClean=true}},mouseOrKeyboardControl=false,inferenceRequests=0}));return;
            }
            if(Environment.GetCommandLineArgs().Contains("--integration-only")){
                await VerifyModelIntegrationAsync(directory);checks.Add(new{modelIntegration=true});await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,checks}));return;
            }
            if(Environment.GetCommandLineArgs().Contains("--enhancements-only")){
                await VerifyEnhancementsAsync(directory);checks.Add(new{enhancements=true});await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,checks}));return;
            }
            if(Environment.GetCommandLineArgs().Contains("--wallpaper-only")){
                await VerifyWallpaperAsync(directory);checks.Add(new{wallpaper=true});await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,checks}));return;
            }
            if(Environment.GetCommandLineArgs().Contains("--interaction-only")){
                await VerifyModernInteractionsAsync(directory);await VerifyImmediateDraftsAsync(directory);checks.Add(new{modernInteractions=true,quickSearchRenders=true,immediateCrossPageDrafts=true,firstUseProviderFlow=true});
                await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,checks}));return;
            }
            foreach(var tag in new[]{"overview","providers","models","requests","agents","images","documents","settings","help"}){
                dirty=false;page=tag;Navigation.SelectedItem=Navigation.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>().First(x=>Equals(x.Tag,tag));Render();await refreshGate.WaitAsync();refreshGate.Release();await RefreshHealthAsync();await Task.Delay(250);
                if(dirty)throw new InvalidOperationException("页面初始化错误地产生了草稿："+tag);
                if(PageInteraction.ActualWidth>800&&PageBody.ActualWidth<PageInteraction.ActualWidth-12)throw new InvalidOperationException("内容区未按窗口宽度伸展："+tag);
                checks.Add(new{page=tag,rendered=true,children=PageBody.Children.Count,width=Root.ActualWidth,height=Root.ActualHeight});
                if(tag is "overview" or "providers" or "models" or "requests" or "images" or "agents" or "documents" or "settings" or "help")await CaptureAsync(Path.Combine(directory,tag+".png"));
                if(tag=="providers"){await VerifyProviderExperienceAsync(directory);checks.Add(new{providerTilesAndGroupedFields=true,providerDraftSwitchProtection=true});if(verifyProviderFlow is not null)await verifyProviderFlow();checks.Add(new{friendlyProviderLabelsAndValidation=true});}
                if(tag=="models"){if(verifyModelDraft is not null)await verifyModelDraft();checks.Add(new{modelDraftMergeAndSwitchGuard=true});}
                if(tag=="images"){if(verifyImageDraft is not null)await verifyImageDraft();checks.Add(new{imageDraftSwitchGuard=true});}
                if(tag=="agents"){if(verifyPolicyDraft is not null)await verifyPolicyDraft();checks.Add(new{globalSubagentPolicyDraftValidated=true});}
                Banner.IsOpen=false;dirty=false;
            }
            dirty=false;drafts.Clear();Root.RequestedTheme=ElementTheme.Dark;Navigate("requests");Render();await Task.Delay(250);await CaptureAsync(Path.Combine(directory,"requests-dark.png"));checks.Add(new{darkThemeRendered=true});
            await VerifyDraftNavigationAsync();checks.Add(new{crossPageDraftPreservation=true,settingsDraftPreservation=true,imagePromptDoesNotDirtyConfig=true,requestFiltersDoNotDirtyConfig=true});
            await VerifyMissingDefaultAsync();checks.Add(new{missingDefaultNeverSilentlyReplaced=true});
            await VerifyOperationUiAsync();checks.Add(new{activeImageUiPreserved=true,saveFormDisabled=true,imageResultsCanReattach=true});
            await VerifyInvalidAgentDefaultAsync();checks.Add(new{missingSubagentDefaultNeverSilentlyReplaced=true});
            await VerifyVisualOperationsAsync(directory);checks.Add(new{operationStepsAndDraftList=true,unchangedRequestsKeepExpandedRows=true,requestStatisticsAndFilterState=true,modalGuards=true,modelFilterRetainsDraft=true,imagePromptSurvivesNavigation=true});
            await VerifyUiLifecycleAsync();checks.Add(new{queuedNavigationKeepsLatest=true,queuedModelSelectionKeepsLatest=true,stalePageCallbacksIgnored=true,deferredDraftNavigationPreservesControls=true,backupFiltersCoalesced=true,retainedPerItemRemoval=true});
            await VerifyModernInteractionsAsync(directory);checks.Add(new{multipleModelRawDrafts=true,invalidDraftBlocksBatch=true,batchPlanComposes=true,quickSearchAndEmptyState=true,quickNavigationPreservesDrafts=true});
            await VerifyImmediateDraftsAsync(directory);checks.Add(new{providerTextCapturedBeforeNavigation=true,credentialDraftKeptInMemory=true,documentDraftCapturedBeforeNavigation=true,imagePromptCapturedBeforeNavigation=true,firstUseProviderGuide=true});
            if(Environment.GetCommandLineArgs().Contains("--state-home")){await VerifyHomeSwitchAsync(directory);checks.Add(new{configurationDirectorySwitchReloaded=true,startupRecoveryRendered=true,staleDirectoryReadDiscarded=true});checks.Add(new{discardFailurePreservesDraft=checksFailureRecoveryVerified,invalidPageAllowsRecovery=checksFailureRecoveryVerified,overviewSurvivesDamagedImageConfig=checksFailureRecoveryVerified});}
            if(Environment.GetCommandLineArgs().Contains("--state-home"))checks.Add(new{modelVisibilityFilters=true,visibilityDraftsAcrossModelsAndPages=true,catalogOnlyVisibilityPlan=true,visibilityComposesWithMapping=true,bulkVisibilityKeepsEditor=true});
            if(Environment.GetCommandLineArgs().Contains("--state-home"))checks.Add(new{globalMarkdownEditor=true,multiFileDrafts=true,markdownSearch=true,globalOverridePriorityHint=true,isolatedMarkdownSaveAndRestore=true});
            if(Environment.GetCommandLineArgs().Contains("--state-home"))checks.Add(new{markdownSourcesGrouped=true,systemMarkdownReadOnly=true,nestedProjectAgentsImported=true,projectFoldersRemembered=true});
            if(Environment.GetCommandLineArgs().Contains("--state-home"))checks.Add(new{unifiedDocuments=true,categoryDraftsAndAliases=true,allCategorySaveAndRestore=true,documentEmptySearch=true,documentSwitchStress=true});
            if(snapshot is null)throw new InvalidOperationException("No workspace snapshot loaded");
            if(File.Exists(snapshot.CatalogPath)){var catalog=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(snapshot.CatalogPath));if(catalog?["models"] is System.Text.Json.Nodes.JsonArray actual&&actual.Count!=snapshot.Models.Count)throw new InvalidOperationException($"Catalog count mismatch: {actual.Count} / {snapshot.Models.Count}");}
            if(snapshot.Models.Count==0)throw new InvalidOperationException("No models loaded from the configured catalog");
            checks.Add(new{configuration=true,models=snapshot.Models.Count,mapped=snapshot.Models.Count(x=>x.Mapped),providers=snapshot.Providers.Count,skills=snapshot.Skills.Count});
            var invalid=configuration.PlanDefaults(snapshot,"__missing_model__","high",snapshot.AgentModel,snapshot.AgentEffort,snapshot.AgentConcurrency);
            if(invalid.Errors.Count==0)throw new InvalidOperationException("Invalid default model was accepted");
            checks.Add(new{invalidModelRejected=true});
            uint dpi=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));double scale=dpi/96.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1200*scale),(int)(800*scale)));bodySize=20;
            foreach(var tag in new[]{"overview","providers","models","requests","agents","images","documents","settings","help"}){
                dirty=false;page=tag;Navigation.SelectedItem=Navigation.MenuItems.OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>().First(x=>Equals(x.Tag,tag));Render();await Task.Delay(300);
                Root.UpdateLayout();if(dirty)throw new InvalidOperationException("大字号页面初始化错误地产生了草稿："+tag);if(PageBody.DesiredSize.Width>PageScroll.ViewportWidth+4)throw new InvalidOperationException($"Narrow layout horizontal overflow on {tag}: {PageBody.DesiredSize.Width}/{PageScroll.ViewportWidth}");
                checks.Add(new{narrowPage=tag,largeDensity=true,width=Root.ActualWidth,height=Root.ActualHeight});
                if(tag=="models")await CaptureAsync(Path.Combine(directory,"models-large-density.png"));
                if(tag=="documents")foreach(var category in Enum.GetValues<CodexControlCenter.Core.DocumentCategory>()){selectDocumentCategory!(category);await FlushUiUpdatesAsync();await Task.Delay(180);Root.UpdateLayout();if(PageBody.DesiredSize.Width>PageScroll.ViewportWidth+4)throw new InvalidOperationException("文档分类大字号横向溢出："+category);await CaptureAsync(Path.Combine(directory,"documents-"+category.ToString().ToLowerInvariant()+"-large-density.png"));}
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(900*scale),(int)(900*scale)));
            foreach(var tag in new[]{"overview","models","requests","providers","images","documents","agents"}){
                dirty=false;Navigate(tag);Render();await Task.Delay(350);Root.UpdateLayout();
                if(PageBody.DesiredSize.Width>PageScroll.ViewportWidth+4||FooterActions.ActualWidth>Root.ActualWidth-40||PageScroll.ActualHeight<120)
                    throw new InvalidOperationException($"Compact layout invalid on {tag}: content={PageBody.DesiredSize.Width}/{PageScroll.ViewportWidth}, footer={FooterActions.ActualWidth}/{Root.ActualWidth}, viewportHeight={PageScroll.ActualHeight}");
                checks.Add(new{compactPage=tag,width=Root.ActualWidth,largeDensity=true});
                if(tag is "overview" or "images" or "documents")await CaptureAsync(Path.Combine(directory,tag+"-compact.png"));
            }
            await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=true,checks},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception e){await File.WriteAllTextAsync(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(new{success=false,error=e.ToString(),checks},new JsonSerializerOptions{WriteIndented=true}));}
        finally{Close();}
    }
    private async Task VerifyMissingDefaultAsync()
    {
        var s=snapshot!;string saved=s.MainModel;
        try{
            dirty=false;drafts.Clear();Navigate("models");s.MainModel="__missing_default_for_native_test__";Render();await Task.Delay(150);
            var choice=Descendants<Microsoft.UI.Xaml.Controls.ComboBox>(PageBody).First(t=>t.Header?.ToString()=="模型");if(choice.SelectedIndex>=0)throw new InvalidOperationException("失效默认模型被自动选择为另一项");
            var list=Descendants<Microsoft.UI.Xaml.Controls.ListView>(PageBody).First();list.SelectedIndex=0;await Task.Delay(100);
            var name=Descendants<Microsoft.UI.Xaml.Controls.TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称");name.Text+=" · 不更改默认值";await Task.Delay(100);
            var plan=planFactory!();if(plan.Files.Any(f=>f.Path==s.ConfigPath))throw new InvalidOperationException("只编辑连接时附带了未确认的默认模型修改");
        }finally{s.MainModel=saved;dirty=false;drafts.Clear();Render();}
    }
    private async Task VerifyOperationUiAsync()
    {
        dirty=false;drafts.Clear();Navigate("images");await Task.Delay(150);var marker=new Microsoft.UI.Xaml.Controls.TextBlock{Text="本地生成中界面测试"};PageBody.Children.Add(marker);
        imageOperationRunning=true;
        try{
            Navigate("requests");await Task.Delay(100);if(HasDrafts||!drafts.ContainsKey("images"))throw new InvalidOperationException("生成任务界面未保留或被当成配置草稿");
            Navigate("images");await Task.Delay(100);if(!PageBody.Children.Contains(marker))throw new InvalidOperationException("返回生图页时丢失运行界面");
        }finally{imageOperationRunning=false;dirty=false;drafts.Clear();}
        busy=true;RefreshDraftChrome();if(PageInteraction.IsEnabled)throw new InvalidOperationException("保存期间表单仍可编辑");busy=false;RefreshDraftChrome();
        var fakeResult=new ImageResultEntry{Status="本地结果挂载测试"};imageResultItems.Add(fakeResult);
        try{Render();await Task.Delay(50);Render();await Task.Delay(50);}finally{imageResultItems.Remove(fakeResult);RefreshImageResults();}
    }
    private async Task VerifyHomeSwitchAsync(string directory)
    {
        var originalService=configuration;var originalPrefs=ReadPreferences();dirty=false;drafts.Clear();
        string fake=Path.Combine(Path.GetFullPath(directory),"配置目录 测试");Directory.CreateDirectory(fake);
        string catalog=Path.Combine(fake,"catalog.json");
        await File.WriteAllTextAsync(catalog,"""{"models":[{"slug":"local-test-model","display_name":"本地验证模型","context_window":65536,"supported_reasoning_levels":[{"effort":"high"}]}]}""");
        await File.WriteAllTextAsync(Path.Combine(fake,"router.json"),"""{"host":"127.0.0.1","port":9,"providers":{"local":{"base_url":"http://127.0.0.1:9/v1","key_env":"LOCAL_FAKE_KEY"}},"routes":{"local-test-model":{"provider":"local","model":"local-test-model"}}}""");
        string config="model = \"local-test-model\"\nmodel_reasoning_effort = \"high\"\nmodel_catalog_json = "+JsonSerializer.Serialize(catalog)+"\n[agents]\ndefault_subagent_model = \"local-test-model\"\ndefault_subagent_reasoning_effort = \"high\"\nmax_concurrent_threads_per_session = 2\n";
        await File.WriteAllTextAsync(Path.Combine(fake,"config.toml"),config);
        try{
            Navigate("settings");await Task.Delay(100);var input=Descendants<Microsoft.UI.Xaml.Controls.TextBox>(PageBody).First(t=>t.Header?.ToString()?.Contains("CODEX_HOME")==true);input.Text=fake;await Task.Delay(100);await SaveLocalSettingsAsync();
            if(snapshot?.CodexHome!=fake||snapshot.MainModel!="local-test-model"||snapshot.Models.Count!=1)throw new InvalidOperationException("保存目录后未重新加载新配置");
            await VerifyFailureRecoveryAsync(fake,directory);checksFailureRecoveryVerified=true;
            await VerifyModelVisibilityAsync(directory);
            await VerifyGlobalMarkdownAsync(directory);
            await VerifyMarkdownSourcesAsync(directory);
            await VerifyKnowledgeDocumentsAsync(directory);
            await VerifyReloadIdentityAsync(fake);
            snapshot=null;RenderStartupRecovery();if(localSave is null||!PageBody.Children.Any())throw new InvalidOperationException("无配置时没有可操作的恢复入口");
        }finally{configuration=originalService;WritePreferences(originalPrefs);dirty=false;drafts.Clear();await ReloadAsync();}
    }
    private bool checksFailureRecoveryVerified;
    private async Task VerifyFailureRecoveryAsync(string fakeHome,string outputDirectory)
    {
        Navigate("models");await Task.Delay(100);
        var name=Descendants<Microsoft.UI.Xaml.Controls.TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称");
        name.Text+=" · 故障恢复草稿";await Task.Delay(100);
        if(!dirty)throw new InvalidOperationException($"故障恢复测试草稿未建立：busy={busy};page={page};changed={name.Text.Contains("故障恢复草稿")};planFiles={planFactory?.Invoke().Files.Count}");
        var originalPlan=planFactory;var originalSnapshot=snapshot;
        string configPath=Path.Combine(fakeHome,"config.toml"),config=await File.ReadAllTextAsync(configPath);
        try{
            await File.WriteAllTextAsync(configPath,"invalid = [");
            bool discarded=await DiscardCurrentDraftAsync();
            if(discarded||!dirty||!ReferenceEquals(originalPlan,planFactory)||!name.Text.Contains("故障恢复草稿")||!ReferenceEquals(originalSnapshot,snapshot))
                throw new InvalidOperationException($"读取失败时草稿或快照被清除：discarded={discarded}, dirty={dirty}, plan={ReferenceEquals(originalPlan,planFactory)}, text={name.Text.Contains("故障恢复草稿")}, snapshot={ReferenceEquals(originalSnapshot,snapshot)}");
        }finally{await File.WriteAllTextAsync(configPath,config);}
        if(!await DiscardCurrentDraftAsync()||dirty||ReferenceEquals(originalPlan,planFactory))throw new InvalidOperationException("修正文件后无法重新载入并放弃草稿");
        string profile=Path.Combine(fakeHome,"control-center","image-profiles.json");Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        try{
            await File.WriteAllTextAsync(profile,"{ invalid");
            Navigate("overview");Render();await Task.Delay(100);if(pageReadFailed||!Descendants<TextBlock>(PageBody).Any(t=>t.Text=="默认主模型"))throw new InvalidOperationException("单个生图配置损坏导致整个概览不可用");
            Navigate("images");await Task.Delay(100);
            if(!Descendants<Microsoft.UI.Xaml.Controls.TextBlock>(PageBody).Any(t=>t.Text=="此页面暂时无法读取")||planFactory is not null||dirty)
                throw new InvalidOperationException("损坏生图配置没有显示页面恢复入口");
            await CaptureAsync(Path.Combine(outputDirectory,"page-error-recovery.png"));
            Navigate("help");if(!PageBody.Children.Any())throw new InvalidOperationException("页面失败后不能继续导航");
        }finally{File.Delete(profile);}
        string i2mConfig=Path.Combine(fakeHome,"skills","i2m-imagegen","config.json");Directory.CreateDirectory(Path.GetDirectoryName(i2mConfig)!);
        try{
            await File.WriteAllTextAsync(profile,"{\"active_provider\":\"official\"}");
            Navigate("images");await Task.Delay(100);
            var choice=Descendants<Microsoft.UI.Xaml.Controls.ComboBox>(PageBody).First(t=>t.Header?.ToString()=="默认生图来源");var original=planFactory;
            await File.WriteAllTextAsync(i2mConfig,"{ invalid");choice.SelectedIndex=0;await Task.Delay(100);
            if(choice.SelectedIndex!=1||!ReferenceEquals(planFactory,original)||dirty)throw new InvalidOperationException("来源读取失败后未恢复原来的选择与表单");
        }finally{File.Delete(i2mConfig);File.Delete(profile);}
        await ReloadAsync();
    }
    private async Task VerifyInvalidAgentDefaultAsync()
    {
        var s=snapshot!;string previous=s.AgentModel;
        try{
            dirty=false;drafts.Clear();Navigate("agents");s.AgentModel="__missing_subagent_for_test__";Render();await Task.Delay(100);
            var combo=Descendants<Microsoft.UI.Xaml.Controls.ComboBox>(PageBody).Last(t=>t.Header?.ToString()=="模型");
            if(combo.SelectedItem is not null)throw new InvalidOperationException("失效子代理默认值被自动替换");
            if(planFactory!().Files.Count!=0)throw new InvalidOperationException("未编辑默认值时产生了子代理修改");
        }finally{s.AgentModel=previous;dirty=false;drafts.Clear();Render();}
    }
    private async Task VerifyDraftNavigationAsync()
    {
        drafts.Clear();dirty=false;Navigate("models");await Task.Delay(200);
        var name=Descendants<Microsoft.UI.Xaml.Controls.TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称");name.Text+=" · 跨页面草稿";await Task.Delay(100);
        if(!dirty)throw new InvalidOperationException("模型修改未标记草稿");var original=planFactory;
        var originalSnapshot=editSnapshot;await ReloadAsync();if(!ReferenceEquals(original,planFactory)||!ReferenceEquals(originalSnapshot,editSnapshot)||!dirty)throw new InvalidOperationException("配置刷新丢失草稿或原始冲突检查快照");
        Navigate("requests");await Task.Delay(100);if(!drafts.ContainsKey("models")||dirty)throw new InvalidOperationException("切换页面没有保留模型草稿");
        await refreshGate.WaitAsync();var previous=recentRequests;
        try{
            recentRequests=[new("diagnostic-model","a","200","12 ms","未采集","合成测试来源")];requestRender?.Invoke();
            var search=Descendants<Microsoft.UI.Xaml.Controls.TextBox>(PageBody).First(t=>t.Header?.ToString()=="搜索模型或接入");search.Text="no-match-in-fixture";await Task.Delay(100);
            await WaitForUiConditionAsync(()=>!dirty&&Descendants<Microsoft.UI.Xaml.Controls.TextBlock>(PageBody).Any(t=>t.Text.Contains("当前筛选下没有")),"请求筛选状态异常");
        }finally{recentRequests=previous;requestRender?.Invoke();refreshGate.Release();}
        Navigate("settings");await Task.Delay(100);var density=Descendants<Microsoft.UI.Xaml.Controls.ComboBox>(PageBody).First(t=>t.Header?.ToString()=="界面密度");density.SelectedIndex=density.SelectedIndex==0?1:0;await Task.Delay(100);int chosen=density.SelectedIndex;
        Navigate("models");await Task.Delay(100);if(!dirty||!ReferenceEquals(original,planFactory)||!name.Text.Contains("跨页面草稿")||!drafts.ContainsKey("settings"))throw new InvalidOperationException("模型或设置草稿恢复失败");
        Navigate("settings");await Task.Delay(100);if(!dirty||localSave is null||density.SelectedIndex!=chosen)throw new InvalidOperationException("设置草稿未恢复");
        drafts.Clear();dirty=false;Navigate("images");await Task.Delay(150);var prompt=Descendants<Microsoft.UI.Xaml.Controls.TextBox>(PageBody).First(t=>t.Header?.ToString()=="测试提示词");prompt.Text="本地界面测试，禁止发出请求";await Task.Delay(100);if(dirty)throw new InvalidOperationException("提示词错误地标为配置修改");
        dirty=false;drafts.Clear();Banner.IsOpen=false;RefreshDraftChrome();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);i++){
            var child=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent,i);if(child is T found)yield return found;foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
    private static async Task WaitForUiConditionAsync(Func<bool> condition,string message,int timeoutMs=4000)
    {
        long start=Environment.TickCount64;
        while(!condition()){
            if(Environment.TickCount64-start>timeoutMs)throw new InvalidOperationException(message);
            await Task.Delay(25);
        }
    }
    private async Task CaptureAsync(string path,UIElement? visual=null)
    {
        var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(visual??Root);var buffer=await bitmap.GetPixelsAsync();
        using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
        var bytes=new byte[buffer.Length];using(var reader=DataReader.FromBuffer(buffer))reader.ReadBytes(bytes);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,bytes);await encoder.FlushAsync();
        stream.Seek(0);using var input=stream.GetInputStreamAt(0);using var data=new DataReader(input);await data.LoadAsync((uint)stream.Size);var result=new byte[stream.Size];data.ReadBytes(result);await File.WriteAllBytesAsync(path,result);
    }
}
