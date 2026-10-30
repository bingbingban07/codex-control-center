using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexControlCenter.App;

/// 概览页与外壳共用的界面组件：颜色基线、卡片、状态标签、入口磁贴与响应式分栏。
public sealed partial class MainWindow
{
    private SolidColorBrush? accentBrush;

    /// 当前强调色。配置文件里的 accent 会覆盖默认色，这里始终读取最新值。
    private Color AccentColor()
    {
        if(Root.Resources.TryGetValue("AccentFillColorDefaultBrush",out var value)&&value is SolidColorBrush brush)return brush.Color;
        return Color.FromArgb(255,0,120,212);
    }
    private SolidColorBrush AccentBrush()
    {
        var color=AccentColor();
        if(accentBrush is null)accentBrush=new SolidColorBrush(color);else accentBrush.Color=color;
        return accentBrush;
    }
    private static SolidColorBrush Tint(Color color,byte alpha)=>new(Color.FromArgb(alpha,color.R,color.G,color.B));

    /// 主题相关的文字颜色直接取自已经在界面中的元素，避免自行判断浅色或深色主题。
    private Brush SecondaryBrush()=>PageSubtitle.Foreground;
    private Brush PrimaryBrush()=>PageTitle.Foreground;
    private Brush TertiaryBrush()=>PageEyebrow.Foreground;

    /// 统一的卡片外观：与其它页面共用同一套描边、圆角与内边距。
    private Border Surface(UIElement content,Thickness? padding=null)
        =>new(){Child=content,Padding=padding??new Thickness(24,20,24,22),BorderThickness=new Thickness(1),Style=(Style)Root.Resources["PanelCardStyle"],CornerRadius=new CornerRadius(12)};

    /// 区块标题：路径标签 + 标题 + 一句说明。
    private StackPanel SectionHeader(string title,string? description=null,string? eyebrow=null)
    {
        var panel=new StackPanel{Spacing=6};
        if(!string.IsNullOrWhiteSpace(eyebrow))panel.Children.Add(new TextBlock{Text=eyebrow,FontSize=13,Foreground=TertiaryBrush(),CharacterSpacing=60});
        panel.Children.Add(new TextBlock{Text=title,FontSize=22,FontWeight=FontWeights.SemiBold,Foreground=PrimaryBrush(),TextWrapping=TextWrapping.Wrap});
        if(!string.IsNullOrWhiteSpace(description))panel.Children.Add(new TextBlock{Text=description,FontSize=16,Foreground=SecondaryBrush(),TextWrapping=TextWrapping.Wrap});
        return panel;
    }

    /// 次级操作：安静的强调色文字按钮，用于卡片内部跳转。
    private Button QuietAction(string label,Action handler)
    {
        var button=new Button{Content=label,FontSize=bodySize,MinHeight=44,Padding=new Thickness(2,6,2,6),Background=new SolidColorBrush(Color.FromArgb(0,0,0,0)),Foreground=AccentBrush(),BorderThickness=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Left,HorizontalContentAlignment=HorizontalAlignment.Left};
        button.Click+=(_,_)=>{try{handler();}catch(Exception e){ShowError("操作失败",e);}};
        return button;
    }

    /// 响应式分栏：列数随可用宽度变化，并优先选择能整除项目数的列数，避免最后一行只剩一个孤项。
    private Grid EvenGrid(double minimumWidth,int maxColumns,params FrameworkElement[] items)
    {
        var grid=new Grid{ColumnSpacing=18,RowSpacing=18};
        int columns=-1;
        void Arrange(double width)
        {
            int fit=Math.Max(1,Math.Min(items.Length,(int)((Math.Max(0,width)+18)/minimumWidth)));
            if(maxColumns>0)fit=Math.Min(fit,maxColumns);
            int next=1;
            for(int c=items.Length;c>=2;c--)if(items.Length%c==0&&c<=fit){next=c;break;}
            if(next==1&&fit>1)next=fit;
            if(columns==next)return;
            columns=next;
            ClearGridDefinitions(grid);
            for(int i=0;i<columns;i++)grid.ColumnDefinitions.Add(new());
            for(int i=0;i<(items.Length+columns-1)/columns;i++)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            for(int i=0;i<items.Length;i++){Grid.SetColumn(items[i],i%columns);Grid.SetRow(items[i],i/columns);}
        }
        foreach(var item in items)grid.Children.Add(item);
        Arrange(1000);
        grid.SizeChanged+=(_,e)=>Arrange(e.NewSize.Width);
        return grid;
    }

    /// 流程步骤的当前状态：描边高亮 + 强调色文字。
    private void StepVisual(Border ring,TextBlock label,bool active)
    {
        ring.BorderBrush=AccentBrush();
        ring.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        label.FontWeight=active?FontWeights.SemiBold:FontWeights.Normal;
        label.Foreground=active?(Brush)AccentBrush():SecondaryBrush();
    }
}
