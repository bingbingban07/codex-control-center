namespace CodexControlCenter.Core;

public sealed record NavigationTarget(string Title,string Description,string Page,string? ItemId=null,string Keywords="");

public static class QuickNavigation
{
    public static IReadOnlyList<NavigationTarget> Search(WorkspaceSnapshot? snapshot,string query,int limit=30)
    {
        var targets=new List<NavigationTarget>{
            new("概览","默认配置、运行状态与待处理事项","overview",Keywords:"首页 dashboard status"),
            new("模型接入","接口地址、认证与 API Key 配置","providers",Keywords:"密钥 key provider endpoint"),
            new("模型列表","模型目录、上下文与推理设置","models",Keywords:"model context tokens 压缩阈值"),
            new("请求记录","查看真实调用结果与错误说明","requests",Keywords:"log error history 日志"),
            new("子代理","模型分配、失败备用与钩子授权","agents",Keywords:"agent hook fallback"),
            new("生图接口","默认图片来源与生成任务","images",Keywords:"image 图片 i2m"),
            new("技能与指令","编辑指令、技能及角色文档","documents",Keywords:"AGENTS markdown skills roles"),
            new("壁纸","图片库、背景预览与 Codex 外观","wallpaper",Keywords:"wallpaper background 图片 背景 透明度 模糊"),
            new("设置与备份","外观、配置目录与恢复","settings",Keywords:"settings backup theme 字号"),
            new("使用指南","操作说明与常见问题","help",Keywords:"帮助 help"),
        };
        if(snapshot is not null){
            targets.AddRange(snapshot.Models.Select(m=>new NavigationTarget(string.IsNullOrWhiteSpace(m.DisplayName)?m.Id:m.DisplayName,
                "模型 · "+m.Id+" · "+m.Provider,"models",m.Id,m.UpstreamModel+" "+m.Provider)));
            targets.AddRange(snapshot.Providers.Select(p=>new NavigationTarget(string.IsNullOrWhiteSpace(p.Name)?p.Id:p.Name,
                "接入来源 · "+p.Id,"providers",p.Id,p.Id+" provider 接口")));
        }
        string[] words=query.Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        return targets.Select((target,index)=>(target,index,score:Score(target,words)))
            .Where(row=>row.score>=0).OrderByDescending(row=>row.score).ThenBy(row=>row.index)
            .Take(Math.Clamp(limit,1,100)).Select(row=>row.target).ToList();
    }

    private static int Score(NavigationTarget target,string[] words)
    {
        int score=0;
        foreach(string word in words){
            if(target.Title.Equals(word,StringComparison.OrdinalIgnoreCase))score+=20;
            else if(target.Title.Contains(word,StringComparison.OrdinalIgnoreCase))score+=10;
            else if((target.Description+" "+target.Keywords).Contains(word,StringComparison.OrdinalIgnoreCase))score+=1;
            else return -1;
        }
        return score;
    }
}
