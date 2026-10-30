using CodexControlCenter.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexControlCenter.App;

/// 模型接入页的展示组件：来源磁贴、密钥引用状态、接入步骤与空状态引导。
public sealed partial class MainWindow
{
    /// 来源磁贴：显示名称、代号、认证方式、关联模型数与密钥引用状态；点击即切换到该来源。
    private Button ProviderTile(ProviderEntry provider,int modelCount,bool selected,Action activate)
    {
        var body=new StackPanel{Spacing=8};
        body.Children.Add(new TextBlock{Text=ProviderName(provider),FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        var badges=EvenGrid(150,2,Pill(provider.AuthMode=="chatgpt"?"ChatGPT 登录":"独立 API Key"),
            Pill(modelCount>0?$"{modelCount} 个模型指向":"暂无模型指向",modelCount>0?"neutral":"warn"));
        body.Children.Add(badges);
        var address=Note(provider.BaseUrl.Length>0?provider.BaseUrl:"尚未填写接口地址");
        address.MaxWidth=520;
        body.Children.Add(address);
        body.Children.Add(Note("代号 "+provider.Id+" · "+(provider.CredentialStatus=="引用已配置"?"密钥引用已配置":"密钥引用未配置")));
        var card=new Border{Child=body,Style=(Style)Root.Resources["PanelCardStyle"],BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(16,14,16,14),VerticalAlignment=VerticalAlignment.Top};
        if(selected){card.BorderBrush=AccentBrush();card.Background=Tint(AccentColor(),22);}
        var tile=new Button{Content=card,Background=new SolidColorBrush(Color.FromArgb(0,0,0,0)),BorderThickness=new Thickness(0),Padding=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,MinHeight=136};
        tile.Click+=(_,_)=>activate();
        AutomationProperties.SetAutomationId(tile,"provider-tile-"+provider.Id);
        AutomationProperties.SetName(tile,$"{ProviderName(provider)}，代号 {provider.Id}，{(provider.AuthMode=="chatgpt"?"ChatGPT 登录":"独立 API Key")}，关联 {modelCount} 个模型");
        AutomationProperties.SetHelpText(tile,"切换到该来源，载入它的接口地址、认证方式与密钥引用设置。");
        return tile;
    }

    /// 新增来源磁贴：始终可见，方便从任意来源直接进入新增流程。
    private Button NewProviderTile(Action activate)
    {
        var body=new StackPanel{Spacing=8};
        body.Children.Add(new TextBlock{Text="＋ 新增独立 API 接入",FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        body.Children.Add(Note("填写服务商地址与密钥，保存后即可在模型列表里选择模型。"));
        var card=new Border{Child=body,Style=(Style)Root.Resources["PanelCardStyle"],BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(16,14,16,14),BorderBrush=Tint(AccentColor(),120),VerticalAlignment=VerticalAlignment.Top};
        var tile=new Button{Content=card,Background=new SolidColorBrush(Color.FromArgb(0,0,0,0)),BorderThickness=new Thickness(0),Padding=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,MinHeight=136};
        tile.Click+=(_,_)=>activate();
        AutomationProperties.SetAutomationId(tile,"provider-tile-add");
        AutomationProperties.SetName(tile,"新增独立 API 接入");
        AutomationProperties.SetHelpText(tile,"切换到新增表单，填写接口地址与密钥。");
        return tile;
    }

    /// 密钥引用状态：只报告引用与文件是否存在，不读取密钥内容，也不声称密钥可用。
    private StackPanel CredentialReferenceStatus(string codexHome,ProviderEntry provider,bool existing,string keyEnvironment,bool chatgpt)
    {
        var panel=new StackPanel{Spacing=10};
        if(chatgpt)
        {
            panel.Children.Add(Pill("使用 Codex 已登录会话","neutral"));
            panel.Children.Add(Note("此来源不保存独立 API Key。请先在 Codex 完成 ChatGPT 登录，再回到这里核对运行配置是否生效。"));
            return panel;
        }
        string env=keyEnvironment.Length>0?keyEnvironment:provider.KeyEnvironment;
        bool fileExists=existing&&provider.Id.Length>0&&File.Exists(Path.Combine(codexHome,"secrets","control-center-"+provider.Id+".dpapi"));
        bool envExists=env.Length>0&&EnvironmentKeyExists(env);
        var badges=EvenGrid(320,2,Pill(fileExists?"加密凭据文件已存在":"尚无加密凭据文件",fileExists?"neutral":"warn"),
            Pill(env.Length==0?"密钥引用名待生成":envExists?$"环境变量 {env} 已存在":$"环境变量 {env} 未找到",envExists?"neutral":"warn"));
        panel.Children.Add(badges);
        panel.Children.Add(Note(fileExists
            ?"此来源已有加密凭据文件：密钥框留空表示保留原密钥。管理台不会读取密钥内容，也无法判断它是否可用。"
            :"尚未保存加密凭据。填写密钥并保存后会写入当前 Windows 账户的加密文件；也可以在高级设置中引用已经存在的环境变量。"));
        return panel;
    }

    /// 接入步骤：编号 + 标题 + 说明，用于首次接入与空状态引导。
    private Border ProviderStep(int index,string title,string detail)
    {
        var badge=new Border{Child=new TextBlock{Text=index.ToString(),FontSize=14,FontWeight=FontWeights.SemiBold,Foreground=AccentBrush(),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center},Width=30,Height=30,CornerRadius=new CornerRadius(9),Background=Tint(AccentColor(),24),BorderBrush=Tint(AccentColor(),80),BorderThickness=new Thickness(1),VerticalAlignment=VerticalAlignment.Top};
        var text=new StackPanel{Spacing=4};
        text.Children.Add(new TextBlock{Text=title,FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        text.Children.Add(Note(detail));
        var row=new Grid{ColumnSpacing=14};
        row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        row.Children.Add(badge);Grid.SetColumn(text,1);row.Children.Add(text);
        return new Border{Child=row,Padding=new Thickness(0,4,0,4)};
    }
}
