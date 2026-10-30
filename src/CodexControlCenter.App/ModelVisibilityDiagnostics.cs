using CodexControlCenter.Core;
using Microsoft.UI.Xaml.Controls;
using System.Text.Json.Nodes;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task VerifyModelVisibilityAsync(string directory)
    {
        var s=snapshot!;string text=await File.ReadAllTextAsync(s.CatalogPath);var catalog=JsonNode.Parse(text)!;
        var array=catalog["models"]!.AsArray();var first=array[0]!.AsObject();
        first["visibility"]="list";
        var hidden=first.DeepClone().AsObject();hidden["slug"]="hidden-fixture";hidden["display_name"]="隐藏的演示模型";hidden["visibility"]="hide";array.Add(hidden);
        var unspecified=first.DeepClone().AsObject();unspecified["slug"]="unspecified-fixture";unspecified["display_name"]="未声明显示规则的演示模型";unspecified.Remove("visibility");array.Add(unspecified);
        try{
            await File.WriteAllTextAsync(s.CatalogPath,catalog.ToJsonString());dirty=false;drafts.Clear();Navigate("models");await ReloadAsync();await Task.Delay(100);
            var list=Descendants<ListView>(PageBody).First();var filter=Descendants<ComboBox>(PageBody).First(c=>c.Header?.ToString()=="按显示状态筛选");
            if(list.Items.Count!=3||dirty)throw new InvalidOperationException("模型显示管理初始化不正确");
            filter.SelectedIndex=2;await Task.Delay(100);
            if(list.Items.Count!=1||((list.Items[0] as ListViewItem)?.Tag as ModelEntry)?.Id!="hidden-fixture"||dirty)throw new InvalidOperationException("隐藏模型筛选未生效或误改配置");
            list.SelectedIndex=0;await Task.Delay(100);
            var visibility=Descendants<ComboBox>(PageBody).First(c=>c.Header?.ToString()=="Codex 列表显示");visibility.SelectedIndex=0;await Task.Delay(100);filter.SelectedIndex=0;await Task.Delay(100);
            var current=list.Items.OfType<ListViewItem>().Single(i=>(i.Tag as ModelEntry)?.Id==s.MainModel);list.SelectedItem=current;await Task.Delay(100);
            visibility=Descendants<ComboBox>(PageBody).First(c=>c.Header?.ToString()=="Codex 列表显示");visibility.SelectedIndex=1;await Task.Delay(100);
            var plan=planFactory!();
            if(plan.Errors.Count>0||plan.Files.Count!=1||plan.Files[0].Path!=s.CatalogPath)throw new InvalidOperationException("多模型显示草稿未合并为单一目录变更");
            Navigate("help");await FlushUiUpdatesAsync();Navigate("models");await Task.Delay(100);
            if(!dirty||planFactory!().Files.Count!=1)throw new InvalidOperationException("跨页丢失模型显示草稿");
            var name=Descendants<TextBox>(PageBody).First(c=>c.Header?.ToString()=="显示名称");name.Text+=" · 可视化编辑";await Task.Delay(100);plan=planFactory!();
            if(plan.Errors.Count>0||plan.Files.Select(f=>f.Path).Distinct().Count()!=plan.Files.Count||!plan.Files.Any(f=>f.Path==s.RouterPath))throw new InvalidOperationException("显示设置和模型编辑无法组合");
            var staged=JsonNode.Parse(plan.Files.Single(f=>f.Path==s.CatalogPath).Content)!["models"]!.AsArray();
            if(staged.Single(n=>n!["slug"]!.ToString()==s.MainModel)!["visibility"]!.ToString()!="hide"||staged.Single(n=>n!["slug"]!.ToString()=="hidden-fixture")!["visibility"]!.ToString()!="list")throw new InvalidOperationException("组合保存丢失显示草稿");
            await CaptureAsync(Path.Combine(directory,"model-display-fixture.png"));
            if(verifyModelVisibilityBulk is null)throw new InvalidOperationException("没有创建批量显示验证入口");await verifyModelVisibilityBulk();
        }finally{await File.WriteAllTextAsync(s.CatalogPath,text);dirty=false;drafts.Clear();await ReloadAsync();}
    }
}
