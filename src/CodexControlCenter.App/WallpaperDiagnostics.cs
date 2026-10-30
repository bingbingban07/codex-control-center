using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CodexControlCenter.App;

public sealed partial class MainWindow
{
    private async Task VerifyWallpaperAsync(string directory)
    {
        if(!IsDiagnosticRun||!Environment.GetCommandLineArgs().Contains("--state-home"))throw new InvalidOperationException("壁纸自检必须使用隔离目录。");
        var library=WallpaperLibrary;var original=library.LoadSettings();var originalTheme=Root.RequestedTheme;var checks=new List<object>();
        var fixtures=Path.Combine(directory,"自定义图片");Directory.CreateDirectory(fixtures);
        async Task<string> MakeImage(string name,int width,int height,bool alpha=false){
            string path=Path.Combine(fixtures,name);byte[] bytes=new byte[width*height*4];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++){int i=(y*width+x)*4;bytes[i]=(byte)(x*255/width);bytes[i+1]=(byte)(y*255/height);bytes[i+2]=150;bytes[i+3]=alpha&&x<width/2?(byte)90:(byte)255;}
            using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Straight,(uint)width,(uint)height,96,96,bytes);await encoder.FlushAsync();
            stream.Seek(0);using var reader=new DataReader(stream.GetInputStreamAt(0));await reader.LoadAsync((uint)stream.Size);byte[] result=new byte[stream.Size];reader.ReadBytes(result);await File.WriteAllBytesAsync(path,result);return path;
        }
        void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
        WallpaperWorkspace Editor()=>PageBody.Tag as WallpaperWorkspace??throw new InvalidOperationException("自定义壁纸工作区未建立。");
        try{
            Root.RequestedTheme=ElementTheme.Light;dirty=false;Navigate("wallpaper");await FlushUiUpdatesAsync();Render();await Task.Delay(500);
            var editor=Editor();await editor.LoadAssetsAsync();Check(!dirty,"初始化建立了假草稿。");
            Check(Descendants<GridView>(PageBody).Single().Items.Count==0,"初始图库没有优先显示用户图片。");
            string first=await MakeImage("自己的横屏.png",1200,700),second=await MakeImage("自己的竖屏.png",480,800,true),broken=Path.Combine(fixtures,"损坏.png");await File.WriteAllTextAsync(broken,"invalid");
            byte[] Pixels(SoftwareBitmap bitmap){var buffer=new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth*bitmap.PixelHeight*4));bitmap.CopyToBuffer(buffer);using var reader=DataReader.FromBuffer(buffer);var bytes=new byte[buffer.Length];reader.ReadBytes(bytes);return bytes;}
            await editor.ImportFilesAsync([first,second,broken]);Check(dirty&&library.List().Count>=5,"批量导入未留下有效图片与草稿。");
            var previewSettings=editor.Current() with{Opacity=1,Darkness=.2,Overlay=0};
            using(var clear=await WallpaperPreview.RenderAsync(first,previewSettings,false,320,180))
            using(var protectedImage=await WallpaperPreview.RenderAsync(first,previewSettings with{Overlay=1},false,320,180))
                Check(Pixels(clear).SequenceEqual(Pixels(protectedImage)),"文字遮罩错误地改变了整张预览背景。");
            checks.Add(new{previewBlankAreasUnaffectedByTextProtection=true});
            foreach(bool darkTheme in new[]{false,true}){
                using var full=await WallpaperPreview.RenderAsync(first,previewSettings with{Darkness=0},darkTheme,320,180);
                using var half=await WallpaperPreview.RenderAsync(first,previewSettings with{Darkness=.5},darkTheme,320,180);
                using var black=await WallpaperPreview.RenderAsync(first,previewSettings with{Darkness=1},darkTheme,320,180);
                var source=Pixels(full);var dimmed=Pixels(half);var darkest=Pixels(black);
                Check(source.Where((_,i)=>i%4!=3).Any(v=>v>30),"原图亮度预览未绘制。");
                for(int i=0;i<source.Length;i++){if(i%4==3)continue;Check(Math.Abs(dimmed[i]-source[i]*.5)<=1&&darkest[i]==0,"压暗预览没有按亮度等比降低。");}
            }
            checks.Add(new{fullOpacityImage=true,dimPixelsProportional=true,zeroOriginalAndMaximumBlack=true,lightAndDarkDimmingConsistent=true});
            Check(File.Exists(first)&&File.Exists(second)&&Banner.IsOpen&&Banner.Message.Contains("损坏.png"),"原图保留或逐文件错误说明失效。");
            Check(!Descendants<ProgressBar>(PageBody).Any(p=>p.Visibility==Visibility.Visible),"导入结束后进度没有恢复。");
            Check(Descendants<GridView>(PageBody).Single().Items.Count==2,"自定义图片筛选错误。");Banner.IsOpen=false;
            editor.Zoom.Value=1.63;editor.PositionX.Value=21;editor.PositionY.Value=78;editor.Intensity.Value=61;editor.Overlay.Value=24;
            await WaitForUiConditionAsync(()=>Descendants<Image>(PageBody).Any(i=>i.Tag is WallpaperPreviewStamp{Settings:{Zoom:1.63,PositionX:21,PositionY:78,Opacity:1,Darkness:.61},Dark:false}),"最新自定义构图没有渲染",12000);
            Check(ApplyButton.IsEnabled&&ApplyButton.Content as string=="应用壁纸","应用按钮不可操作。");
            await CaptureAsync(Path.Combine(directory,"custom-workspace-light.png"));checks.Add(new{customFirst=true,batchImport=true,partialFailureExplanation=true,originalsPreserved=true,continuousComposition=true});
            PageScroll.ChangeView(null,PageScroll.ScrollableHeight,null,true);await Task.Delay(250);await CaptureAsync(Path.Combine(directory,"custom-gallery.png"));PageScroll.ChangeView(null,0,null,true);
            Navigate("overview");await FlushUiUpdatesAsync();Navigate("wallpaper");await FlushUiUpdatesAsync();Check(ReferenceEquals(editor,Editor())&&editor.Zoom.Value==1.63&&dirty,"跨页构图草稿未恢复。");
            await localSave!();dirty=false;var committed=library.LoadSettings();Check(committed.Zoom==1.63&&committed.PositionX==21&&committed.PositionY==78,"构图应用结果不一致。");
            editor.Zoom.Value=1.75;library.SaveSnapshot(committed with{Blur=4},library.SettingsRevision());bool rejected=false;try{await localSave!();}catch(InvalidOperationException){rejected=true;}Check(rejected&&dirty&&editor.Zoom.Value==1.75,"外部冲突没有保留草稿。");
            dirty=false;drafts.Clear();Render();await Editor().LoadAssetsAsync();Root.RequestedTheme=ElementTheme.Dark;
            await WaitForUiConditionAsync(()=>Descendants<Image>(PageBody).Any(i=>i.Tag is WallpaperPreviewStamp{Dark:true}),"深色预览未更新",10000);await CaptureAsync(Path.Combine(directory,"custom-workspace-dark.png"));
            var search=Descendants<TextBox>(PageBody).Single(t=>t.Header as string=="搜索图片");search.Text="___empty___";await FlushUiUpdatesAsync();Check(!dirty&&Descendants<GridView>(PageBody).Single().Items.Count==0,"图库搜索产生草稿或空状态错误。");Check(!Descendants<Button>(PageBody).Single(b=>b.Content as string=="删除所选副本").IsEnabled,"筛选隐藏选择后仍可误删图片。");search.Text="";await FlushUiUpdatesAsync();
            var asset=library.List().First(a=>a.Name.Contains("横屏"));await Task.Run(()=>library.SetFavorite(asset.Id,true));await Task.Run(()=>library.Rename(asset.Id,"我的收藏背景"));await Editor().LoadAssetsAsync();
            var scope=Descendants<ComboBox>(PageBody).Single(c=>c.Header as string=="图片范围");scope.SelectedIndex=2;await FlushUiUpdatesAsync();Check(Descendants<GridView>(PageBody).Single().Items.Count==1,"收藏筛选没有刷新。");scope.SelectedIndex=0;await FlushUiUpdatesAsync();
            checks.Add(new{crossPageDrafts=true,compositionSave=true,externalConflictRetainsDraft=true,darkPreview=true,searchAndFavorites=true,metadataRefresh=true});
            uint dpi=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));double scale=dpi/96.0;AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(900*scale),(int)(980*scale)));bodySize=20;dirty=false;Render();await Editor().LoadAssetsAsync();await Task.Delay(400);Root.UpdateLayout();
            Check(PageBody.DesiredSize.Width<=PageScroll.ViewportWidth+4,"自定义壁纸窄窗口横向溢出。");await CaptureAsync(Path.Combine(directory,"custom-workspace-compact.png"));
            var frame=Descendants<ComboBox>(PageBody).Single(c=>c.Header as string=="模拟窗口画幅");frame.SelectedIndex=4;
            await WaitForUiConditionAsync(()=>Descendants<Image>(PageBody).Any(i=>i.Tag is WallpaperPreviewStamp{Width:720,Height:1280}),"竖屏图像未完成构图渲染",10000);
            var portrait=(Grid)Descendants<Image>(PageBody).Single(i=>i.Tag is WallpaperPreviewStamp{Width:720,Height:1280}).Parent;Root.UpdateLayout();
            Check(Math.Abs(portrait.ActualWidth/portrait.ActualHeight-9.0/16)<.01,"竖屏预览被拉伸。");await CaptureAsync(Path.Combine(directory,"custom-workspace-portrait-preview.png"),portrait);
            var stress=Editor();for(int i=0;i<80;i++){stress.Zoom.Value=1+(i%40)*.03;stress.PositionX.Value=i%100;if(i%8==0){Navigate("requests");await FlushUiUpdatesAsync();Navigate("wallpaper");await FlushUiUpdatesAsync();Check(ReferenceEquals(stress,Editor()),"压力切页丢失保留编辑器。");}}
            await WaitForUiConditionAsync(()=>Descendants<Image>(PageBody).Any(i=>i.Tag is WallpaperPreviewStamp stamp&&Math.Abs(stamp.Settings.Zoom-stress.Zoom.Value)<.0001),"连续调整预览没有追上最终参数",15000);
            checks.Add(new{compactLargeDensity=true,portraitFrame=true,continuousSliderEdits=80,stressDraftRestores=10});
            var importCopies=new List<string>();for(int i=0;i<40;i++){string copy=Path.Combine(fixtures,"批量取消-"+i+".png");File.Copy(first,copy);importCopies.Add(copy);}
            var importing=stress.ImportFilesAsync(importCopies);Navigate("requests");await FlushUiUpdatesAsync();await importing;Navigate("wallpaper");await FlushUiUpdatesAsync();
            Check(ReferenceEquals(stress,Editor())&&!Descendants<ProgressBar>(PageBody).Any(p=>p.Visibility==Visibility.Visible),"切页取消导入后进度界面未复位。");
            CodexWallpaperRuntime.WriteJson(CodexWallpaperRuntime.StatusPath(WallpaperRoot),new WallpaperRuntimeStatus("applied","上一版外观已应用",1,"older-revision",DateTimeOffset.UtcNow){Version="0.7.0",ProtocolVersion=2});stress.RefreshStatus();
            Check(Descendants<TextBlock>(PageBody).Any(t=>t.Text.Contains("等待 Codex 同步")),"文件保存与窗口实际同步没有区分。");File.Delete(CodexWallpaperRuntime.StatusPath(WallpaperRoot));
            checks.Add(new{cancelImportAcrossPagesResetsProgress=true,pendingRuntimeNeverShownAsApplied=true});
            File.WriteAllText(library.SettingsPath,"{ broken custom wallpaper");dirty=false;drafts.Clear();Render();Check(Descendants<TextBlock>(PageBody).Any(t=>t.Text=="壁纸设置需要恢复"),"损坏配置没有恢复界面。");library.RecoverSettings(true,library.SettingsRevision());
            checks.Add(new{damagedSettingsRecovery=true});
            await File.WriteAllTextAsync(Path.Combine(directory,"wallpaper-result.json"),JsonSerializer.Serialize(new{success=true,checks},new JsonSerializerOptions{WriteIndented=true}));
        }finally{
            wallpaperImportCancellation?.Cancel();await StopWallpaperTrialAsync();try{library.LoadSettings();}catch(InvalidDataException){library.RecoverSettings(false,library.SettingsRevision());}
            library.SaveSettings(original,library.SettingsRevision());Root.RequestedTheme=originalTheme;dirty=false;drafts.Clear();
        }
    }
}
