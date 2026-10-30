namespace CodexControlCenter.Core;

public sealed partial class ConfigurationService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,GlobalMarkdownEntry> registeredMarkdown=new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> markdownProjectFolders=new(StringComparer.OrdinalIgnoreCase);
    private sealed record MarkdownScope(string Root,string Source,bool Editable,int Limit=300);
    private IEnumerable<MarkdownScope> MarkdownScopes()=>[
        new(Path.Combine(_codexHome,"skills",".system"),"系统技能",false),
        new(Path.Combine(_codexHome,"plugins","cache"),"插件文档",false,500),
        new(Path.Combine(_codexHome,"skills"),"个人技能",true),
        new(Path.Combine(Path.GetDirectoryName(_codexHome)??_codexHome,".agents","skills"),"共享技能",true),
        new(Path.Combine(_codexHome,"agents"),"角色文件",true),
        new(Path.Combine(_codexHome,"prompts"),"提示词文件",true,200),
        new(Path.Combine(_codexHome,"instructions"),"补充指令",true,200)];
    private bool IsMarkdownUnder(string path,string root)
    {
        string full=Path.GetFullPath(path),home=Path.GetFullPath(root),relative=Path.GetRelativePath(home,full);
        if(relative=="."||relative.StartsWith("..",StringComparison.Ordinal)||Path.IsPathRooted(relative)||!full.EndsWith(".md",StringComparison.OrdinalIgnoreCase))return false;
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)return false;
        if(Directory.Exists(home)&&!home.Equals(Path.GetFullPath(_codexHome),StringComparison.OrdinalIgnoreCase)&&(File.GetAttributes(home)&FileAttributes.ReparsePoint)!=0)return false;
        for(string? directory=Path.GetDirectoryName(full);directory is not null&&!directory.Equals(home,StringComparison.OrdinalIgnoreCase);directory=Path.GetDirectoryName(directory))
            if(Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)return false;
        return true;
    }
    private MarkdownScope? MarkdownScopeFor(string path)=>MarkdownScopes().FirstOrDefault(scope=>IsMarkdownUnder(path,scope.Root));
    private bool IsReadableMarkdown(string path)=>IsGlobalMarkdownPath(path)||MarkdownScopeFor(path) is not null;
    private bool IsGlobalMarkdownPath(string path)
    {
        var scope=MarkdownScopeFor(path);if(scope is not null)return scope.Editable;
        if(registeredMarkdown.TryGetValue(Path.GetFullPath(path),out var registered))return registered.Editable&&IsMarkdownUnder(path,markdownProjectFolders.Keys.FirstOrDefault(root=>IsMarkdownUnder(path,root))??_codexHome);
        string normalized=Path.GetFullPath(path),home=Path.GetFullPath(_codexHome);
        string relative=Path.GetRelativePath(home,normalized);
        if(!string.Equals(Path.GetExtension(normalized),".md",StringComparison.OrdinalIgnoreCase)||relative.StartsWith("..",StringComparison.Ordinal)||Path.IsPathRooted(relative))return false;
        if(File.Exists(normalized)&&(File.GetAttributes(normalized)&FileAttributes.ReparsePoint)!=0)return false;
        for(string? directory=Path.GetDirectoryName(normalized);directory is not null&&!directory.Equals(home,StringComparison.OrdinalIgnoreCase);directory=Path.GetDirectoryName(directory))
            if(Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)return false;
        var parts=relative.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        return parts.Length==1;
    }
    private GlobalMarkdownEntry DescribeGlobalMarkdown(string path)
    {
        var scope=MarkdownScopeFor(path);
        if(scope is not null){
            string name=scope.Source=="共享技能"?".agents/skills/"+Path.GetRelativePath(scope.Root,path):Path.GetRelativePath(_codexHome,path);
            return new(name,Path.GetFullPath(path),scope.Editable?"个人 Markdown 文件":"宿主管理的只读文档",scope.Editable?"保存原文；是否读取取决于技能、角色配置或显式引用。":"只读参考；请通过技能或插件的维护入口更新。",scope.Source,scope.Editable);
        }
        if(!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),Path.GetFullPath(_codexHome),StringComparison.OrdinalIgnoreCase)&&registeredMarkdown.TryGetValue(Path.GetFullPath(path),out var registered))return registered;
        string relative=Path.GetRelativePath(_codexHome,path);
        string kind=relative.Equals("AGENTS.md",StringComparison.OrdinalIgnoreCase)?"默认全局指令":relative.Equals("AGENTS.override.md",StringComparison.OrdinalIgnoreCase)?"优先全局指令":"补充 Markdown 文件";
        string effect=kind switch{
            "默认全局指令"=>"新会话读取；存在非空 AGENTS.override.md 时优先读取覆盖文件。",
            "优先全局指令"=>"非空时在新会话中优先于全局 AGENTS.md；已有聊天保持已加载指令。",
            _=>"仅保存原文；此文件是否加载取决于显式引用或你的使用流程。"};
        return new(relative,Path.GetFullPath(path),kind,effect);
    }
    private void LoadGlobalMarkdown(WorkspaceSnapshot snapshot)
    {
        try{var saved=new AppPreferencesStore(Path.Combine(_stateHome,"preferences.json")).Load()["markdownFolders"] as System.Text.Json.Nodes.JsonArray;
            if(saved is not null)foreach(var item in saved.Take(16)){string? folder=item?.GetValue<string>();if(!string.IsNullOrWhiteSpace(folder)&&Directory.Exists(folder))markdownProjectFolders.TryAdd(Path.GetFullPath(folder),0);}
        }catch(Exception error) when(error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException){snapshot.Warnings.Add("已添加的项目指令目录暂时无法读取。");}
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Path.Combine(_codexHome,"AGENTS.md"),Path.Combine(_codexHome,"AGENTS.override.md")};
        foreach(string file in Directory.EnumerateFiles(_codexHome,"*",SearchOption.TopDirectoryOnly).Where(p=>p.EndsWith(".md",StringComparison.OrdinalIgnoreCase)))paths.Add(file);
        foreach(var scope in MarkdownScopes()){
            if(!Directory.Exists(scope.Root))continue;
            foreach(string file in ScanMarkdownFiles(scope.Root,false,scope.Limit,snapshot.Warnings))paths.Add(file);
        }
        foreach(string file in paths.OrderBy(p=>p.Equals(snapshot.AgentsPath,StringComparison.OrdinalIgnoreCase)?0:Path.GetFileName(p).Equals("AGENTS.override.md",StringComparison.OrdinalIgnoreCase)?1:2).ThenBy(p=>p,StringComparer.OrdinalIgnoreCase)){
            if(!IsReadableMarkdown(file))continue;
            try{AddHash(snapshot,file);snapshot.GlobalMarkdownFiles.Add(DescribeGlobalMarkdown(file));}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException){snapshot.Warnings.Add($"无法读取全局 Markdown 文件：{Path.GetFileName(file)}。");}
        }
        foreach(string folder in markdownProjectFolders.Keys)RegisterMarkdownFolder(snapshot,folder);
    }
    private static readonly HashSet<string> MarkdownScanSkip=new([".git",".codex",".system-cache","node_modules","bin","obj","sessions","archived_sessions","secrets","worktrees","logs","plans","release","verification","work","dist","target",".venv","venv"],StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> ScanMarkdownFiles(string root,bool instructionNamesOnly,int limit,List<string> warnings)
    {
        var result=new List<string>();var directories=new Stack<string>();directories.Push(Path.GetFullPath(root));int files=0,visited=0;
        while(directories.Count>0&&visited++<1500){
            string directory=directories.Pop();
            try{
                if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)continue;
                foreach(string file in Directory.EnumerateFiles(directory,"*",SearchOption.TopDirectoryOnly)){
                    string name=Path.GetFileName(file);
                    if(!name.EndsWith(".md",StringComparison.OrdinalIgnoreCase)||(instructionNamesOnly&&!name.Equals("AGENTS.md",StringComparison.OrdinalIgnoreCase)&&!name.Equals("AGENTS.override.md",StringComparison.OrdinalIgnoreCase)&&!name.Equals("AGENT.md",StringComparison.OrdinalIgnoreCase)))continue;
                    if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)continue;
                    if(files++>=limit){warnings.Add($"{root} 的 Markdown 文件超过 {limit} 份，扫描已停止。");return result;}
                    result.Add(file);
                }
                foreach(string child in Directory.EnumerateDirectories(directory))if(!MarkdownScanSkip.Contains(Path.GetFileName(child)))directories.Push(child);
            }catch(Exception error) when(error is IOException or UnauthorizedAccessException){warnings.Add($"无法扫描 Markdown 目录：{directory}。");}
        }
        if(directories.Count>0)warnings.Add($"{root} 的子目录过多，扫描已停止。");
        return result;
    }
    public IReadOnlyList<GlobalMarkdownEntry> RegisterMarkdownFolder(WorkspaceSnapshot snapshot,string directory)
    {
        string root=Path.GetFullPath(directory);
        if(!Directory.Exists(root)||Directory.GetParent(root) is null||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("请选择存在的具体项目文件夹，不支持磁盘根部或目录链接。");
        markdownProjectFolders.TryAdd(root,0);var entries=new List<GlobalMarkdownEntry>();
        foreach(string file in ScanMarkdownFiles(root,true,300,snapshot.Warnings)){
            if(!IsMarkdownUnder(file,root))continue;
            var scope=MarkdownScopeFor(file);if(scope is not null&&!scope.Editable)continue;
            string full=Path.GetFullPath(file);
            var entry=new GlobalMarkdownEntry(Path.GetFileName(root)+"/"+Path.GetRelativePath(root,file),full,"项目级指令","仅作用于对应项目或子目录；不会替换全局指令。AGENT.md 需要明确配置或引用。","项目指令");
            registeredMarkdown[full]=entry;
            if(!snapshot.FileHashes.ContainsKey(NormalizePath(full)))AddHash(snapshot,full);
            if(!snapshot.GlobalMarkdownFiles.Any(e=>e.Path.Equals(full,StringComparison.OrdinalIgnoreCase)))snapshot.GlobalMarkdownFiles.Add(entry);
            entries.Add(entry);
        }
        return entries;
    }
    public string ReadGlobalMarkdown(WorkspaceSnapshot snapshot,string path)
    {
        if(!IsReadableMarkdown(path))throw new UnauthorizedAccessException("请选择已发现的全局、技能、角色或已添加项目中的 Markdown 文件。");
        if(File.Exists(path)&&new FileInfo(path).Length>2*1024*1024)throw new IOException("Markdown 文件超过 2 MiB，请使用外部编辑器处理。");
        return ReadDocument(snapshot,path);
    }
    public ChangePlan PlanGlobalMarkdown(WorkspaceSnapshot snapshot,string path,string content)
    {
        if(!IsGlobalMarkdownPath(path))return new(){Errors=["此文件为只读文档，或不属于已添加的 Markdown 编辑范围。"]};
        if(System.Text.Encoding.UTF8.GetByteCount(content)>2*1024*1024)return new(){Errors=["Markdown 内容超过 2 MiB，请使用外部编辑器处理。"]};
        var entry=DescribeGlobalMarkdown(path);
        var plan=PlanText(snapshot,path,content,"编辑全局 Markdown · "+entry.Name);
        for(int i=0;i<plan.Files.Count;i++)plan.Files[i]=plan.Files[i] with{Effect=entry.Effect};
        return plan;
    }
    public string NewGlobalMarkdownPath(string fileName,string folder="")
    {
        if(folder is not ("" or "prompts" or "instructions"))throw new ArgumentException("请选择根目录、prompts 或 instructions。");
        if(string.IsNullOrWhiteSpace(fileName)||fileName.Length>100||fileName!=fileName.Trim()||fileName.IndexOfAny(Path.GetInvalidFileNameChars())>=0||Path.GetFileName(fileName)!=fileName||!fileName.EndsWith(".md",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("文件名应以 .md 结尾，不包含路径、无效字符或首尾空格。");
        string stem=fileName.Split('.')[0];
        if(stem.Length==0||System.Text.RegularExpressions.Regex.IsMatch(stem,"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new ArgumentException("请使用普通 Markdown 文件名，避开 Windows 保留名称。");
        string path=Path.Combine(_codexHome,folder,fileName);
        if(!IsGlobalMarkdownPath(path))throw new UnauthorizedAccessException("文件位置不属于全局 Markdown 编辑范围。");
        return Path.GetFullPath(path);
    }
}
