using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text.Json;

namespace CodexControlCenter.App;

public sealed partial class MainWindow
{
    private void RenderEnhancementSettings()
    {
        bool enabled=EnhancementMode.IsEnabled(snapshot!.CodexHome);
        var panel=Section("Codex 原版与增强模式");
        panel.Children.Add(Text(enabled?"增强模式 · 已启用":"原版模式 · 增强功能已停用",24));
        panel.Children.Add(Note("共享当前电脑上的官方登录和聊天。原版模式使用官方直连；增强模式启用已配置的壁纸、指令、技能与子代理钩子。首次安装不预设第三方来源、模型或备用策略；统一路由需要单独配置。"));
        panel.Children.Add(EvenGrid(240,2,
            AsyncAction("使用原版模式",()=>SwitchEnhancementModeAsync("original")),
            AsyncAction("启用增强模式",()=>SwitchEnhancementModeAsync("enhanced"))));
        panel.Children.Add(Note("修改会统一备份；其他页面有草稿时请先保存。新聊天或正常重开 Codex 后完整生效，已有聊天中的历史指令不会被改写。原版模式下可随时回管理台重新开启。"));
        Add(Card(panel));
    }
    private async Task SwitchEnhancementModeAsync(string mode)
    {
        if(HasDrafts){ShowNotice("请先处理草稿","模式切换会更新配置，请先保存或放弃当前草稿。",InfoBarSeverity.Warning);return;}
        string adapter=Path.Combine(appRoot,"adapters","enhancement-mode","enhancement_mode.py");
        if(!File.Exists(adapter))throw new FileNotFoundException("缺少增强模式组件，请使用完整便携包。",adapter);
        string python=ReadPreferences()["pythonExecutable"]?.ToString()??"";if(string.IsNullOrWhiteSpace(python))python="python";
        var start=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8};
        foreach(string value in new[]{"-X","utf8",adapter,mode,"--codex-home",snapshot!.CodexHome,"--state-home",stateHome,"--workspace",appRoot})start.ArgumentList.Add(value);
        using var process=Process.Start(start)??throw new InvalidOperationException("无法启动增强模式组件。");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try{await process.WaitForExitAsync(timeout.Token);}catch{try{process.Kill(true);}catch{}throw;}
        string text=await output;await error;
        if(process.ExitCode!=0){
            try{using var failure=JsonDocument.Parse(text);throw new InvalidOperationException(failure.RootElement.GetProperty("error").GetString());}
            catch(JsonException){throw new InvalidOperationException("模式切换未完成，原有配置已保留。请检查备份与路径权限。");}
        }
        using var result=JsonDocument.Parse(text);if(!result.RootElement.GetProperty("success").GetBoolean())throw new InvalidOperationException("模式切换未完成。");
        await ReloadAsync();
        ShowNotice(mode=="original"?"已切换为原版模式":"已启用增强模式","登录与聊天已保留。请正常重开 Codex，使指令、技能和启动方式完整更新。",InfoBarSeverity.Success);
    }
    private async Task VerifyEnhancementsAsync(string directory)
    {
        var previous=configuration;var prefs=ReadPreferences();string fake=Path.Combine(Path.GetFullPath(directory),"隔离增强模式配置");Directory.CreateDirectory(fake);
        await File.WriteAllTextAsync(Path.Combine(fake,"config.toml"),"model_provider = \"openai\"\nmodel = \"fixture\"\n[agents]\ndefault_subagent_model = \"fixture\"\n");
        await File.WriteAllTextAsync(Path.Combine(fake,"catalog.json"),"""{"models":[{"slug":"fixture","display_name":"测试模型","supported_reasoning_levels":[{"effort":"high"}]}]}""");
        await File.WriteAllTextAsync(Path.Combine(fake,"router.json"),"""{"providers":{"fixture":{"base_url":"http://127.0.0.1:9/v1","key_env":"FAKE_KEY"}},"routes":{"fixture":{"provider":"fixture","model":"fixture"}}}""");
        string agents="User instruction\r\n<!-- codex-control-center:image-generation:start -->\r\nManaged rule\r\n<!-- codex-control-center:image-generation:end -->\r\n";
        await File.WriteAllTextAsync(Path.Combine(fake,"AGENTS.md"),agents);
        try{
            configuration=new ConfigurationService(fake,stateHome);dirty=false;drafts.Clear();await ReloadAsync();Navigate("settings");Render();await Task.Delay(150);
            if(!Descendants<Button>(PageBody).Any(b=>b.Content as string=="使用原版模式"))throw new InvalidOperationException("原版模式按钮未显示。");
            await SwitchEnhancementModeAsync("original");
            if(EnhancementMode.IsEnabled(fake)||(await File.ReadAllTextAsync(Path.Combine(fake,"AGENTS.md"))).Contains("Managed rule"))throw new InvalidOperationException("原版模式未停用增强内容。");
            Render();await Task.Delay(100);await CaptureAsync(Path.Combine(directory,"original-mode-settings.png"));
            await SwitchEnhancementModeAsync("enhanced");
            if(!EnhancementMode.IsEnabled(fake)||await File.ReadAllTextAsync(Path.Combine(fake,"AGENTS.md"))!=agents)throw new InvalidOperationException("增强模式未原样恢复指令。");
            Render();await Task.Delay(100);await CaptureAsync(Path.Combine(directory,"enhanced-mode-settings.png"));
            dirty=true;await SwitchEnhancementModeAsync("original");if(!EnhancementMode.IsEnabled(fake))throw new InvalidOperationException("有草稿时允许修改模式。");dirty=false;
            await File.WriteAllTextAsync(Path.Combine(directory,"enhancements-result.json"),JsonSerializer.Serialize(new{success=true,originalAndEnhancedSwitch=true,rawInstructionRoundtrip=true,settingsButtons=true,draftGuard=true,isolatedHome=fake,mouseOrKeyboardControl=false,inferenceRequests=0}));
        }finally{configuration=previous;WritePreferences(prefs);dirty=false;drafts.Clear();await ReloadAsync();}
    }
}
