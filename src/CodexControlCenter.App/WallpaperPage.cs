using CodexControlCenter.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using System.Text.Json;

namespace CodexControlCenter.App;

public sealed partial class MainWindow
{
    private sealed record WallpaperPreviewStamp(WallpaperSettings Settings,bool Dark,int Width=0,int Height=0);
    private string WallpaperRoot=>Path.Combine(stateHome,"wallpaper");
    private WallpaperLibrary WallpaperLibrary=>new(WallpaperRoot);
    private bool IsDiagnosticRun=>Environment.GetCommandLineArgs().Any(a=>a is "--smoke" or "--stress-ui");
    private CancellationTokenSource? wallpaperImportCancellation;
    private string? wallpaperTrialId;
    private WallpaperWorkspace? wallpaperTrialOwner;
    private DispatcherQueueTimer? wallpaperTrialTimer;
    private bool wallpaperTrialRenewing;
    private Task wallpaperTrialStopping=Task.CompletedTask;
    private HashSet<string> wallpaperPresetIds=[];

    private async Task InitializeWallpaperSafelyAsync()
    {
        try{
            var library=WallpaperLibrary;string marker=Path.Combine(WallpaperRoot,"bundled-v2.json");
            if(!File.Exists(marker)){
                string bundled=Path.Combine(appRoot,"assets","wallpapers");var ids=new List<string>();
                var skipped=new List<string>();
                if(Directory.Exists(bundled))foreach(string file in Directory.EnumerateFiles(bundled,"*.jpg")){
                    try{var asset=await Task.Run(()=>library.ImportAsync(file));ids.Add(asset.Id);wallpaperPresetIds.Add(asset.Id);}
                    catch(Exception error){skipped.Add(Path.GetFileName(file));UiCrashLog.Exception("wallpaper.preset",error);}
                }
                CodexWallpaperRuntime.WriteJson(marker,new{version=2,ids,skipped});
            }
            using(var doc=JsonDocument.Parse(await File.ReadAllBytesAsync(marker)))wallpaperPresetIds=doc.RootElement.GetProperty("ids").EnumerateArray().Select(v=>v.GetString()!).ToHashSet();
            if(page=="wallpaper"&&PageBody.Tag is WallpaperWorkspace workspace)await workspace.LoadAssetsAsync();
            if(!IsDiagnosticRun){string? executable=CodexWallpaperRuntime.FindExecutable(WallpaperRoot);if(executable is not null)CodexWallpaperRuntime.RememberExecutable(WallpaperRoot,executable);if(EnhancementMode.IsEnabled(snapshot?.CodexHome)&&library.LoadSettings().Enabled)StartWallpaperHost(false);}
        }catch(Exception error){UiCrashLog.Exception("wallpaper.initialize",error);}
    }
    private void StartWallpaperHost(bool launch)
    {
        if(IsDiagnosticRun)return;
        if(!EnhancementMode.IsEnabled(snapshot?.CodexHome))throw new InvalidOperationException("当前为原版模式。请先在设置与备份中启用增强模式，再连接壁纸。");
        string helper=Path.Combine(appRoot,"helpers","wallpaper","CodexControlCenter.WallpaperHost.exe");
        if(!File.Exists(helper))throw new FileNotFoundException("缺少壁纸组件，请重新解压完整便携包。",helper);
        var start=new System.Diagnostics.ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--root");start.ArgumentList.Add(WallpaperRoot);if(launch)start.ArgumentList.Add("--launch");
        System.Diagnostics.Process.Start(start)?.Dispose();
    }
    private void UpdateWallpaperStatus()
    {
        if(page=="wallpaper"&&PageBody.Tag is WallpaperWorkspace workspace)workspace.RefreshStatus();
    }
    private void RenderWallpaper()
    {
        PageTitle.Text="自定义壁纸";PageSubtitle.Text="导入喜欢的图片，调整构图，再应用到 Codex";
        try{var workspace=new WallpaperWorkspace(this);PageBody.Tag=workspace;workspace.Render();}
        catch(Exception error)when(error is IOException or InvalidDataException or FormatException or ArgumentException){RenderWallpaperRecovery(error);}
    }
    private void OnWallpaperPageLeaving()
    {
        wallpaperImportCancellation?.Cancel();if(PageBody.Tag is WallpaperWorkspace editor)editor.Suspend();_ = StopWallpaperTrialAsync();
    }
    private async Task StopWallpaperTrialAsync()
    {
        wallpaperTrialTimer?.Stop();string? id=wallpaperTrialId;wallpaperTrialId=null;wallpaperTrialOwner=null;
        if(id is not null){wallpaperTrialStopping=Task.Run(()=>{try{new WallpaperPreviewService(WallpaperLibrary).Stop(id);}catch(Exception error){UiCrashLog.Exception("wallpaper.preview.stop",error);}});}
        await wallpaperTrialStopping;
    }
    private async Task BeginWallpaperTrialAsync(WallpaperWorkspace editor)
    {
        if(IsDiagnosticRun)throw new InvalidOperationException("界面自检不连接用户的 Codex 窗口。");
        var status=CodexWallpaperRuntime.ReadStatus(WallpaperRoot);
        if(status.State is not("applied" or "disabled" or "ready" or "preview"))throw new InvalidOperationException("请先通过壁纸入口打开并连接 Codex，再使用实际窗口试用。");
        if(status.ProtocolVersion<2)throw new InvalidOperationException("当前壁纸组件为旧版，请正常退出 Codex，再从新版“Codex（壁纸）”启动。");
        await StopWallpaperTrialAsync();var settings=editor.Current();string revision=editor.Revision;
        var lease=await Task.Run(()=>new WallpaperPreviewService(WallpaperLibrary).Start(settings,revision));
        if(page!="wallpaper"||!ReferenceEquals(PageBody.Tag,editor)){await Task.Run(()=>new WallpaperPreviewService(WallpaperLibrary).Stop(lease.Id));return;}
        wallpaperTrialId=lease.Id;wallpaperTrialOwner=editor;
        wallpaperTrialTimer??=DispatcherQueue.CreateTimer();wallpaperTrialTimer.Stop();wallpaperTrialTimer.Interval=TimeSpan.FromSeconds(2);
        if(!wallpaperTrialTimerAttached){wallpaperTrialTimerAttached=true;wallpaperTrialTimer.Tick+=async(_,_)=>{
            if(wallpaperTrialRenewing||wallpaperTrialId is null||wallpaperTrialOwner is null)return;
            wallpaperTrialRenewing=true;string id=wallpaperTrialId;var owner=wallpaperTrialOwner;var current=owner.Current();
            try{await Task.Run(()=>new WallpaperPreviewService(WallpaperLibrary).Renew(id,current));}
            catch(Exception error){if(wallpaperTrialId==id){await StopWallpaperTrialAsync();if(page=="wallpaper")ShowNotice("已结束实际窗口试用",Safe(error.Message)+" 草稿仍保留。",InfoBarSeverity.Warning);}}
            finally{wallpaperTrialRenewing=false;if(page=="wallpaper")UpdateWallpaperStatus();}
        };}
        wallpaperTrialTimer.Start();editor.RefreshStatus();
    }
    private bool wallpaperTrialTimerAttached;

