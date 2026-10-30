using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;

/// 模型接入页的界面自检：只读取界面状态和内存草稿，不写配置、不发起任何付费请求。
public sealed partial class MainWindow
{
    /// 由模型接入页注册的自检入口：检查来源磁贴、字段分组、默认折叠、状态措辞与切换草稿保护。
    private Func<Task>? verifyProviderExperience;

    /// 供冒烟测试调用：进入模型接入页，跑完界面自检并保存截图；结束后不留下草稿。
    private async Task VerifyProviderExperienceAsync(string directory)
    {
        dirty=false;drafts.Clear();Navigate("providers");await Task.Delay(250);
        if(verifyProviderExperience is null)throw new InvalidOperationException("模型接入页没有提供体验自检入口");
        await verifyProviderExperience();
        await CaptureAsync(System.IO.Path.Combine(directory,"providers-experience.png"));
        dirty=false;drafts.Clear();Banner.IsOpen=false;RefreshDraftChrome();
    }
}
