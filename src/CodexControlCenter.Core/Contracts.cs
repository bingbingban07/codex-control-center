namespace CodexControlCenter.Core;

public sealed class WorkspaceSnapshot
{
    public string CodexHome { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string CatalogPath { get; set; } = "";
    public string RouterPath { get; set; } = "";
    public string ImageConfigPath { get; set; } = "";
    public string AgentsPath { get; set; } = "";
    public string MainModel { get; set; } = "";
    public string MainEffort { get; set; } = "high";
    public string AgentModel { get; set; } = "";
    public string AgentEffort { get; set; } = "max";
    public int AgentConcurrency { get; set; } = 20;
    public List<ModelEntry> Models { get; set; } = [];
    public List<ProviderEntry> Providers { get; set; } = [];
    public List<SkillEntry> Skills { get; set; } = [];
    public List<GlobalMarkdownEntry> GlobalMarkdownFiles { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public Dictionary<string,string> FileHashes { get; set; } = [];
    public DateTimeOffset LoadedAt { get; set; } = DateTimeOffset.Now;
}
public sealed class ModelEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Provider { get; set; } = "";
    public string UpstreamModel { get; set; } = "";
    public long ContextWindow { get; set; }
    public long MaxContextWindow { get; set; }
    public long? AutoCompactTokenLimit { get; set; }
    public int EffectiveContextWindowPercent { get; set; }=95;
    public bool UpdateCompactionSettings { get; set; }
    public string Efforts { get; set; } = "";
    public string AgentProtocol { get; set; } = "";
    public bool Mapped { get; set; }
    public string Visibility { get; set; } = "";
    public string VisibilityText => Visibility switch
    {
        "list" => "Codex 中显示",
        "hide" => "Codex 中隐藏",
        "" => "显示规则未声明",
        _ => "其他显示规则"
    };
    public string Status => Mapped ? "已映射 · 调用未验证" : "缺少路由映射";
}
public sealed class ProviderEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string AuthMode { get; set; } = "api_key";
    public string KeyEnvironment { get; set; } = "";
    public string RequestProfile { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    public string CredentialStatus { get; set; } = "未验证";
}
public sealed class SkillEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Editable { get; set; }
    public override string ToString() => Name;
}
public sealed record FileEdit(string Path, string Content, string Description, string Effect);
public sealed class ChangePlan
{
    public List<FileEdit> Files { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public string Summary => string.Join(Environment.NewLine + Environment.NewLine, Files.Select(f => $"{f.Description}\n{f.Path}\n生效：{f.Effect}"));
}
public sealed record ApplyResult(string BackupDirectory, IReadOnlyList<string> Effects);
public sealed record BackupEntry(string Directory, DateTime CreatedAt, string Summary);
public sealed record ConfigurationProgress(string Stage,string FilePath);
public sealed record GlobalMarkdownEntry(string Name,string Path,string Kind,string Effect,string Source="全局配置",bool Editable=true);
