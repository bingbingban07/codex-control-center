using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private void OnOpenHelp(object sender,RoutedEventArgs e)=>Navigate("help");
    private static string PageInstructions(string key)=>key switch{
        "providers"=>"先选一个已有来源或新增接入 → 填写接口地址与密钥 → 点击检查连接 → 用底部按钮检查并保存。高级设置通常不用改。连接可达只说明服务有响应，并不等于密钥已经验证。",
        "documents"=>"在全局与项目指令、技能、角色三个分类间切换；系统与插件文档只读。项目中的 AGENTS 可通过“添加项目指令目录”导入，子目录中的同名文件也会列出。全部分类的草稿统一预览、备份并保存；普通 Markdown 文件不会自动成为全局指令。",
        "models"=>"搜索、筛选或排序模型，切换时保留每个模型的编辑草稿；底部统一预览保存。高级设置可修改上下文、自动压缩阈值和有效预算。隐藏模型可随时恢复显示。模型目录需重启 Codex 读取，已有聊天的选择不会自动更换。Ctrl+K 可快速定位模型。",
        "agents"=>"可选“按并发分配 A/B”或“默认模型 + 失败备用”。后者只在明确创建/接入失败后重建一次。开启“自动记住管理台钩子授权”后，保存会记住这 5 个钩子的精确指纹；也可使用立即修复入口。Codex 重新加载后使用当前规则。",
        "images"=>"先选择默认图片服务并保存；再填写提示词，手动发起一次测试。API Key 与 ChatGPT Plus 登录不同。生成可能计费，失败或取消都不会自动重试。",
        "wallpaper"=>"批量导入或拖入自己的图片，选择后调整焦点、缩放、强度、模糊与遮罩；可临时在 Codex 中试用，底部应用后持久生效。切页或关闭管理台会结束未保存试用。首次连接先正常退出 Codex，再从桌面“Codex（壁纸）”启动。连接后修改无需重启。图片重命名和收藏立即保存；关闭开关并应用可恢复默认外观。",
        "settings"=>"修改外观、刷新频率、运行工具或配置目录后，统一点击底部保存设置。备份可按日期或内容筛选；恢复前先处理其他页面的草稿。",
        "requests"=>"按模型名称搜索，再按来源和结果筛选。绿色成功只对应那条历史请求；缺少时间或耗时的日志会标明未采集。不同来源没有关联 ID 时不会被拼成一条请求。",
        "help"=>"选择下面的任务入口，按步骤完成即可。遇到问题时，先看提示中的处理方法，再展开技术详情。",
        _=>"这里汇总当前默认模型、服务状态和最近请求。想改配置时进入对应页面；有圆点的页面表示草稿尚未保存。"
    };
    private void RenderHelp()
    {
        PageTitle.Text="使用指南";PageSubtitle.Text="按你要完成的任务选择入口";GuideText.Text=PageInstructions("help");
        foreach(var item in new[]{
            (Title:"接入一个模型服务",Text:"1. 在“模型接入”添加来源，填写地址和独立 API Key。\n2. 在“模型列表”添加该来源提供的模型名称。\n3. 选择默认主模型，检查并保存。",Target:"providers",Button:"开始配置接入"),
            (Title:"让子代理使用不同来源",Text:"1. 在“子代理”选择 A、B 的接入来源和模型。\n2. 启用分流并保存。\n3. 结束旧子代理，在 Codex 重新加载并信任新规则。A 完成后释放槽位，已有任务不会更换模型。",Target:"agents",Button:"配置子代理"),
            (Title:"配置和测试生图",Text:"1. 选择 i2m 或官方图片服务。\n2. 填写该服务的独立密钥并保存。\n3. 输入提示词，手动测试一次。生成可能计费；不自动换源或重试。",Target:"images",Button:"配置图片服务"),
            (Title:"给 Codex 设置壁纸",Text:"1. 在“壁纸”导入图片，或选择内置背景。\n2. 开启壁纸，调整强度、模糊与文字保护遮罩并保存。\n3. 正常退出 Codex，再通过壁纸启动入口打开。连接后修改自动生效；关闭并保存可恢复。",Target:"wallpaper",Button:"设置壁纸"),
            (Title:"管理指令、技能与角色",Text:"1. 进入“技能与指令”，在全局与项目指令、技能、角色三个分类间切换。\n2. 直接编辑原文或创建草稿；系统与插件文档只读。\n3. 底部按钮一次预览、备份并保存全部三类草稿。普通 Markdown 文件是否加载取决于显式引用。",Target:"documents",Button:"管理技能与指令"),
            (Title:"看懂状态与排查错误",Text:"“已配置”只表示配置存在；“连接可达”只表示接口响应。\n“HTTP 200 成功”是某条真实请求的结果。\n401 通常需要检查密钥或登录；429 需要检查服务限制；未采集表示日志没有提供该项信息。",Target:"requests",Button:"查看请求记录"),
            (Title:"保存、草稿和恢复",Text:"编辑后内容只是草稿，可暂时切换页面。保存前会展示改动并备份。\n草稿不写入磁盘，关闭前请保存。\n若文件被其他程序改动，会停止提交并保留草稿；可先对比最新内容。",Target:"settings",Button:"查看设置与备份")
        }){
            var section=Section(item.Title);section.Children.Add(Text(item.Text));section.Children.Add(Action(item.Button,()=>Navigate(item.Target)));Add(Card(section));
        }
    }
    private void RenderStartupRecovery()
    {
        pageInstance=new();
        EnsurePageBodyAttached();
        ClearPanelChildren(PageBody);PageTitle.Text="先找到 Codex 配置目录";PageSubtitle.Text="目录不可用时可在这里修复，无需重新安装";GuideText.Text="选择包含 config.toml 的 Codex 配置目录，再读取。常见位置是当前用户的 .codex 文件夹。";
        NavStatus.Text="尚未连接配置目录";
        string standard=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        string initial=ReadPreferences()["codexHome"]?.ToString()??Environment.GetEnvironmentVariable("CODEX_HOME")??standard;
        var panel=Section("配置目录");var input=Input("Codex 配置目录",initial);panel.Children.Add(input);panel.Children.Add(Note("目录中应有 config.toml。管理台只读取确认；保存目录选择后不会自动修改模型或密钥。"));
        localSave=async()=>{
            string candidate=Path.GetFullPath(input.Text.Trim());if(!File.Exists(Path.Combine(candidate,"config.toml")))throw new FileNotFoundException("该目录中没有 config.toml，请选择 Codex 的配置目录。");
            var service=new ConfigurationService(candidate,stateHome);await Task.Run(()=>service.LoadAsync());var prefs=ReadPreferences();prefs["codexHome"]=candidate;WritePreferences(prefs);configuration=service;return true;
        };
        panel.Children.Add(AsyncAction("读取这个目录",SaveLocalSettingsAsync));panel.Children.Add(Action("填入常用位置",()=>input.Text=standard));Add(Card(panel));RefreshDraftChrome();
    }
    private async Task SaveLocalSettingsAsync()
    {
        if(busy||localSave is null)return;busy=true;RefreshDraftChrome();SetFooter("正在验证并保存设置…");
        try{
            bool wallpaper=page=="wallpaper";bool saved=await localSave();if(saved)RecordSave([wallpaper?"壁纸已应用到本地配置；Codex 实际同步状态见壁纸页。":"管理台外观、目录与工具设置已保存。"]);busy=false;
            if(saved){dirty=false;drafts.Remove(page);bool refreshed=await ReloadAsync();SetFooter(wallpaper?"壁纸设置已应用 · 实际同步状态见连接区域":refreshed?"设置已保存，其他页面的草稿继续保留":"目录设置已保存，但配置未能加载，请按错误提示处理。");}
        }catch(Exception error){SetFooter("设置保存未完成，修改仍保留。");ShowError("设置未保存",error);}finally{busy=false;RefreshDraftChrome();}
    }
}