    private void RenderWallpaperRecovery(Exception error)
    {
        var library=WallpaperLibrary;var panel=Section("壁纸设置需要恢复");panel.Children.Add(Note(Safe(error.Message)));panel.Children.Add(Note(library.SettingsPath));
        panel.Children.Add(Note("恢复前保留当前文件的原始副本，已导入图片不受影响。"));
        async Task Recover(bool backup){
            string revision=library.SettingsRevision();var dialog=new ContentDialog{Title=backup?"恢复上次壁纸设置？":"重置并关闭壁纸？",Content="当前设置保留为恢复前副本，图片库和其他 Codex 配置均保留。",PrimaryButtonText="确认恢复",CloseButtonText="取消",XamlRoot=Root.XamlRoot};
            if(await ShowDialogAsync(dialog)!=ContentDialogResult.Primary)return;
            await StopWallpaperTrialAsync();busy=true;RefreshDraftChrome();try{await Task.Run(()=>library.RecoverSettings(backup,revision));}finally{busy=false;RefreshDraftChrome();}
            Render();if(!IsDiagnosticRun)StartWallpaperHost(false);
        }
        var restore=AsyncAction("恢复上次壁纸设置",()=>Recover(true));restore.IsEnabled=File.Exists(library.SettingsPath+".bak");panel.Children.Add(restore);panel.Children.Add(AsyncAction("重置壁纸设置",()=>Recover(false)));Add(Card(panel));
    }
}
