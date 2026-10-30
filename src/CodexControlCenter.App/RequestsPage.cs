using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private List<RequestRecord> recentRequests=[];
    private Action? requestRender;
    private void RenderRequests()
    {
        var summary=Section("最近采样概况");var total=Text("",22);summary.Children.Add(total);
        var success=Note("");var failure=Note("");var unknown=Note("");
        summary.Children.Add(TileGrid(Card(success),Card(failure),Card(unknown)));
        summary.Children.Add(Note("仅统计最近读取的最多 100 条日志记录。不同来源独立计数，不表示账号总用量或独立请求总数。"));Add(summary);
        var search=new TextBox{Header="搜索模型或接入",PlaceholderText="模型编号、来源名称或接入 ID",FontSize=bodySize,MinHeight=52};
        var provider=Choice("接入来源",["全部接入"],"全部接入",false);
        var result=Choice("结果",["全部结果","成功","错误或取消","结果未确认"],"全部结果",false);
        Add(TileGrid(search,provider,result));
        var toolbar=Section();var reset=Action("清除筛选",()=>{search.Text="";provider.SelectedIndex=0;result.SelectedIndex=0;});toolbar.Children.Add(reset);
        var count=Note("");toolbar.Children.Add(count);Add(toolbar);var entries=new StackPanel{Spacing=12};Add(entries);
        var labels=new Dictionary<string,string>();var expanded=new HashSet<string>();string? fingerprint=null;bool updating=false;
        void Update(){
            if(updating)return;updating=true;
            try{
            string selectedId=labels.GetValueOrDefault(provider.SelectedItem?.ToString()??"")??"";
            var ids=snapshot!.Providers.Select(p=>p.Id).Concat(recentRequests.Select(r=>r.Provider)).Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct().Order().ToList();
            var nextLabels=ids.ToDictionary(id=>SourceName(id)+" ["+id+"]",id=>id);
            if(!labels.SequenceEqual(nextLabels)){
                labels=nextLabels;ClearControlItems(provider);provider.Items.Add("全部接入");foreach(string label in labels.Keys)provider.Items.Add(label);
                provider.SelectedItem=labels.FirstOrDefault(p=>p.Value==selectedId).Key??"全部接入";
            }
            selectedId=labels.GetValueOrDefault(provider.SelectedItem?.ToString()??"")??"";
            string q=search.Text.Trim();
            string next=System.Text.Json.JsonSerializer.Serialize(new{recentRequests,q,selectedId,result.SelectedIndex});if(next==fingerprint)return;fingerprint=next;
            total.Text=$"{recentRequests.Count} 条已采样记录";success.Text=$"{recentRequests.Count(RequestSucceeded)} 条成功";failure.Text=$"{recentRequests.Count(r=>IsFailure(r.Status))} 条错误或取消";unknown.Text=$"{recentRequests.Count(r=>!RequestSucceeded(r)&&!IsFailure(r.Status))} 条结果未确认";
            success.Foreground=ToneBrush("ok");failure.Foreground=ToneBrush("bad");
            var values=recentRequests.Where(r=>(q.Length==0||(r.Model+" "+r.Provider+" "+SourceName(r.Provider)).Contains(q,StringComparison.OrdinalIgnoreCase))&&(selectedId.Length==0||r.Provider==selectedId)).Where(r=>result.SelectedIndex switch{1=>RequestSucceeded(r),2=>IsFailure(r.Status),3=>!RequestSucceeded(r)&&!IsFailure(r.Status),_=>true}).ToList();
            count.Text=$"筛选后 {values.Count} 条 / 已采样 {recentRequests.Count} 条 · 展开一条记录查看来源与处理建议";
            reset.IsEnabled=q.Length>0||selectedId.Length>0||result.SelectedIndex>0;ClearPanelChildren(entries);
            if(values.Count==0){entries.Children.Add(EmptyState("当前筛选下没有真实请求记录",recentRequests.Count==0?"实际调用出现后会自动刷新。你也可以先到模型接入检查来源配置。":"试试清除筛选，或搜索其他模型与来源。"));return;}
            var occurrences=new Dictionary<string,int>();
            foreach(var record in values){
                string key=System.Text.Json.JsonSerializer.Serialize(record);int occurrence=occurrences.GetValueOrDefault(key);occurrences[key]=occurrence+1;key+="#"+occurrence;
                string status=record.Model=="未采集"&&IsSuccess(record.Status)?"HTTP "+record.Status+" · 模型结果未确认":RuntimeService.DescribeStatus(record.Status);var detail=Section();
                detail.Children.Add(Note("记录来源："+Safe(string.IsNullOrWhiteSpace(record.Source)?"未采集":record.Source)));
                detail.Children.Add(Note("接入："+Safe(SourceName(record.Provider))+" ["+Safe(record.Provider)+"]"));
                detail.Children.Add(Note("模型："+Safe(record.Model)));detail.Children.Add(Note("时间："+Safe(record.Time)+" · 耗时："+Safe(record.Duration)));
                if(!string.IsNullOrWhiteSpace(record.RequestId))detail.Children.Add(Note("关联编号："+Safe(record.RequestId)));
                detail.Children.Add(Text(record.Model=="未采集"?"此记录未关联到模型，可能来自旧版旁路观测；不能据此判断模型调用成功。":RequestAdvice(record.Status),16));
                var expander=new Expander{Header=Row(Safe(record.Model),Safe(SourceName(record.Provider))+" · "+Safe(record.Time),status+" · "+Safe(record.Duration),IsFailure(record.Status)?"bad":RequestSucceeded(record)?"ok":"neutral"),Content=detail,IsExpanded=expanded.Contains(key),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};
                expander.Expanding+=(_,_)=>expanded.Add(key);expander.Collapsed+=(_,_)=>expanded.Remove(key);entries.Children.Add(expander);
            }
            // Drop keys that have aged out of the bounded sample, retaining filtered-out expansion states.
            var live=new HashSet<string>();var counts=new Dictionary<string,int>();foreach(var record in recentRequests){string key=System.Text.Json.JsonSerializer.Serialize(record);int n=counts.GetValueOrDefault(key);counts[key]=n+1;live.Add(key+"#"+n);}expanded.IntersectWith(live);
            }finally{updating=false;}
        }
        requestRender=Update;search.TextChanged+=(_,_)=>Update();provider.SelectionChanged+=(_,_)=>Update();result.SelectionChanged+=(_,_)=>Update();Update();
    }
    private static string RequestAdvice(string status)=>status switch{
        "400"=>"先核对模型映射和接入来源状态；来源正常时，再检查请求参数与该服务的兼容性。不可用来源会在概览的配置状态中列出。",
        "401" or "403"=>"请到模型接入检查 API Key 或登录引用，再重新加载对应服务。",
        "429"=>"服务正在限制请求，请检查该来源的额度与并发限制。",
        "404"=>"检查服务商模型名称与接口地址是否正确。",
        _=>IsSuccess(status)?"这条历史记录已成功，不代表其他模型或后续请求也可用。":IsFailure(status)?"先检查该来源的服务状态与配置，再决定是否手动重试。":"此来源没有采集到明确结果，无法据此判断成功或失败。"};
    private static bool IsSuccess(string status)=>int.TryParse(status,out int code)&&code is >=200 and <300;
    private static bool RequestSucceeded(RequestRecord record)=>!string.IsNullOrWhiteSpace(record.Model)&&record.Model!="未采集"&&IsSuccess(record.Status);
    private static bool IsFailure(string status)=>int.TryParse(status,out int code)&&code>=400||status.Contains("cancel",StringComparison.OrdinalIgnoreCase)||status.Contains("error",StringComparison.OrdinalIgnoreCase);
}
