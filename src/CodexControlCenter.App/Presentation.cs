using CodexControlCenter.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexControlCenter.App;

/// 外壳与页面通用组件：布局自适应、状态色、卡片与状态标签。
public sealed partial class MainWindow
{
    private readonly Dictionary<string,SolidColorBrush> toneBrushes=[];
    private string operationPhase="";
    private int loadingCount;
    private DateTimeOffset? lastSaveAt;
    private IReadOnlyList<string> lastSaveEffects=[];
    private string? lastSaveBackup;
    private bool pageReadFailed;

    private static string CanonicalPageKey(string key)=>key switch{"global" or "skills"=>"documents",_=>key};
    private static string PageName(string key)=>CanonicalPageKey(key) switch{
        "providers"=>"模型接入","models"=>"模型列表","agents"=>"子代理","images"=>"生图接口",
        "documents"=>"技能与指令","wallpaper"=>"壁纸","settings"=>"设置与备份","requests"=>"请求记录","help"=>"使用指南",_=>"概览"};

    private void OnShellSizeChanged(object sender,SizeChangedEventArgs args)
        =>UpdateShellLayout();
    private void UpdateShellLayout()
    {
        if(WorkspaceArea is null||FooterGrid is null)return;
        bool compact=Root.ActualWidth<1100;
        bool minimal=Navigation.DisplayMode==NavigationViewDisplayMode.Minimal;
        WorkspaceArea.Padding=new Thickness(compact?20:32,minimal?56:16,compact?20:32,16);
        bool stacked=WorkspaceArea.ActualWidth<960;
        Grid.SetRow(HeaderActions,stacked?1:0);Grid.SetColumn(HeaderActions,stacked?0:1);
        Grid.SetColumnSpan(PageHeading,stacked?2:1);Grid.SetColumnSpan(HeaderActions,stacked?2:1);
        bool footerStacked=Root.ActualWidth<1080;
        Grid.SetRow(FooterActions,footerStacked?1:0);Grid.SetColumn(FooterActions,footerStacked?0:1);
        Grid.SetColumnSpan(FooterCopy,footerStacked?2:1);Grid.SetColumnSpan(FooterActions,footerStacked?2:1);
        TitleBrand.Visibility=Root.ActualWidth<720?Visibility.Collapsed:Visibility.Visible;
    }
    private void RefreshOperationChrome()
    {
        bool processing=busy||loadingCount>0||imageOperationRunning;
        OperationRing.IsActive=processing;OperationRing.Visibility=processing?Visibility.Visible:Visibility.Collapsed;
        OperationDot.Visibility=processing?Visibility.Collapsed:Visibility.Visible;
        OperationDot.Fill=dirty?ToneBrush("warn"):snapshot is null?ToneBrush("neutral"):ToneBrush("ok");
        RefreshButton.IsEnabled=!busy&&loadingCount==0&&!dialogActive;
        QuickSearchButton.IsEnabled=!busy&&snapshot is not null&&!dialogActive;
        DraftsButton.IsEnabled=DraftCount>0&&!busy&&!dialogActive;
        DraftsButton.Content=DraftCount>0?$"草稿清单 · {DraftCount}":"草稿清单";
        OperationLabel.Text=busy?operationPhase switch{"review"=>"请确认本页变更","write"=>"正在备份并保存",_=>"正在检查本页配置"}:
            loadingCount>0?"正在读取最新配置":pageReadFailed?"本页需要处理读取问题":dirty?$"{PageName(page)} · 草稿未保存":
            imageOperationRunning?"图片任务进行中":snapshot is null?"尚未连接配置目录":$"{PageName(page)} · 没有未保存的修改";
        WorkflowPanel.Visibility=page!="wallpaper"&&(planFactory is not null||localSave is not null)&&!pageReadFailed?Visibility.Visible:Visibility.Collapsed;
        EditStep.Text=dirty?"草稿待保存":"编辑配置";
        ReviewStep.Text=operationPhase=="review"?"正在确认变更":localSave is not null?"校验设置":"预览确认";
        WriteStep.Text=operationPhase=="write"?"正在备份保存":localSave is not null?"保存设置":"备份保存";
        int step=operationPhase=="write"?3:operationPhase=="review"||operationPhase=="check"?2:1;
        StepVisual(EditBadgeRing,EditStep,step==1);
        StepVisual(ReviewBadgeRing,ReviewStep,step==2);
        StepVisual(WriteBadgeRing,WriteStep,step==3);
        PageEyebrow.Text=EyebrowFor(page);
        ToolTipService.SetToolTip(ApplyButton,page=="documents"?"同时预览并保存全局与项目指令、技能、角色三类的全部草稿。Ctrl+S":dirty?"只保存当前页面；其他页面草稿保留。Ctrl+S":"先编辑本页配置，再预览并保存。");
    }
    private void RecordSave(IEnumerable<string> effects,string? backup=null)
    {
        lastSaveAt=DateTimeOffset.Now;lastSaveEffects=effects.Select(Safe).Distinct().ToArray();lastSaveBackup=backup;
    }
    private async void OnOpenDrafts(object sender,RoutedEventArgs args)
    {
        if(busy||dialogActive)return;
        var (dialog,options)=BuildDraftDialog();
        if(await ShowDialogAsync(dialog)==ContentDialogResult.Primary&&options.SelectedItem is ListViewItem{Tag:string selectedKey})Navigate(selectedKey);
    }
    private (ContentDialog,ListView) BuildDraftDialog()
    {
        var targets=DraftTargets();var list=Section();list.Children.Add(Note("草稿仅保留在本次运行中。选择页面继续编辑，每页单独预览保存。"));
        var options=new ListView{SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        foreach(string key in targets)options.Items.Add(new ListViewItem{Tag=key,Content=Row(PageName(key),key==page?"当前页面":"点击返回此页继续编辑","未保存","warn")});
        if(targets.Count>0)options.SelectedIndex=0;list.Children.Add(options);
        var dialog=new ContentDialog{Title=$"{targets.Count} 页草稿尚未保存",Content=new ScrollViewer{Content=list,MaxHeight=480},PrimaryButtonText="前往所选页面",IsPrimaryButtonEnabled=targets.Count>0,CloseButtonText="继续当前页",XamlRoot=Root.XamlRoot};
        return (dialog,options);
    }
    private List<string> DraftTargets()=>Navigation.MenuItems.OfType<NavigationViewItem>().Select(n=>n.Tag?.ToString()??"")
        .Where(key=>key==page?dirty:drafts.TryGetValue(key,out var draft)&&draft.HasChanges).ToList();

    /// 状态色：浅色与深色主题分别取值，保证状态标签在两种主题下都可读。
    private Color ToneColor(string tone)
    {
        bool dark=Root.ActualTheme==ElementTheme.Dark;
        return tone switch{
            "ok"=>dark?Color.FromArgb(255,116,208,155):Color.FromArgb(255,31,115,70),
            "bad"=>dark?Color.FromArgb(255,245,151,151):Color.FromArgb(255,176,48,48),
            "warn"=>dark?Color.FromArgb(255,235,197,117):Color.FromArgb(255,137,91,15),
            _=>dark?Color.FromArgb(255,205,205,205):Color.FromArgb(255,91,91,91)};
    }
    private Brush ToneBrush(string tone,bool background=false)
    {
        var color=ToneColor(tone);
        if(background)color.A=Root.ActualTheme==ElementTheme.Dark?(byte)28:(byte)18;
        string key=tone+(background?":background":":text");
        if(!toneBrushes.TryGetValue(key,out var brush))toneBrushes[key]=brush=new SolidColorBrush(color);
        brush.Color=color;return brush;
    }
    private Brush ToneBorderBrush(string tone)
    {
        var color=ToneColor(tone);
        color.A=Root.ActualTheme==ElementTheme.Dark?(byte)88:(byte)56;
        string key=tone+":border";
        if(!toneBrushes.TryGetValue(key,out var brush))toneBrushes[key]=brush=new SolidColorBrush(color);
        brush.Color=color;return brush;
    }
    private void RefreshThemePalette(){
        foreach(string key in toneBrushes.Keys.ToArray()){
            string[] parts=key.Split(':');if(parts[1]=="border")ToneBorderBrush(parts[0]);else ToneBrush(parts[0],parts[1]=="background");
        }
    }

    /// 状态标签：圆点 + 文案 + 淡色底与描边。
    private Border Pill(string label,string tone="neutral")
    {
        var dot=new Border{Width=8,Height=8,CornerRadius=new CornerRadius(4),Background=ToneBrush(tone),VerticalAlignment=VerticalAlignment.Center};
        var text=new TextBlock{Text=label,FontSize=14,TextWrapping=TextWrapping.Wrap,Foreground=ToneBrush(tone),MaxWidth=320,VerticalAlignment=VerticalAlignment.Center};
        var row=new Grid{ColumnSpacing=8};row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        row.Children.Add(dot);Grid.SetColumn(text,1);row.Children.Add(text);
        return new Border{Child=row,Padding=new Thickness(12,5,12,6),CornerRadius=new CornerRadius(999),Background=ToneBrush(tone,true),BorderBrush=ToneBorderBrush(tone),BorderThickness=new Thickness(1),HorizontalAlignment=HorizontalAlignment.Left};
    }

    /// 卡片网格：列数随宽度变化，并尽量保持每行项目数量均衡。
    private Grid TileGrid(params FrameworkElement[] tiles)
        =>TileGrid(330,tiles);
    private Grid TileGrid(double minimumWidth,params FrameworkElement[] tiles)
        =>EvenGrid(minimumWidth,int.MaxValue,tiles);

    /// 指标卡：标签 + 数值 + 状态标签，可选跳转入口。
    private Border Metric(string title,string value,string description,string tone="neutral",string? target=null)
    {
        var content=Section();content.Spacing=8;
        content.Children.Add(new TextBlock{Text=title,FontSize=15,Foreground=SecondaryBrush(),TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new TextBlock{Text=value,FontSize=30,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        content.Children.Add(Pill(description,tone));
        if(target is not null)content.Children.Add(QuietAction("查看"+PageName(target),()=>Navigate(target)));
        return Surface(content);
    }

    /// 空状态：标题 + 说明 + 可选操作。
    private Border EmptyState(string title,string description,string? action=null,Action? handler=null)
    {
        var panel=Section(title);panel.Spacing=8;
        panel.Children.Add(new TextBlock{Text=description,FontSize=16,Foreground=SecondaryBrush(),TextWrapping=TextWrapping.Wrap});
        if(action is not null&&handler is not null)panel.Children.Add(QuietAction(action,handler));
        return Surface(panel);
    }
}
