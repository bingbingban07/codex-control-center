using CodexControlCenter.Core;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private static string ProviderName(ProviderEntry p)=>string.IsNullOrEmpty(p.Name)||p.Name==p.Id?p.Id switch{"a"=>"API Key A","b"=>"API Key B","plus"=>"ChatGPT Plus","mimo"=>"MiMo Token Plan","wb"=>"WorkBuddy",_=>p.Id}:p.Name;
    private IEnumerable<string> EffortsFor(string id){var values=(editSnapshot??snapshot)!.Models.FirstOrDefault(m=>m.Id==id)?.Efforts.Split([',',' '],StringSplitOptions.RemoveEmptyEntries)??[];return values.Length>0?values:["low","medium","high","xhigh","max","ultra"];}

    /// 页面头部的路径标签：说明当前页面属于工作区的哪一部分。
    private static string EyebrowFor(string key)=>CanonicalPageKey(key) switch{
        "providers"=>"配置 / 模型接入","models"=>"配置 / 模型列表","agents"=>"配置 / 子代理",
        "images"=>"配置 / 生图接口","documents"=>"配置 / 技能与指令","wallpaper"=>"外观 / 壁纸","settings"=>"系统 / 设置与备份",
        "requests"=>"运行 / 请求记录","help"=>"系统 / 使用指南",_=>"工作区 / 概览"};
}
