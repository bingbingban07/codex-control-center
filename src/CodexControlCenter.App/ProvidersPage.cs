using CodexControlCenter.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;

/// 模型接入页：来源概览与选择、常用设置分组、连接检查与本地转发面板。
public sealed partial class MainWindow
{
    private Func<Task>? verifyProviderFlow;
    private void RenderProviders()
    {
        var s=snapshot!;string Label(ProviderEntry p)=>ProviderName(p)+" ["+p.Id+"]";
        const string add="＋ 新增独立 API 接入";
        TextBox? nameBox=null;

        Add(SectionHeader("模型接入","把模型服务商、登录方式和密钥引用集中在一页。改动先进入本页草稿，预览确认后才会备份保存。"));
        int chatgptCount=s.Providers.Count(p=>p.AuthMode=="chatgpt");
        int keyCount=s.Providers.Count-chatgptCount;
        int linkedModels=s.Models.Count(m=>s.Providers.Any(p=>p.Id==m.Provider));
        int idleProviders=s.Providers.Count(p=>!s.Models.Any(m=>m.Provider==p.Id));
        Add(EvenGrid(240,4,
            Metric("接入来源",s.Providers.Count.ToString(),s.Providers.Count>0?"已配置来源":"尚未配置来源",s.Providers.Count>0?"ok":"warn"),
            Metric("独立 API Key",keyCount.ToString(),"凭据保存在本机加密文件"),
            Metric("ChatGPT 登录",chatgptCount.ToString(),"复用 Codex 已登录会话"),
            Metric("已关联模型",linkedModels.ToString(),s.Providers.Count==0?"等待接入来源":idleProviders>0?$"{idleProviders} 个来源暂无模型":"每个来源都有模型指向",linkedModels>0?"ok":"warn")));

        var choose=Choice("选择接入来源",s.Providers.Select(Label).Append(add),s.Providers.Count>0?Label(s.Providers[0]):add,false);
        AutomationProperties.SetName(choose,"选择接入来源：可在已有来源之间切换，或新增独立 API 接入");
        var tiles=new StackPanel{Spacing=14};
        var picker=Section("接入来源");picker.Spacing=14;
        picker.Children.Add(choose);picker.Children.Add(tiles);Add(Card(picker));

        var editor=Section();editor.Spacing=18;Add(Card(editor));

        void RenderTiles()
        {
            ClearPanelChildren(tiles);string current=choose.SelectedItem?.ToString()??add;
            if(s.Providers.Count==0)
            {
                var guide=Section("还没有接入任何来源");guide.Spacing=10;
                guide.Children.Add(Note("完成下面三步就能在 Codex 中调用外部模型。接入配置本身不会发起任何付费请求。"));
                guide.Children.Add(ProviderStep(1,"填写接口地址","服务商提供的 Base URL，例如 https://服务商地址/v1"));
                guide.Children.Add(ProviderStep(2,"填写 API Key","密钥只写入当前 Windows 账户的加密文件，管理台不显示明文"));
                guide.Children.Add(ProviderStep(3,"检查连接并保存","底部“预览并保存”会先展示改动，再备份写入配置文件"));
                guide.Children.Add(Action("开始新增接入",()=>nameBox?.Focus(FocusState.Programmatic)));
                tiles.Children.Add(Card(guide));
            }
            var items=new List<FrameworkElement>();
            foreach(var provider in s.Providers)
            {
                int count=s.Models.Count(m=>m.Provider==provider.Id);
                items.Add(ProviderTile(provider,count,Label(provider)==current,()=>choose.SelectedItem=Label(provider)));
            }
            items.Add(NewProviderTile(()=>choose.SelectedItem=add));
            tiles.Children.Add(EvenGrid(260,int.MaxValue,items.ToArray()));
        }

        void SelectProvider()
        {
            ClearPanelChildren(editor);var original=s.Providers.FirstOrDefault(p=>Label(p)==choose.SelectedItem?.ToString());bool existing=original is not null;var p=original??new ProviderEntry();
            int number=1;while(s.Providers.Any(x=>x.Id=="api-"+number))number++;

            editor.Children.Add(new TextBlock{Text=existing?ProviderName(p):"新增接入来源",FontSize=24,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            var badges=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
            badges.Children.Add(Pill(existing?"已存在的来源":"尚未保存",existing?"neutral":"warn"));
            badges.Children.Add(Pill(p.AuthMode=="chatgpt"?"ChatGPT 登录":"独立 API Key"));
            badges.Children.Add(Pill(existing?$"{s.Models.Count(m=>m.Provider==p.Id)} 个模型指向此来源":"保存后可在模型列表选用"));
            editor.Children.Add(badges);

            if(!existing)
            {
                var guide=Section("首次接入：三步完成");guide.Spacing=6;
                guide.Children.Add(ProviderStep(1,"填写接口地址与名称","名称只用于在管理台中识别；真正的调用地址以服务商地址为准"));
                guide.Children.Add(ProviderStep(2,"填写 API Key","保存时写入本机加密文件；管理台不显示也不验证密钥"));
                guide.Children.Add(ProviderStep(3,"检查连接后保存","连接检查不发送密钥，只确认接口是否响应"));
                editor.Children.Add(guide);
            }

            var basics=Section("基本信息");basics.Spacing=12;
            var name=Input("显示名称",existing?ProviderName(p):"");name.PlaceholderText="例如：我的模型服务";nameBox=name;
            AutomationProperties.SetName(name,"来源显示名称，只用于在管理台中识别");
            var url=Input("接口地址（Base URL）",p.BaseUrl);url.PlaceholderText="例如：https://服务商地址/v1";
            AutomationProperties.SetName(url,"接口地址（Base URL），需要完整的 http 或 https 地址");
            basics.Children.Add(EvenGrid(360,2,name,url));
            var addressHint=Note("");basics.Children.Add(addressHint);
            editor.Children.Add(basics);

            var security=Section("认证与密钥");security.Spacing=12;
            var auth=Choice("认证方式",["独立 API Key","已有 ChatGPT 登录"],p.AuthMode=="chatgpt"?"已有 ChatGPT 登录":"独立 API Key");
            AutomationProperties.SetName(auth,"认证方式：独立 API Key 或已有 ChatGPT 登录");
            var key=new PasswordBox{Header=existing?"更换 API Key（留空不更换）":"API Key（由服务商提供）",FontSize=bodySize,MinHeight=52};var keyOwner=pageInstance;
            key.RegisterPropertyChangedCallback(PasswordBox.PasswordProperty,(_,_)=>{if(ReferenceEquals(keyOwner,pageInstance))MarkDirty();});
            AutomationProperties.SetName(key,"API Key 输入框；留空表示保留已保存的密钥");
            var authNote=Note("");security.Children.Add(auth);security.Children.Add(key);security.Children.Add(authNote);
            var credentialHost=new StackPanel{Spacing=10};security.Children.Add(credentialHost);
            editor.Children.Add(security);

            var advanced=Section();
            var id=Input("接入代号（自动填写，可自定义）",existing?p.Id:"api-"+number);id.IsReadOnly=existing;
            var env=Input("密钥环境变量名（自动填写）",p.KeyEnvironment);
            var profile=Choice("请求适配",["标准 OpenAI 兼容","MiMo 兼容"],p.RequestProfile=="mimo"?"MiMo 兼容":"标准 OpenAI 兼容");
            var proxy=Input("代理地址（仅 ChatGPT 来源）",p.ProxyUrl);
            foreach(var c in new UIElement[]{id,env,profile,proxy})advanced.Children.Add(c);
            var proxyNote=Note("");advanced.Children.Add(proxyNote);
            advanced.Children.Add(Note("接入代号用于连接模型，已有来源的代号不能直接改名。使用独立 API Key 时，环境变量名可留空自动生成。"));
            editor.Children.Add(new Expander{Header="高级设置（通常无需修改）",Content=advanced,HorizontalAlignment=HorizontalAlignment.Stretch});

            var connection=Section("连接检查");connection.Spacing=10;
            var connectionState=Text("尚未检查",16);connectionState.Foreground=ToneBrush("neutral");connection.Children.Add(connectionState);
            connection.Children.Add(Note("只发送不带凭据的 GET 请求，用来确认接口是否响应；不会验证密钥，也不会发起模型调用。"));
            var connectionResult=Text("尚未检查 · 填写地址后可手动检查",16);connection.Children.Add(connectionResult);
            long endpointRevision=0;
            void EndpointChanged(){endpointRevision++;bool valid=IsValidEndpoint(url.Text.Trim());addressHint.Text=valid?"地址格式有效。连接检查只验证接口是否响应。":"请填写完整 http:// 或 https:// 地址，不含密钥、查询参数或片段。";addressHint.Foreground=ToneBrush(valid?"neutral":"warn");connectionResult.Text="地址已修改，请重新检查；之前结果不适用于新地址。";connectionState.Text="地址已修改";connectionState.Foreground=ToneBrush("warn");}
            url.TextChanged+=(_,_)=>EndpointChanged();EndpointChanged();connectionResult.Text="尚未检查 · 填写地址后可手动检查";connectionState.Text="尚未检查";connectionState.Foreground=ToneBrush("neutral");
            connection.Children.Add(AsyncAction("检查接口是否可达（不发送密钥）",async()=>{
                var captured=Draft();long revision=endpointRevision;
                if(!IsValidEndpoint(captured.BaseUrl)){connectionResult.Text="地址格式不正确，请先修正上方接口地址。";connectionResult.Foreground=ToneBrush("warn");connectionState.Text="地址无效";connectionState.Foreground=ToneBrush("warn");return;}
                connectionResult.Text="正在检查接口，请稍候…";connectionState.Text="检查中";connectionState.Foreground=ToneBrush("neutral");
                var h=await runtime.CheckProviderAsync(captured,lifetime.Token);
                if(revision!=endpointRevision)return;
                connectionResult.Text=$"{DateTime.Now:HH:mm:ss} · {h.Status}\n{h.Detail}";connectionResult.Foreground=ToneBrush(h.Online?"neutral":"warn");
                connectionState.Text=h.Status;connectionState.Foreground=ToneBrush(h.Online?"ok":"warn");
            }));
            editor.Children.Add(Card(connection));
            editor.Children.Add(Note("保存后会单独核对运行配置。连接可达、文件已保存和模型调用成功是三个状态；实际调用请查看请求记录。"));

            ProviderEntry Draft()=>new(){Id=id.Text.Trim(),Name=string.IsNullOrWhiteSpace(name.Text)?id.Text.Trim():name.Text.Trim(),BaseUrl=url.Text.Trim(),AuthMode=auth.SelectedIndex==1?"chatgpt":"api_key",KeyEnvironment=string.IsNullOrWhiteSpace(env.Text)?"CODEX_CC_"+id.Text.Trim().ToUpperInvariant().Replace('-','_'):env.Text.Trim(),RequestProfile=profile.SelectedIndex==1?"mimo":"",ProxyUrl=proxy.Text.Trim()};
            planFactory=()=>{
                var d=Draft();var plan=configuration.PlanProvider(s,d);
                if(!existing&&d.AuthMode=="api_key"&&key.Password.Length==0&&!EnvironmentKeyExists(d.KeyEnvironment))plan.Errors.Add("新接入尚未配置密钥：请填写 API Key，或在高级设置中选择已存在的密钥环境变量。");
                if(d.AuthMode=="api_key"&&System.Text.RegularExpressions.Regex.IsMatch(d.Id,"^[A-Za-z0-9_-]{1,64}$"))AddCredential(plan,Path.Combine(s.CodexHome,"secrets","control-center-"+d.Id+".dpapi"),key.Password);
                return plan;
            };

            void UpdateCredential(){ClearPanelChildren(credentialHost);credentialHost.Children.Add(CredentialReferenceStatus(s.CodexHome,p,existing,env.Text.Trim(),auth.SelectedIndex==1));}
            void ShowAuth()
            {
                bool login=auth.SelectedIndex==1;
                key.Visibility=login?Visibility.Collapsed:Visibility.Visible;
                authNote.Text=login?"此来源使用 Codex 中已有的 ChatGPT 登录；独立 API Key 不会随此登录方式保存。":"密钥仅保存到当前 Windows 账户的加密文件；留空会保留原密钥。";
                proxyNote.Text=login?"代理只对 ChatGPT 登录来源生效，且不能带路径或查询参数。":"当前使用独立 API Key，代理地址不会生效；需要代理请改回 ChatGPT 登录。";
                UpdateCredential();
            }
            auth.SelectionChanged+=(_,_)=>ShowAuth();env.TextChanged+=(_,_)=>UpdateCredential();

            verifyProviderFlow=async()=>{
                if(!existing)return;var before=Draft();auth.SelectedIndex=p.AuthMode=="chatgpt"?1:0;await Task.Delay(50);if(Draft().AuthMode!=before.AuthMode)throw new InvalidOperationException("认证方式显示值没有正确映射");
                url.Text="invalid-address";await Task.Delay(50);var plan=planFactory();if(plan.Errors.Count==0)throw new InvalidOperationException("无效接口地址未被阻止");if(plan.Errors.Any(e=>UserFeedback.TranslateValidation(e).Contains("Provider base_url")))throw new InvalidOperationException("接口校验没有转换为操作提示");dirty=false;
            };

            ShowAuth();RenderTiles();
        }

        object? previous=choose.SelectedItem;bool reverting=false;
        choose.SelectionChanged+=(_,_)=>{if(reverting||Equals(previous,choose.SelectedItem))return;if(dirty){reverting=true;choose.SelectedItem=previous;reverting=false;ShowNotice("本来源还有未保存的修改","请先保存或放弃本页修改后，再选择其他来源。",InfoBarSeverity.Warning);return;}previous=choose.SelectedItem;SelectProvider();};SelectProvider();

        verifyProviderExperience=async()=>{
            var tileButtons=Descendants<Button>(PageBody).Where(b=>(AutomationProperties.GetAutomationId(b)??"").StartsWith("provider-tile-")).ToList();
            if(tileButtons.Count!=s.Providers.Count+1)throw new InvalidOperationException("来源磁贴数量与配置不一致");
            foreach(var provider in s.Providers)if(!tileButtons.Any(b=>AutomationProperties.GetAutomationId(b)=="provider-tile-"+provider.Id))throw new InvalidOperationException("来源磁贴缺少："+provider.Id);
            var advancedExpander=Descendants<Expander>(PageBody).FirstOrDefault(e=>(e.Header?.ToString()??"").StartsWith("高级设置"));
            if(advancedExpander is null||advancedExpander.IsExpanded)throw new InvalidOperationException("高级设置没有默认折叠");
            advancedExpander.IsExpanded=true;await Task.Delay(180);
            foreach(string header in new[]{"显示名称","接口地址（Base URL）","认证方式","接入代号（自动填写，可自定义）","密钥环境变量名（自动填写）","请求适配","代理地址（仅 ChatGPT 来源）"})
                if(!Descendants<TextBox>(PageBody).Any(t=>t.Header?.ToString()==header)&&!Descendants<ComboBox>(PageBody).Any(c=>c.Header?.ToString()==header))throw new InvalidOperationException("缺少字段："+header);
            advancedExpander.IsExpanded=false;
            if(!Descendants<PasswordBox>(PageBody).Any(k=>(k.Header?.ToString()??"").Contains("API Key")))throw new InvalidOperationException("缺少 API Key 输入框");
            if(Descendants<TextBlock>(PageBody).Any(t=>t.Text.Contains("已验证")||t.Text.Contains("验证通过")))throw new InvalidOperationException("界面出现了未经证实的密钥结论");
            var nameInput=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称");string before=nameInput.Text;string selected=choose.SelectedItem?.ToString()??add;
            nameInput.Text=before+" · 体验自检";await Task.Delay(120);
            if(!dirty)throw new InvalidOperationException("修改来源没有标记草稿");
            if(s.Providers.Count>1)
            {
                var other=s.Providers.First(p=>Label(p)!=selected);
                choose.SelectedItem=Label(other);await Task.Delay(120);
                if(choose.SelectedItem?.ToString()!=selected)throw new InvalidOperationException("有草稿时仍然切换了来源");
            }
            nameInput.Text=before;await Task.Delay(60);dirty=false;
        };

        var local=Section("本地转发服务");local.Spacing=12;
        local.Children.Add(Note("本地转发负责真正发出请求：运行状态、配置同步与启动都在这里，和上方的来源设置互相独立。"));
        healthPanel=new();healthPanel.Children.Add(Note("正在读取本地服务状态…"));local.Children.Add(healthPanel);
        var start=AsyncAction("启动本地服务",async()=>{await runtime.StartRouterAsync(s);await RefreshHealthAsync();ShowNotice("已提交启动请求","已经在线的服务会继续运行。请在状态刷新后确认是否成功启动。",InfoBarSeverity.Success);});
        var check=AsyncAction("核对配置是否已生效（不调用模型）",async()=>{var h=await runtime.CheckRouterConfigurationAsync(s,lifetime.Token);ShowNotice(h.Status,h.Detail,h.Online?InfoBarSeverity.Success:InfoBarSeverity.Warning);});
        var install=AsyncAction("安装路由自动加载支持",async()=>{await runtime.InstallRouterReloadSupportAsync(s,lifetime.Token);ShowNotice("路由支持已安装","已备份原路由模块。当前请求继续；下次路由进程启动后启用自动加载，重启后请核对运行配置。",InfoBarSeverity.Warning);});
        foreach(var button in new[]{start,check,install}){button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Center;}
        local.Children.Add(EvenGrid(300,3,start,check,install));
        local.Children.Add(Note("支持自动加载的路由会在后续请求前读取新路由和加密密钥，正在进行的请求继续。旧服务会明确提示尚未确认，需要结束当前任务后重启。"));
        Add(Card(local));
    }
    private static bool EnvironmentKeyExists(string key)=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key,EnvironmentVariableTarget.Process))||!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key,EnvironmentVariableTarget.User));
    private static bool IsValidEndpoint(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme is "http" or "https"&&string.IsNullOrEmpty(uri.UserInfo)&&string.IsNullOrEmpty(uri.Query)&&string.IsNullOrEmpty(uri.Fragment);
}
