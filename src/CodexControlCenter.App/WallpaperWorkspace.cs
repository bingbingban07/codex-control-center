using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.Text.Json;

namespace CodexControlCenter.App;

public sealed partial class MainWindow
{
    private sealed class WallpaperWorkspace
    {
        private readonly MainWindow w;
        private readonly object owner;
        private readonly WallpaperLibrary library;
        private WallpaperSettings saved;
        public string Revision {get;private set;}
        private IReadOnlyList<WallpaperAsset> assets=[];
        private IReadOnlyList<string> unavailable=[];
        private string? selectedId;
        private bool populating,ready,suspended;
        private CancellationTokenSource? previewCancellation;
        private readonly SemaphoreSlim catalogGate=new(1);
        private readonly SemaphoreSlim thumbnailGate=new(3);
        private CancellationTokenSource? thumbnailCancellation;
        private int previewSequence;
        private readonly ToggleSwitch enabled=new(){Header="在 Codex 中显示壁纸",OnContent="开启",OffContent="关闭"};
        private readonly Image previewImage=new(){Stretch=Stretch.Uniform};
        private readonly Border previewBorder=new(){CornerRadius=new CornerRadius(16),BorderThickness=new Thickness(1)};
        private readonly Grid previewCanvas=new(){Height=410};
        private readonly GridView gallery=new(){SelectionMode=ListViewSelectionMode.Single,MaxHeight=460,IsItemClickEnabled=false};
        private readonly TextBlock selectionLabel=new(){TextWrapping=TextWrapping.Wrap,FontSize=22};
        private readonly TextBlock statusLabel=new(){TextWrapping=TextWrapping.Wrap};
        private readonly TextBlock valuesLabel=new(){TextWrapping=TextWrapping.Wrap};
        private readonly TextBlock draftLabel=new(){TextWrapping=TextWrapping.Wrap};
        private readonly TextBlock importLabel=new(){TextWrapping=TextWrapping.Wrap};
        private readonly TextBlock emptyLabel=new(){TextWrapping=TextWrapping.Wrap};
        private readonly TextBlock libraryWarning=new(){TextWrapping=TextWrapping.Wrap};
        private Button cleanupButton=null!;
        private readonly StackPanel windowDetails=new(){Spacing=8};
        private readonly ProgressBar importProgress=new(){Minimum=0,Maximum=100,Visibility=Visibility.Collapsed};
        private TextBox search=null!;
        private ComboBox fit=null!,scope=null!,sort=null!,previewTheme=null!,aspect=null!;
        public Slider PositionX=null!,PositionY=null!,Zoom=null!,Intensity=null!,Blur=null!,Overlay=null!;
        private readonly List<Border> previewTextMasks=[];
        private Button trialButton=null!,stopButton=null!,favoriteButton=null!,renameButton=null!,deleteButton=null!,cancelImport=null!;
        private string statusFingerprint="";
        public WallpaperWorkspace(MainWindow window)
        {
            w=window;owner=w.pageInstance;library=w.WallpaperLibrary;(saved,Revision)=library.LoadSnapshot();selectedId=saved.ImageId;
        }
        private bool IsCurrent=>!w.lifetime.IsCancellationRequested&&w.page=="wallpaper"&&ReferenceEquals(w.pageInstance,owner);
        public WallpaperSettings Current()=>saved with{
            Enabled=enabled.IsOn,ImageId=!enabled.IsOn&&ready&&!assets.Any(a=>a.Id==selectedId)?null:selectedId,
            Fit=fit.SelectedIndex==1?"contain":"cover",PositionX=PositionX.Value,PositionY=PositionY.Value,Zoom=Zoom.Value,
            Opacity=1,Darkness=Intensity.Value/100,Blur=Blur.Value,Overlay=Overlay.Value/100};

