using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexControlCenter.Core;

public sealed class SubagentModelChoice
{
    public string Provider { get; set; }="";
    public string Model { get; set; }="";
    public string Effort { get; set; }="max";
}
public sealed class SubagentRoutingPolicy
{
    public int Version { get; set; }=1;
    public bool Enabled { get; set; }
    public bool AutoTrustHooks { get; set; }=false;
    public string Mode { get; set; }="concurrent";
    public string Revision { get; set; }="";
    public string RouterConfigPath { get; set; }="";
    public SubagentModelChoice Primary { get; set; }=new();
    public SubagentModelChoice Overflow { get; set; }=new();
}
public sealed class SubagentPolicyService(string codexHome,string appRoot)
{
    private const string Marker="Codex 管理台 · 子代理全局分流";
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,PropertyNameCaseInsensitive=true,WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    private readonly string home=Path.GetFullPath(codexHome);
    public string PolicyPath=>Path.Combine(home,"control-center","subagent-policy.json");
    public string StatusPath=>Path.Combine(home,"control-center","subagent-policy-status.json");
    public string HookPath=>Path.Combine(home,"control-center","runtime","subagent-policy","policy_hook.py");
    public string SourceHookPath=>Path.Combine(Path.GetFullPath(appRoot),"adapters","subagent-policy","policy_hook.py");
    public string HookCommand(string pythonPath)=>$"\"{pythonPath}\" \"{HookPath}\" --codex-home \"{home}\"";
    public static string SpawnArguments(SubagentModelChoice choice)=>new JsonObject{
        ["model"]=choice.Model,["reasoning_effort"]=choice.Effort,["fork_context"]=false
    }.ToJsonString(new JsonSerializerOptions{Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
    private static string ModelIdentityInstructions=>
        "调用 spawn_agent 时，model 必须逐字使用下面 JSON 中的完整模型编号；接入来源仅用于路由核对，不是 model 的前缀。禁止自行拼接来源/模型，也禁止对已有斜杠的编号再次添加来源。\n"+
        "创建工具的模型提示和 Unknown model 错误可能只展示前五项，不代表完整可用目录；不要仅因模型未列在提示中就认定宿主未开放。若返回 Unknown model，先核对实际提交的 model 与下面编号是否完全一致。明确拼写错误可改正同一模型后再提交；尚未取得代理 ID 时不调用 close_agent，不将参数错误当成上游模型故障。正确编号仍被拒绝时报告创建失败，不擅自换源；仅在本节明确授权备用时按其规则处理。\n";
    public SubagentRoutingPolicy Load(WorkspaceSnapshot s)
    {
        if(File.Exists(PolicyPath))return JsonSerializer.Deserialize<SubagentRoutingPolicy>(File.ReadAllText(PolicyPath),JsonOptions)??throw new FormatException("子代理分流配置为空");
        var initial=s.Models.FirstOrDefault(m=>m.Id==s.AgentModel&&m.Mapped)??s.Models.FirstOrDefault(m=>m.Mapped);
        return new(){Primary=new(){Provider=initial?.Provider??"",Model=initial?.Id??"",Effort=s.AgentEffort},Overflow=new(){Provider=initial?.Provider??"",Model=initial?.Id??"",Effort=s.AgentEffort}};
    }
    public JsonObject? ReadStatus()
    {
        if(!File.Exists(StatusPath))return null;
        try{return JsonNode.Parse(File.ReadAllText(StatusPath)) as JsonObject;}catch(IOException){return null;}catch(JsonException){return null;}
    }
    public ChangePlan Plan(WorkspaceSnapshot s,SubagentRoutingPolicy policy,string pythonPath,string? configDraft=null,string? agentsDraft=null)
    {
        var plan=new ChangePlan();
        if(!EnhancementMode.IsEnabled(home)){plan.Errors.Add("当前为原版模式。请先在设置与备份中启用增强模式，再应用子代理策略。");return plan;}
        if(policy.Mode is not ("concurrent" or "failure_fallback"))plan.Errors.Add("请选择并发分流或默认模型与失败备用。");
        if(policy.Enabled){
            ValidateChoice(s,policy.Primary,"A",plan.Errors);ValidateChoice(s,policy.Overflow,"B",plan.Errors);
            if(policy.Primary.Model==policy.Overflow.Model&&policy.Primary.Provider==policy.Overflow.Provider)plan.Errors.Add("请为 A、B 选择不同的接入来源或模型。");
            if(!Path.IsPathRooted(pythonPath)||!File.Exists(pythonPath))plan.Errors.Add("请先在设置中配置可用的 Python 路径。");
            if(!File.Exists(SourceHookPath))plan.Errors.Add("发布目录缺少子代理调度脚本。");
        }
        if(plan.Errors.Count>0)return plan;
        var previous=Load(s);
        bool same=previous.Enabled==policy.Enabled&&previous.Mode==policy.Mode&&previous.AutoTrustHooks==policy.AutoTrustHooks&&
            previous.Primary.Provider==policy.Primary.Provider&&previous.Primary.Model==policy.Primary.Model&&previous.Primary.Effort==policy.Primary.Effort&&
            previous.Overflow.Provider==policy.Overflow.Provider&&previous.Overflow.Model==policy.Overflow.Model&&previous.Overflow.Effort==policy.Overflow.Effort&&
            previous.RouterConfigPath==s.RouterPath;
        policy.Revision=same&&!string.IsNullOrWhiteSpace(previous.Revision)?previous.Revision:Guid.NewGuid().ToString("N");policy.Version=1;policy.RouterConfigPath=s.RouterPath;
        if(policy.Enabled)Add(plan,s,HookPath,File.ReadAllText(SourceHookPath),"安装固定入口的子代理钩子","下次钩子事件读取；版本升级不改变钩子命令路径");
        Add(plan,s,PolicyPath,JsonSerializer.Serialize(policy,JsonOptions)+Environment.NewLine,"保存全局子代理 A/B 策略","按设置记住钩子授权；Codex 重新加载后，新建子代理使用当前策略");
        string hookFile=Path.Combine(home,"hooks.json");
        JsonObject root;
        try{root=File.Exists(hookFile)?JsonNode.Parse(File.ReadAllText(hookFile)) as JsonObject??throw new FormatException("hooks.json 根节点必须为对象"):new JsonObject();}
        catch(Exception ex) when(ex is JsonException or FormatException or IOException){
            plan.Files.Clear();plan.Errors.Add("hooks.json 格式无效或无法读取，请修正 JSON 后重试；原文件和本次全部配置均未改写。");return plan;
        }
        if(root["hooks"] is not null&&root["hooks"] is not JsonObject)throw new FormatException("hooks.json 的 hooks 必须为对象，已保留原文件。");
        if(root["hooks"] is not JsonObject hooks){hooks=new();root["hooks"]=hooks;}
        foreach(var entry in hooks.ToArray()){
            if(entry.Value is not JsonArray groups)continue;
            foreach(var node in groups.ToArray()){
                if(node is not JsonObject group||group["hooks"] is not JsonArray handlers)continue;
                foreach(var handler in handlers.ToArray())if(handler?["statusMessage"]?.ToString()==Marker)handlers.Remove(handler);
                if(handlers.Count==0)groups.Remove(node);
            }
        }
        if(policy.Enabled){
            string command=HookCommand(pythonPath);
            foreach(string name in new[]{"PreToolUse","PostToolUse","SubagentStart","SubagentStop","SessionEnd"}){
                if(hooks[name] is not null&&hooks[name] is not JsonArray)throw new FormatException($"hooks.json 的 {name} 必须为数组。");
                if(hooks[name] is not JsonArray groups){groups=new();hooks[name]=groups;}
                var group=new JsonObject();if(name is "PreToolUse" or "PostToolUse")group["matcher"]="(spawn_agent|send_input|close_agent|resume_agent)$|^Agent$";
                group["hooks"]=new JsonArray(new JsonObject{["type"]="command",["command"]=command,["commandWindows"]=command,["timeout"]=10,["statusMessage"]=Marker});groups.Add(group);
            }
            string config=TomlEditor.SetValue(configDraft??File.ReadAllText(s.ConfigPath),"features","hooks",true,s.ConfigPath);
            if(config!=File.ReadAllText(s.ConfigPath))Add(plan,s,s.ConfigPath,config,"启用 Codex 生命周期 Hook","Codex 重启后加载");
        }
        if(policy.Enabled||File.Exists(hookFile))Add(plan,s,hookFile,root.ToJsonString(JsonOptions)+Environment.NewLine,"更新子代理创建与结束 Hook","所有共享此 CODEX_HOME 的新会话；已有会话需重新加载");
        string agents=agentsDraft??(File.Exists(s.AgentsPath)?File.ReadAllText(s.AgentsPath):"");
        const string start="<!-- codex-control-center:subagent-policy:start -->",end="<!-- codex-control-center:subagent-policy:end -->";
        try { agents=RemoveManagedInstructions(agents,start,end); }
        catch(FormatException ex) { plan.Errors.Add(ex.Message);return plan; }
        if(policy.Enabled&&policy.Mode=="failure_fallback")agents=agents.TrimEnd()+Environment.NewLine+Environment.NewLine+start+Environment.NewLine+
            "## 默认子代理与失败备用（受管理）\n\n"+
            "启用模型分配时，来源、模型与推理强度以本节为准；未启用分配时的默认配置独立保留。其他编排、质量与交接要求继续有效。\n"+
            ModelIdentityInstructions+
            $"默认接入来源：`{policy.Primary.Provider}`。默认调用参数：`{SpawnArguments(policy.Primary)}`。\n"+
            $"失败备用来源：`{policy.Overflow.Provider}`。备用调用参数：`{SpawnArguments(policy.Overflow)}`。\n"+
            "用户明确指定：所有新建子代理使用默认调用参数；只有默认子代理创建或接入明确失败后，才允许按备用调用参数替代一次。并发增加不会切换来源。\n"+
            "收到创建错误，或子代理返回 errored 状态时，先如实说明失败；若已返回代理 ID，关闭失败代理并确认其 previous_status 为 errored，再使用备用模型重新创建；创建阶段未返回 ID 时保留原始错误，不调用 close_agent。交接完整任务、已完成内容和已有修改。没有明确失败证据、只是运行较慢或未交付时，不得自动备用。备用也失败则停止并报告，不循环换源，不重复执行已完成的工作。\n"+
            "该备用切换已获用户授权，无需再次询问；除此以外仍禁止换用其他模型。模型和 reasoning_effort 启动时显式指定，不继承父历史，使用完整明文交接。调度 Hook 只记录失败并校验一次备用许可，不会自行创建子代理；实际替代由主代理根据工具结果发起。\n"+end+Environment.NewLine;
        else if(policy.Enabled)agents=agents.TrimEnd()+Environment.NewLine+Environment.NewLine+start+Environment.NewLine+
            "## 全局子代理模型分流（受管理）\n\n"+
            ModelIdentityInstructions+
            $"用户在管理台启用了全局分流，模型选择以本节为准：首个工作槽位 A 的接入来源是 `{policy.Primary.Provider}`，调用参数：`{SpawnArguments(policy.Primary)}`；其余新建子代理 B 的接入来源是 `{policy.Overflow.Provider}`，调用参数：`{SpawnArguments(policy.Overflow)}`。\n"+
            "全局槽位由本机 Hook 原子分配，不依据单个聊天计数或 HTTP 请求数。不要通过指定其他模型绕过分配；不要自动切换来源或将失败当作回退授权。保留按需委派、完整明文交接、不继承父历史和失败如实报告的规则。\n"+
            "A 完成后释放优先槽位，之后新建的子代理可使用 A；已分配的工作保持原模型。已完成的 A 重新接到任务时，如果另一位已占 A，则等待它结束再重试，不能偷偷改用 B。\n"+end+Environment.NewLine;
        if(agents!=(File.Exists(s.AgentsPath)?File.ReadAllText(s.AgentsPath):""))Add(plan,s,s.AgentsPath,agents,"同步子代理分流指令","新会话加载，保留现有默认模型和其他用户指令");
        return plan;
    }
    private static string RemoveManagedInstructions(string text,string start,string end)
    {
        // Older versions could leave a known generated heading with only its end marker.
        // Repair that shape, but stop on ambiguous boundaries instead of deleting user rules.
        var result=new System.Text.StringBuilder();bool inside=false,headingSeen=false;
        foreach(string line in System.Text.RegularExpressions.Regex.Split(text,@"(?<=\n)")){
            string value=line.Trim();
            bool title=value is "## 默认子代理与失败备用（受管理）" or "## 全局子代理模型分流（受管理）";
            if(value==start){
                if(inside)throw new FormatException("子代理受管理指令存在嵌套或缺失结束标记；请先修复边界，已保留原文件。");
                inside=true;headingSeen=false;continue;
            }
            if(value==end){
                if(!inside)throw new FormatException("子代理受管理指令存在无法识别的孤立结束标记；已保留原文件。");
                inside=false;headingSeen=false;continue;
            }
            if(title){
                if(inside&&headingSeen)throw new FormatException("子代理受管理指令存在重复标题且缺少边界；已保留原文件。");
                inside=true;headingSeen=true;continue;
            }
            if(inside&&System.Text.RegularExpressions.Regex.IsMatch(value,@"^#{1,6}\s"))
                throw new FormatException("子代理受管理指令缺少结束标记且包含其他章节；已保留原文件。");
            if(!inside)result.Append(line);
        }
        if(inside)throw new FormatException("子代理受管理指令缺少结束标记；已保留原文件。");
        return result.ToString();
    }
    private static void ValidateChoice(WorkspaceSnapshot s,SubagentModelChoice choice,string name,List<string> errors)
    {
        var model=s.Models.FirstOrDefault(m=>m.Id==choice.Model&&m.Mapped&&m.Provider==choice.Provider);
        if(model is null){errors.Add($"{name} 的模型与接入来源不匹配，或缺少路由。");return;}
        var efforts=model.Efforts.Split([',',' '],StringSplitOptions.RemoveEmptyEntries);
        if(choice.Effort is not ("none" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra")||efforts.Length>0&&!efforts.Contains(choice.Effort))errors.Add($"{name} 不支持所选推理档位。");
    }
    private static void Add(ChangePlan plan,WorkspaceSnapshot s,string path,string content,string description,string effect)
    {
        s.FileHashes.TryAdd(path,File.Exists(path)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))):"<missing>");
        if(File.Exists(path)&&File.ReadAllText(path)==content)return;
        plan.Files.Add(new(path,content,description,effect));
    }
}
