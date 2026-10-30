using Microsoft.UI.Xaml.Controls;
using System.Text.Json;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private async Task RunUiStressAsync(string directory)
    {
        Directory.CreateDirectory(directory);int completed=0,selections=0,draftRestores=0;
        using var collection=new CancellationTokenSource();
        var collector=Task.Run(()=>{while(!collection.IsCancellationRequested){GC.Collect(2,GCCollectionMode.Forced,false,false);Thread.Sleep(100);}});
        try{
            var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"--cycles");int cycles=at>=0&&at+1<args.Length&&int.TryParse(args[at+1],out var count)?Math.Clamp(count,1,300):60;
            for(int i=0;i<cycles;i++){
                dirty=false;drafts.Clear();Navigate("models");await FlushUiUpdatesAsync();await Task.Delay(12);
                var list=Descendants<ListView>(PageBody).First();
                for(int n=0;n<4&&list.Items.Count>0;n++){
                    Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称").Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                    list.SelectedIndex=(i*4+n)%list.Items.Count;selections++;await FlushUiUpdatesAsync();await Task.Delay(4);
                }
                if(i%3==0){
                    var name=Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称");name.Text+=" · 压力草稿";await FlushUiUpdatesAsync();
                    Navigate("settings");await FlushUiUpdatesAsync();Navigate("models");await FlushUiUpdatesAsync();
                    if(!dirty||!ReferenceEquals(name,Descendants<TextBox>(PageBody).First(t=>t.Header?.ToString()=="显示名称")))throw new InvalidOperationException("压力测试中的草稿页面恢复失败");
                    draftRestores++;
                }
                dirty=false;drafts.Remove("models");Navigate("settings");await FlushUiUpdatesAsync();await Task.Delay(12);
                var filter=Descendants<TextBox>(PageBody).First(t=>t.PlaceholderText.Contains("筛选备份"));filter.Text=i%2==0?"no-match-stress":"";await Task.Delay(4);
                Navigate("requests");await Task.Delay(6);completed++;
                if(i%5==0)await File.WriteAllTextAsync(Path.Combine(directory,"stress-progress.json"),JsonSerializer.Serialize(new{completed,selections}));
                if(i%10==0)await RefreshHealthAsync();
            }
            await File.WriteAllTextAsync(Path.Combine(directory,"stress-result.json"),JsonSerializer.Serialize(new{success=true,completed,selections,draftRestores,forcedCollections=true}));
        }catch(Exception error){await File.WriteAllTextAsync(Path.Combine(directory,"stress-result.json"),JsonSerializer.Serialize(new{success=false,completed,selections,draftRestores,error=error.ToString()}));}
        finally{collection.Cancel();await collector;dirty=false;drafts.Clear();Close();}
    }
}