        public void Render()
        {
            var heading=w.Section();heading.Spacing=8;heading.Children.Add(w.Text("把喜欢的图片，变成自己的工作空间",28));heading.Children.Add(w.Note("图片保存在本机。先调整草稿，试用满意后再应用；随时可恢复原来的外观。"));heading.Children.Add(w.AsyncAction("＋ 导入自定义壁纸",PickFilesAsync));w.Add(heading);
            var connection=w.Section();statusLabel.FontSize=w.bodySize;connection.Children.Add(statusLabel);
            var restorePrevious=w.AsyncAction("恢复上次应用",RestorePreviousAsync);restorePrevious.IsEnabled=File.Exists(library.SettingsPath+".bak");
            connection.Children.Add(w.EvenGrid(260,3,
                w.AsyncAction("连接 / 启动 Codex",async()=>{w.StartWallpaperHost(true);await Task.Delay(400);RefreshStatus();}),
                restorePrevious,
                w.AsyncAction("导出壁纸诊断",ExportDiagnosticsAsync)));
            var advanced=w.Section();advanced.Children.Add(w.Note("首次连接请正常退出 Codex，再用桌面“Codex”启动。以后应用会自动同步，管理台可以关闭。"));advanced.Children.Add(windowDetails);
            advanced.Children.Add(w.AsyncAction("定位 Codex 程序",LocateCodexAsync));connection.Children.Add(new Expander{Header="连接详情与处理方法",Content=advanced,HorizontalAlignment=HorizontalAlignment.Stretch});w.Add(w.Card(connection));

            fit=w.Choice("缩放方式",["铺满画面","完整显示"],saved.Fit=="contain"?"完整显示":"铺满画面",track:false);
            previewTheme=w.Choice("预览主题",["跟随管理台","浅色","深色"],"跟随管理台",track:false);
            aspect=w.Choice("模拟窗口画幅",["16:9 标准横屏","16:10 工作窗口","21:9 超宽屏","3:2 紧凑窗口","9:16 竖屏"],"16:9 标准横屏",track:false);
            Slider Slider(string title,double min,double max,double value,double step){var slider=new Slider{Header=title,Minimum=min,Maximum=max,Value=value,StepFrequency=step,FontSize=w.bodySize};AutomationProperties.SetName(slider,title);return slider;}
            PositionX=Slider("水平焦点",0,100,saved.PositionX,1);PositionY=Slider("垂直焦点",0,100,saved.PositionY,1);Zoom=Slider("图片缩放",1,3,saved.Zoom,.01);
            Intensity=Slider("压暗程度",0,100,saved.Dimming*100,1);Blur=Slider("背景模糊",0,64,saved.Blur,1);Overlay=Slider("阅读底色",0,100,saved.Overlay*100,1);enabled.IsOn=saved.Enabled;enabled.FontSize=w.bodySize;
            var preview=w.Section();preview.Children.Add(selectionLabel);previewCanvas.Children.Add(previewImage);
            var sample=new Grid{Padding=new Thickness(28),RowSpacing=14,IsHitTestVisible=false};sample.RowDefinitions.Add(new(){Height=GridLength.Auto});sample.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});sample.RowDefinitions.Add(new(){Height=GridLength.Auto});
            Border ProtectedText(string text,double size){var mask=new Border{Child=w.Text(text,size),HorizontalAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(10,6,10,6),CornerRadius=new CornerRadius(8)};previewTextMasks.Add(mask);return mask;}
            sample.Children.Add(w.Text("Codex   /   工作空间",16));
            var sampleText=ProtectedText("让背景安静一点，让想法更清晰。\n• 段落与列表共用一块平整底色\n• 不再逐行叠加白色胶囊",22);sampleText.VerticalAlignment=VerticalAlignment.Center;Grid.SetRow(sampleText,1);sample.Children.Add(sampleText);
            var composer=new Border{Child=w.Text("发送消息…",16),CornerRadius=new CornerRadius(14),Padding=new Thickness(18),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(130,128,128,128))};Grid.SetRow(composer,2);sample.Children.Add(composer);previewCanvas.Children.Add(sample);
            previewBorder.Child=previewCanvas;previewBorder.BorderBrush=w.BrushResource("CardStrokeColorDefaultBrush");preview.Children.Add(previewBorder);
            preview.Children.Add(w.EvenGrid(220,2,aspect,previewTheme));draftLabel.FontSize=16;preview.Children.Add(draftLabel);
            var previewActions=w.EvenGrid(220,2,trialButton=w.AsyncAction("在 Codex 中临时试用",()=>w.BeginWallpaperTrialAsync(this)),stopButton=w.AsyncAction("结束试用，恢复已应用",async()=>{await w.StopWallpaperTrialAsync();RefreshStatus();}));preview.Children.Add(previewActions);
            preview.Children.Add(w.Note("临时试用不保存。切页后自动结束；管理台异常退出时，最长约 10 秒恢复。画幅用于模拟布局，实际背景会适应 Codex 窗口。"));

            var controls=w.Section("构图与可读性");controls.Children.Add(enabled);controls.Children.Add(fit);
            controls.Children.Add(w.EvenGrid(220,2,PositionX,PositionY));controls.Children.Add(Zoom);controls.Children.Add(Intensity);controls.Children.Add(Blur);controls.Children.Add(Overlay);
            controls.Children.Add(w.Note("压暗程度：0% 保留原图，向右越暗，100% 为黑色。每段回复共用一块阅读底色，包含列表与段落间隙，不逐行叠加或泛光；发送气泡、代码块和菜单保留原生样式。"));
            valuesLabel.FontSize=16;controls.Children.Add(valuesLabel);controls.Children.Add(w.EvenGrid(200,2,
                w.Action("构图居中",()=>{PositionX.Value=50;PositionY.Value=50;Zoom.Value=1;}),
                w.Action("恢复默认外观",()=>{enabled.IsOn=false;PositionX.Value=50;PositionY.Value=50;Zoom.Value=1;Intensity.Value=20;Blur.Value=0;Overlay.Value=35;Changed();})));
            var workspaceGrid=new Grid{ColumnSpacing=24,RowSpacing=22};workspaceGrid.ColumnDefinitions.Add(new(){Width=new GridLength(1.65,GridUnitType.Star)});workspaceGrid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});workspaceGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});workspaceGrid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            var left=w.Card(preview);var right=w.Card(controls);workspaceGrid.Children.Add(left);Grid.SetColumn(right,1);workspaceGrid.Children.Add(right);
            workspaceGrid.SizeChanged+=(_,e)=>{bool narrow=e.NewSize.Width<1100;Grid.SetColumn(right,narrow?0:1);Grid.SetRow(right,narrow?1:0);Grid.SetColumnSpan(left,narrow?2:1);Grid.SetColumnSpan(right,narrow?2:1);ResizePreview();};w.Add(workspaceGrid);

            var collection=w.Section("我的图片");collection.Children.Add(w.Note("拖入图片，或一次选择多张。支持 PNG、JPEG、WebP、BMP；只操作管理台副本，原图保持不变。"));
            var importButtons=w.EvenGrid(240,2,w.AsyncAction("＋ 导入自己的图片",PickFilesAsync),cancelImport=w.Action("取消导入",()=>w.wallpaperImportCancellation?.Cancel()));cancelImport.IsEnabled=false;collection.Children.Add(importButtons);collection.Children.Add(importProgress);collection.Children.Add(importLabel);
            search=w.Input("搜索图片","",track:false);search.PlaceholderText="按名称查找";
            scope=w.Choice("图片范围",["我的图片","全部图片","仅收藏","示例背景"],selectedId is not null&&w.wallpaperPresetIds.Contains(selectedId)?"全部图片":"我的图片",track:false);
            sort=w.Choice("排序",["最近导入","名称","分辨率"],"最近导入",track:false);
            collection.Children.Add(w.EvenGrid(240,3,search,scope,sort));collection.Children.Add(emptyLabel);collection.Children.Add(gallery);
            favoriteButton=w.AsyncAction("收藏所选图片",async()=>{var asset=GallerySelection();if(asset is null)return;await Task.Run(()=>library.SetFavorite(asset.Id,!asset.IsFavorite));await LoadAssetsAsync();});
            renameButton=w.AsyncAction("重命名所选图片",RenameAsync);deleteButton=w.AsyncAction("删除所选副本",DeleteAsync);
            collection.Children.Add(w.EvenGrid(240,3,favoriteButton,renameButton,deleteButton));collection.Children.Add(libraryWarning);cleanupButton=w.AsyncAction("清理不可用的图片副本",CleanupUnavailableAsync);collection.Children.Add(cleanupButton);collection.Children.Add(w.Note("图片名称、收藏和删除立即保存；构图与启停通过底部“应用壁纸”保存。"));
            var drop=w.Card(collection);w.PageBody.AllowDrop=true;w.PageBody.DragOver+=(_,e)=>{if(e.DataView.Contains(StandardDataFormats.StorageItems)){e.AcceptedOperation=DataPackageOperation.Copy;e.DragUIOverride.Caption="导入为自定义壁纸";}};
            w.PageBody.Drop+=async(_,e)=>{var deferral=e.GetDeferral();try{if(e.DataView.Contains(StandardDataFormats.StorageItems)){var items=await e.DataView.GetStorageItemsAsync();await ImportFilesAsync(items.OfType<StorageFile>().Select(f=>f.Path));}}catch(Exception error){if(IsCurrent)w.ShowError("导入图片失败",error);}finally{deferral.Complete();}};w.Add(drop);
            gallery.SelectionChanged+=(_,_)=>{if(populating||!IsCurrent)return;var id=(gallery.SelectedItem as GridViewItem)?.Tag as string;if(id is null)return;selectedId=id;enabled.IsOn=true;Changed();RefreshSelection();};
            void Filter()=>w.QueueUiUpdate("wallpaper.gallery",owner,PopulateGallery);
            search.TextChanged+=(_,_)=>Filter();scope.SelectionChanged+=(_,_)=>Filter();sort.SelectionChanged+=(_,_)=>Filter();
            foreach(var slider in new[]{PositionX,PositionY,Zoom,Intensity,Blur,Overlay})slider.ValueChanged+=(_,_)=>Changed();
            enabled.Toggled+=(_,_)=>Changed();fit.SelectionChanged+=(_,_)=>Changed();previewTheme.SelectionChanged+=(_,_)=>SchedulePreview();aspect.SelectionChanged+=(_,_)=>{ResizePreview();SchedulePreview();};
            previewCanvas.ActualThemeChanged+=(_,_)=>{if(previewTheme.SelectedIndex==0)SchedulePreview();};
            w.localSave=SaveAsync;RefreshSelection();RefreshStatus();_ = LoadAssetsAsync();
        }

        private WallpaperAsset? Selected()=>assets.FirstOrDefault(a=>a.Id==selectedId);
        private WallpaperAsset? GallerySelection()=>(gallery.SelectedItem as GridViewItem)?.Tag is string id?assets.FirstOrDefault(a=>a.Id==id):null;
        private void Changed()
        {
            if(populating||!IsCurrent)return;w.dirty=Current()!=saved;w.RefreshDraftChrome();
            draftLabel.Text=w.dirty?"当前为未应用草稿 · 底部应用后自动同步":"显示当前已保存设置 · Codex 应用情况见连接状态";SchedulePreview();
        }
        private void ResizePreview()
        {
            double ratio=aspect?.SelectedIndex switch{1=>1.6,2=>21.0/9,3=>1.5,4=>9.0/16,_=>16.0/9};
            double available=(previewBorder.Parent as FrameworkElement)?.ActualWidth??previewBorder.ActualWidth;
            if(available>0){double width=Math.Min(available,620*ratio);previewBorder.Width=width;previewBorder.HorizontalAlignment=HorizontalAlignment.Center;previewCanvas.Height=width/ratio;}
        }
        private void SchedulePreview()
        {
            if(suspended||!IsCurrent)return;previewCancellation?.Cancel();previewCancellation?.Dispose();previewCancellation=CancellationTokenSource.CreateLinkedTokenSource(w.lifetime.Token);var ct=previewCancellation.Token;int sequence=++previewSequence;
            _=RenderPreviewAsync(sequence,ct);
        }
        private async Task RenderPreviewAsync(int sequence,CancellationToken ct)
        {
            try{
                await Task.Delay(120,ct);var asset=Selected();var settings=Current();valuesLabel.Text=$"焦点 {settings.PositionX:0}% / {settings.PositionY:0}% · 缩放 {settings.Zoom:0.00}×\n压暗 {settings.Dimming:P0} · 模糊 {settings.Blur:0} · 阅读底色 {settings.Overlay:P0}";
                bool dark=previewTheme.SelectedIndex==2||previewTheme.SelectedIndex==0&&w.Root.ActualTheme==ElementTheme.Dark;previewCanvas.RequestedTheme=previewTheme.SelectedIndex==0?ElementTheme.Default:dark?ElementTheme.Dark:ElementTheme.Light;
                previewCanvas.Background=new SolidColorBrush(dark?Windows.UI.Color.FromArgb(255,24,24,24):Microsoft.UI.Colors.White);
                byte maskAlpha=settings.Enabled?(byte)Math.Round(settings.Overlay*.78*255):(byte)0;
                var maskBrush=new SolidColorBrush(dark?Windows.UI.Color.FromArgb(maskAlpha,24,24,24):Windows.UI.Color.FromArgb(maskAlpha,255,255,255));
                foreach(var mask in previewTextMasks)mask.Background=maskBrush;
                if(asset is null){previewImage.Source=null;return;}
                (int width,int height)=aspect.SelectedIndex switch{1=>(1200,750),2=>(1400,600),3=>(1200,800),4=>(720,1280),_=>(1200,675)};
                using var bitmap=await WallpaperPreview.RenderAsync(asset.Path,settings,dark,width,height,ct);ct.ThrowIfCancellationRequested();var source=new SoftwareBitmapSource();await source.SetBitmapAsync(bitmap);
                if(sequence!=previewSequence||!IsCurrent||ct.IsCancellationRequested)return;previewImage.Source=source;previewImage.Tag=new WallpaperPreviewStamp(settings,dark,width,height);
            }catch(OperationCanceledException){}catch(Exception error){if(IsCurrent&&sequence==previewSequence)draftLabel.Text="预览暂不可用："+Safe(error.Message)+"。可重新导入图片或选择另一张。";}
        }
        public async Task LoadAssetsAsync()
        {
            try{await catalogGate.WaitAsync(w.lifetime.Token);}catch(OperationCanceledException){return;}
            try{var loaded=await Task.Run(()=>new{Assets=library.List(),Unavailable=library.UnavailableImageIds()});if(w.lifetime.IsCancellationRequested)return;assets=loaded.Assets;unavailable=loaded.Unavailable;ready=true;if(IsCurrent){PopulateGallery();RefreshSelection();SchedulePreview();}}
            catch(Exception error){if(IsCurrent)w.ShowError("图片库暂不可用",error);}finally{catalogGate.Release();}
        }
        private void PopulateGallery()
        {
            if(!IsCurrent)return;populating=true;
            try{
                thumbnailCancellation?.Cancel();thumbnailCancellation?.Dispose();thumbnailCancellation=CancellationTokenSource.CreateLinkedTokenSource(w.lifetime.Token);var thumbnailToken=thumbnailCancellation.Token;
                var filtered=assets.Where(a=>scope.SelectedIndex switch{0=>!w.wallpaperPresetIds.Contains(a.Id),2=>a.IsFavorite,3=>w.wallpaperPresetIds.Contains(a.Id),_=>true}).Where(a=>a.Name.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase));
                filtered=sort.SelectedIndex switch{1=>filtered.OrderBy(a=>a.Name,StringComparer.CurrentCultureIgnoreCase),2=>filtered.OrderByDescending(a=>(long)a.Width*a.Height),_=>filtered.OrderByDescending(a=>a.ImportedAt)};
                w.ClearControlItems(gallery);foreach(var asset in filtered){
                    var content=w.Section();content.Spacing=8;content.Width=210;
                    var thumb=new Image{Width=210,Height=118,Stretch=Stretch.UniformToFill};
                    bool repaired=false;thumb.ImageFailed+=(_,_)=>{if(repaired||thumbnailToken.IsCancellationRequested||!IsCurrent)return;repaired=true;_ = LoadThumbnailAsync(asset,thumb,thumbnailToken,true);};
                    thumb.Loaded+=(_,_)=>{if(!IsCurrent||thumbnailToken.IsCancellationRequested)return;if(asset.ThumbnailPath is not null)thumb.Source=new BitmapImage{DecodePixelWidth=420,UriSource=new Uri(asset.ThumbnailPath)};else _=LoadThumbnailAsync(asset,thumb,thumbnailToken);};
                    thumb.Unloaded+=(_,_)=>thumb.Source=null;
                    content.Children.Add(new Border{Child=thumb,CornerRadius=new CornerRadius(10)});var name=w.Text((asset.IsFavorite?"★ ":"")+asset.Name,16);name.MaxLines=2;name.TextTrimming=TextTrimming.CharacterEllipsis;content.Children.Add(name);string size=asset.ByteLength<1024*1024?$"{asset.ByteLength/1024.0:0.#} KB":$"{asset.ByteLength/1024.0/1024:0.#} MB";content.Children.Add(w.Note($"{asset.Width} × {asset.Height}  ·  {size}"));
                    var item=new GridViewItem{Tag=asset.Id,Content=content,Padding=new Thickness(10)};AutomationProperties.SetName(item,asset.Name+"，"+asset.Width+"乘"+asset.Height+(asset.IsFavorite?"，已收藏":""));gallery.Items.Add(item);if(asset.Id==selectedId)gallery.SelectedItem=item;
                }
                emptyLabel.Text=gallery.Items.Count==0?(assets.Any(a=>!w.wallpaperPresetIds.Contains(a.Id))?"没有匹配的图片，试试清空搜索或切换范围。":"还没有自己的壁纸。拖入图片或点击“导入自己的图片”；也可切换到示例背景。"):$"{gallery.Items.Count} 张图片 · 选择后调整，底部应用";
                libraryWarning.Text=unavailable.Count>0?$"有 {unavailable.Count} 个图片副本不可用，可清理后重新导入。当前启用的壁纸需先关闭并应用。":"";cleanupButton.Visibility=unavailable.Count>0?Visibility.Visible:Visibility.Collapsed;
                RefreshSelection();
            }finally{populating=false;}
        }
        private async Task LoadThumbnailAsync(WallpaperAsset asset,Image image,CancellationToken ct,bool rebuild=false)
        {
            bool acquired=false;
            try{await thumbnailGate.WaitAsync(ct);acquired=true;string path=await Task.Run(()=>library.EnsureThumbnailAsync(asset.Id,ct,rebuild),ct);if(IsCurrent&&!ct.IsCancellationRequested&&image.IsLoaded)image.Source=new BitmapImage{DecodePixelWidth=420,UriSource=new Uri(path),CreateOptions=rebuild?BitmapCreateOptions.IgnoreImageCache:BitmapCreateOptions.None};}
            catch(OperationCanceledException){}catch(Exception error){UiCrashLog.Write("wallpaper.thumbnail",Safe(error.Message));}
            finally{if(acquired)thumbnailGate.Release();}
        }
        private void RefreshSelection()
        {
            var asset=Selected();selectionLabel.Text=asset is not null?asset.Name:selectedId is null?"先导入一张喜欢的图片":"原壁纸图片已缺失，请重新选择或关闭壁纸";
            bool selectedInGallery=GallerySelection() is not null;favoriteButton?.SetValue(Control.IsEnabledProperty,selectedInGallery);renameButton?.SetValue(Control.IsEnabledProperty,selectedInGallery);deleteButton?.SetValue(Control.IsEnabledProperty,selectedInGallery);
            if(favoriteButton is not null)favoriteButton.Content=asset?.IsFavorite==true?"取消收藏":"收藏所选图片";
            draftLabel.Text=w.dirty?"当前为未应用草稿 · 底部应用后自动同步":"设置已保存 · 实际效果见上方 Codex 连接状态";
        }
        public void RefreshStatus()
        {
            if(!IsCurrent)return;var status=CodexWallpaperRuntime.ReadStatus(w.WallpaperRoot);
            bool connected=status.State is "applied" or "disabled" or "preview" or "ready";trialButton.IsEnabled=connected&&status.ProtocolVersion>=2&&Selected() is not null&&w.wallpaperTrialId is null;stopButton.IsEnabled=w.wallpaperTrialId is not null;
            statusLabel.Text=connected&&status.ProtocolVersion<2?"正在使用旧版壁纸组件。请正常退出 Codex，再从桌面“Codex”启动，以应用新构图和试用保护。":
                w.wallpaperTrialId is not null&&status.PreviewLeaseId!=w.wallpaperTrialId?"已请求临时试用 · 等待 Codex 窗口确认":
                connected&&status.Revision!=Revision?"设置已保存 · 等待 Codex 同步。上次状态："+status.Message:status.Message;
            string fingerprint=JsonSerializer.Serialize(new{status.State,status.Version,status.Windows});if(fingerprint==statusFingerprint)return;statusFingerprint=fingerprint;
            w.ClearPanelChildren(windowDetails);windowDetails.Children.Add(w.Note($"组件版本：{(status.Version=="legacy"?"旧版 / 尚未连接":status.Version)}"));
            foreach(var entry in status.Windows)windowDetails.Children.Add(w.Note($"窗口 {entry.Id[..Math.Min(8,entry.Id.Length)]} · {entry.Message}"));
        }
        private async Task PickFilesAsync()
        {
            var picker=new FileOpenPicker();WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(w));foreach(string ext in new[]{".png",".jpg",".jpeg",".webp",".bmp"})picker.FileTypeFilter.Add(ext);
            var files=await picker.PickMultipleFilesAsync();if(IsCurrent)await ImportFilesAsync(files.Select(f=>f.Path));
        }
        public async Task ImportFilesAsync(IEnumerable<string> paths)
        {
            if(!IsCurrent)return;if(w.wallpaperImportCancellation is not null){w.ShowNotice("图片正在导入","可以取消当前导入后再选择其他图片。");return;}
            var files=paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(101).ToList();if(files.Count==0)return;if(files.Count>100)throw new InvalidOperationException("一次最多导入 100 张图片，请分批导入。");
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(w.lifetime.Token);w.wallpaperImportCancellation=cancellation;cancelImport.IsEnabled=true;importProgress.Visibility=Visibility.Visible;var failed=new List<string>();int completed=0;string? firstId=null,initialSelection=selectedId;
            try{
                foreach(string path in files){cancellation.Token.ThrowIfCancellationRequested();importLabel.Text=$"正在导入 {completed+1}/{files.Count} · {Path.GetFileName(path)}";
                    try{var imported=await Task.Run(()=>library.ImportAsync(path,cancellation.Token),cancellation.Token);firstId??=imported.Id;}
                    catch(OperationCanceledException){throw;}catch(Exception error){failed.Add(Path.GetFileName(path)+"："+Safe(error.Message));}
                    completed++;if(IsCurrent)importProgress.Value=completed*100.0/files.Count;
                }
            }catch(OperationCanceledException){if(IsCurrent)importLabel.Text="已取消，已完成的图片仍保留。";}
            finally{
                if(ReferenceEquals(w.wallpaperImportCancellation,cancellation))w.wallpaperImportCancellation=null;
                if(!w.lifetime.IsCancellationRequested){cancelImport.IsEnabled=false;importProgress.Visibility=Visibility.Collapsed;importLabel.Text=$"已处理 {completed}/{files.Count} 张，{failed.Count} 张未能导入。"+(cancellation.IsCancellationRequested?" 已取消剩余导入。":"");}
                if(IsCurrent){await LoadAssetsAsync();if(firstId is not null&&selectedId==initialSelection){selectedId=firstId;enabled.IsOn=true;scope.SelectedIndex=0;PopulateGallery();Changed();}RefreshSelection();
                    if(failed.Count>0)w.ShowNotice("部分图片未能导入",string.Join("\n",failed.Take(5))+(failed.Count>5?$"\n另有 {failed.Count-5} 项，请检查文件后重试。":""),InfoBarSeverity.Warning);
                }
                if(!w.lifetime.IsCancellationRequested)w.RefreshDraftChrome();
            }
        }
        private async Task CleanupUnavailableAsync()
        {
            if(unavailable.Count==0)return;var dialog=new ContentDialog{Title="清理不可用的图片副本？",Content="只清理图片库中已损坏或缺少原图的副本，保留外部原文件。当前正在启用的图片不会被删除。",PrimaryButtonText="确认清理",CloseButtonText="取消",XamlRoot=w.Root.XamlRoot};if(await w.ShowDialogAsync(dialog)!=ContentDialogResult.Primary)return;
            var targets=unavailable.ToArray();string revision=Revision;w.busy=true;w.RefreshDraftChrome();
            try{var result=await Task.Run(()=>{int count=0;foreach(string id in targets){var current=library.LoadSnapshot();if(current.Revision!=revision)throw new InvalidOperationException("设置已被其他操作更新，请重新读取后再清理。");if(current.Settings.Enabled&&current.Settings.ImageId==id)continue;var deleted=library.Delete(id,revision);revision=deleted.Revision;count++;}return(count,library.LoadSnapshot());});
                saved=result.Item2.Settings;Revision=result.Item2.Revision;await LoadAssetsAsync();w.ShowNotice("清理完成",$"已清理 {result.count} 个不可用副本。",InfoBarSeverity.Success);
            }finally{w.busy=false;w.RefreshDraftChrome();}
        }
        private async Task RenameAsync()
        {
            var asset=GallerySelection();if(asset is null)return;var input=w.Input("图片显示名称",asset.Name,track:false);input.MaxLength=100;
            var dialog=new ContentDialog{Title="重命名图片",Content=input,PrimaryButtonText="保存名称",CloseButtonText="取消",XamlRoot=w.Root.XamlRoot};if(await w.ShowDialogAsync(dialog)!=ContentDialogResult.Primary)return;
            string name=input.Text;await Task.Run(()=>library.Rename(asset.Id,name));await LoadAssetsAsync();
        }
        private async Task DeleteAsync()
        {
            var asset=GallerySelection();if(asset is null)return;
            var dialog=new ContentDialog{Title="删除所选图片副本？",Content=asset.Name+"\n只删除本地图片库中的副本。正在应用的壁纸需先关闭或换图并应用。",PrimaryButtonText="删除副本",CloseButtonText="取消",XamlRoot=w.Root.XamlRoot};if(await w.ShowDialogAsync(dialog)!=ContentDialogResult.Primary)return;
            await w.StopWallpaperTrialAsync();w.busy=true;w.RefreshDraftChrome();try{var result=await Task.Run(()=>library.Delete(asset.Id,Revision));saved=result.Settings;Revision=result.Revision;selectedId=null;await LoadAssetsAsync();Changed();}finally{w.busy=false;w.RefreshDraftChrome();}
        }
        private async Task<bool> SaveAsync()
        {
            var settings=Current();var result=await Task.Run(()=>library.SaveSnapshot(settings,Revision));saved=result.Settings;Revision=result.Revision;await w.StopWallpaperTrialAsync();
            if(!w.IsDiagnosticRun)try{w.StartWallpaperHost(false);}catch(Exception error){w.ShowNotice("壁纸已应用到配置","连接组件尚未启动："+Safe(error.Message),InfoBarSeverity.Warning);}
            RefreshStatus();return true;
        }
        private async Task RestorePreviousAsync()
        {
            var dialog=new ContentDialog{Title="恢复上一次应用的壁纸？",Content="将替换当前草稿并恢复上次应用设置。当前配置保留为恢复前副本，图片库不受影响。",PrimaryButtonText="恢复并应用",CloseButtonText="取消",XamlRoot=w.Root.XamlRoot};if(await w.ShowDialogAsync(dialog)!=ContentDialogResult.Primary)return;
            await w.StopWallpaperTrialAsync();w.busy=true;w.RefreshDraftChrome();try{await Task.Run(()=>library.RecoverSettings(true,Revision));w.dirty=false;w.drafts.Remove("wallpaper");}finally{w.busy=false;w.RefreshDraftChrome();}
            w.Render();if(!w.IsDiagnosticRun)try{w.StartWallpaperHost(false);}catch(Exception error){w.ShowNotice("上次设置已恢复","连接组件暂未启动："+Safe(error.Message),InfoBarSeverity.Warning);}
        }
        private async Task LocateCodexAsync()
        {
            var picker=new FileOpenPicker();WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(w));picker.FileTypeFilter.Add(".exe");var file=await picker.PickSingleFileAsync();if(file is null||!IsCurrent)return;CodexWallpaperRuntime.RememberExecutable(w.WallpaperRoot,file.Path);w.ShowNotice("已找到 Codex","正常退出后可通过壁纸入口打开。",InfoBarSeverity.Success);
        }
        private async Task ExportDiagnosticsAsync()
        {
            var runtime=CodexWallpaperRuntime.ReadStatus(w.WallpaperRoot);var session=CodexWallpaperRuntime.ReadSession(w.WallpaperRoot);var selected=Selected();string path=Path.Combine(w.stateHome,"exports","wallpaper-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{version="0.9.0",time=DateTimeOffset.UtcNow,runtime,processAlive=session is not null&&CodexWallpaperRuntime.IsSessionProcessAlive(session),endpointOwned=session is not null&&CodexWallpaperRuntime.OwnsLoopbackEndpoint(session),applied=saved,draft=Current(),image=selected is null?null:new{selected.Name,selected.Width,selected.Height,selected.ByteLength},libraryCount=assets.Count},new JsonSerializerOptions{WriteIndented=true}));if(IsCurrent)w.ShowNotice("已导出壁纸诊断",path,InfoBarSeverity.Success);
        }
        public void Suspend(){suspended=true;previewCancellation?.Cancel();thumbnailCancellation?.Cancel();}
        public void Resume(){suspended=false;RefreshStatus();RefreshSelection();if(w.wallpaperImportCancellation is null){cancelImport.IsEnabled=false;importProgress.Visibility=Visibility.Collapsed;}_ = LoadAssetsAsync();SchedulePreview();}
    }
}
