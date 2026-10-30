using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn.Model;

namespace CodexControlCenter.Core;

public sealed partial class ConfigurationService
{
    public event EventHandler<ConfigurationProgress>? Progress;
    private const string MissingHash = "<missing>";
    private const string AppMarker = "CodexControlCenter.Configuration.v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _codexHome;
    private readonly string _stateHome;
    private readonly string _backupRoot;
    private readonly HashSet<string> _knownEditPaths = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationService(string? codexHome = null, string? stateHome = null)
    {
        _codexHome = Path.GetFullPath(ResolveCodexHome(codexHome));
        _stateHome = Path.GetFullPath(stateHome ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexControlCenter"));
        _backupRoot = Path.Combine(_stateHome, "backups");
    }

    public async Task<WorkspaceSnapshot> LoadAsync()
    {
        await Task.Yield();
        var configPath = Path.Combine(_codexHome, "config.toml");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Codex config.toml was not found.", configPath);
        }

        // Parse and fingerprint the same bytes. A second read could associate stale values
        // with a newer fingerprint, allowing a later save to overwrite an external edit.
        var configBytes = await File.ReadAllBytesAsync(configPath);
        var config = DecodeText(configBytes);
        var configTable = TomlEditor.ReadTable(config, configPath);
        var snapshot = new WorkspaceSnapshot
        {
            CodexHome = _codexHome,
            ConfigPath = configPath,
            LoadedAt = DateTimeOffset.Now
        };

        snapshot.FileHashes[NormalizePath(configPath)] = HashText(configBytes);
        var catalogPath = ResolveCatalogPath(config, configPath);
        snapshot.CatalogPath = catalogPath;
        AddHash(snapshot, catalogPath);
        var routerPath = FindRouterPath(catalogPath);
        snapshot.RouterPath = routerPath;
        AddHash(snapshot, routerPath);

        var agentsPath = Path.Combine(_codexHome, "AGENTS.md");
        snapshot.AgentsPath = agentsPath;
        var roleDirectory = Path.Combine(_codexHome, "agents");
        AddHash(snapshot, agentsPath);
        if (Directory.Exists(roleDirectory) && (File.GetAttributes(roleDirectory) & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var agentPath in Directory.EnumerateFiles(roleDirectory, "*.toml", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(agentPath) & FileAttributes.ReparsePoint) != 0)
                    continue;

                AddHash(snapshot, agentPath);
                try
                {
                    var agentText = await File.ReadAllTextAsync(agentPath);
                    var agentTable = TomlEditor.ReadTable(agentText, agentPath);
                    var name = GetString(agentTable, null, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        snapshot.Warnings.Add($"Agent file {Path.GetFileName(agentPath)} has no public name.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
                {
                    snapshot.Warnings.Add($"Agent file {Path.GetFileName(agentPath)} could not be validated.");
                }
            }
        }

        var imageConfigPath = FindImageConfigPath();
        snapshot.ImageConfigPath = imageConfigPath;
        AddHash(snapshot, imageConfigPath);
        var profilesPath = Path.Combine(_codexHome, "control-center", "image-profiles.json");
        AddHash(snapshot, profilesPath);
        AddHash(snapshot, Path.Combine(_codexHome,"hooks.json"));
        AddHash(snapshot, Path.Combine(_codexHome,"control-center","subagent-policy.json"));
        AddHash(snapshot, Path.Combine(_codexHome,"control-center","runtime","subagent-policy","policy_hook.py"));

        var agentsTable = GetTable(configTable, "agents");
        snapshot.AgentModel = ReadTomlString(config, "agents", "default_subagent_model");
        snapshot.AgentEffort = ReadTomlString(config, "agents", "default_subagent_reasoning_effort");
        snapshot.AgentConcurrency = TomlEditor.TryGetInt64(config, "agents", "max_concurrent_threads_per_session", out var concurrency) ? (int)concurrency : 20;
        snapshot.MainModel = ReadTomlString(config, null, "model");
        snapshot.MainEffort = ReadTomlString(config, null, "model_reasoning_effort");
        if (string.IsNullOrWhiteSpace(snapshot.MainEffort))
        {
            snapshot.MainEffort = "high";
        }

        LoadProviders(snapshot);
        await LoadCatalogAsync(snapshot);
        LoadSkills(snapshot);
        LoadGlobalMarkdown(snapshot);
        await ValidateImagePublicFieldsAsync(snapshot);

        if (snapshot.Providers.Count == 0)
        {
            snapshot.Warnings.Add("No model providers were found in config.toml.");
        }

        ReportProgress("snapshot-read", configPath);
        foreach (var file in snapshot.FileHashes)
            CheckUnchanged(file.Key, file.Value);
        RememberEditPaths(snapshot);
        int unfinished=ListBackups().Count(b=>b.Summary.StartsWith("[未完成事务",StringComparison.Ordinal));
        if(unfinished>0)snapshot.Warnings.Add($"检测到 {unfinished} 个未完成的配置事务，请到“设置与备份”检查并恢复。");
        return snapshot;
    }
    public ChangePlan PlanDefaults(WorkspaceSnapshot s, string model, string effort, string agentModel, string agentEffort, int concurrency)
    {
        var plan = new ChangePlan();
        if (!TryFindModel(s, model, out var mainModel))
        {
            plan.Errors.Add($"Unknown or unmapped default model: {model}.");
        }
        else if (!IsValidEffort(effort) || !EffortAllowedForModel(mainModel, effort))
        {
            plan.Errors.Add($"Unsupported main reasoning effort for {model}: {effort}.");
        }

        if (!TryFindModel(s, agentModel, out var subagentModel))
        {
            plan.Errors.Add($"Unknown or unmapped subagent model: {agentModel}.");
        }
        else if (!IsValidEffort(agentEffort) || !EffortAllowedForModel(subagentModel, agentEffort))
        {
            plan.Errors.Add($"Unsupported subagent reasoning effort for {agentModel}: {agentEffort}.");
        }

        if (concurrency is < 1 or > 100)
        {
            plan.Errors.Add("Subagent concurrency must be between 1 and 100.");
        }

        if (plan.Errors.Count > 0)
        {
            return plan;
        }

        try
        {
            var content = ReadText(s.ConfigPath);
            content = TomlEditor.SetValue(content, null, "model", model, s.ConfigPath);
            content = TomlEditor.SetValue(content, null, "model_reasoning_effort", effort, s.ConfigPath);
            content = TomlEditor.SetValue(content, "agents", "default_subagent_model", agentModel, s.ConfigPath);
            content = TomlEditor.SetValue(content, "agents", "default_subagent_reasoning_effort", agentEffort, s.ConfigPath);
            content = TomlEditor.SetValue(content, "agents", "max_concurrent_threads_per_session", (long)concurrency, s.ConfigPath);
            TomlEditor.Validate(content, s.ConfigPath);
            plan.Files.Add(new FileEdit(s.ConfigPath, content, "Update default model and subagent settings", "Codex 重启后生效"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            plan.Errors.Add(SafeError("Unable to prepare config.toml", ex));
        }

        return plan;
    }

    public ChangePlan PlanProvider(WorkspaceSnapshot s, ProviderEntry provider)
    {
        var plan = new ChangePlan();
        if (provider is null || string.IsNullOrWhiteSpace(provider.Id))
        {
            plan.Errors.Add("Provider id is required.");
            return plan;
        }
        if (!Regex.IsMatch(provider.Id, "^[A-Za-z0-9_-]{1,64}$"))
        {
            plan.Errors.Add("Provider id must use 1-64 letters, digits, underscores or hyphens.");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl) ||
            !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            plan.Errors.Add("Provider base_url must be an absolute HTTP(S) URL without credentials, query, or fragment.");
        }

        var authMode = string.IsNullOrWhiteSpace(provider.AuthMode) ? "api_key" : provider.AuthMode;
        if (authMode is not ("api_key" or "chatgpt"))
        {
            plan.Errors.Add("Provider auth_mode must be api_key or chatgpt.");
        }

        if (!string.IsNullOrWhiteSpace(provider.RequestProfile) && provider.RequestProfile != "mimo")
        {
            plan.Errors.Add("Provider request_profile must be empty or mimo.");
        }

        if (!string.IsNullOrWhiteSpace(provider.KeyEnvironment) &&
            !Regex.IsMatch(provider.KeyEnvironment, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            plan.Errors.Add("Provider key_env must be a valid environment variable name.");
        }

        if (authMode == "api_key" && string.IsNullOrWhiteSpace(provider.KeyEnvironment))
        {
            plan.Errors.Add("API-key providers require key_env.");
        }

        if (authMode != "chatgpt" && !string.IsNullOrWhiteSpace(provider.ProxyUrl))
        {
            plan.Errors.Add("proxy_url is supported only for ChatGPT providers.");
        }
        if(authMode=="chatgpt" && provider.BaseUrl!="https://chatgpt.com/backend-api/codex")
        {
            plan.Errors.Add("ChatGPT authentication requires the exact official Codex endpoint.");
        }
        if(!string.IsNullOrWhiteSpace(provider.ProxyUrl) &&
           (!Uri.TryCreate(provider.ProxyUrl,UriKind.Absolute,out var proxy) ||
            proxy.Scheme is not ("http" or "https") || proxy.AbsolutePath is not ("" or "/") ||
            !string.IsNullOrEmpty(proxy.Query) || !string.IsNullOrEmpty(proxy.Fragment)))
        {
            plan.Errors.Add("ChatGPT proxy must be an HTTP(S) URL without path, query or fragment.");
        }

        if (plan.Errors.Count > 0)
        {
            return plan;
        }

        try
        {
            if (!File.Exists(s.RouterPath))
            {
                plan.Errors.Add("router.json was not found.");
                return plan;
            }

            var router = ParseJson(ReadText(s.RouterPath), s.RouterPath);
            if (router is not JsonObject root)
            {
                plan.Errors.Add("router.json root must be an object.");
                return plan;
            }

            if (root["providers"] is not JsonObject providers)
            {
                providers = new JsonObject();
                root["providers"] = providers;
            }

            if (!TryGetJsonObject(providers, provider.Id, out var providerNode))
            {
                providerNode = new JsonObject();
                providers[provider.Id] = providerNode;
            }

            var hadAuthMode = providerNode.ContainsKey("auth_mode");
            if(authMode=="chatgpt" && (!Path.IsPathRooted(JsonString(providerNode,"auth_file")) || string.IsNullOrWhiteSpace(JsonString(providerNode,"auth_file"))))
            {
                plan.Errors.Add("Select an existing ChatGPT provider with an auth_file reference; sign in through Codex first.");
                return plan;
            }
            SetOrClearJsonString(providerNode, "name", provider.Name);
            SetOrClearJsonString(providerNode, "base_url", provider.BaseUrl);
            SetOrClearJsonString(providerNode, "key_env", provider.KeyEnvironment);
            SetOrClearJsonString(providerNode, "request_profile", provider.RequestProfile);
            SetOrClearJsonString(providerNode, "proxy_url", provider.ProxyUrl);
            if(!string.IsNullOrWhiteSpace(provider.ProxyUrl))providerNode.Remove("proxy_env");
            if (hadAuthMode || !string.Equals(provider.AuthMode, "api_key", StringComparison.OrdinalIgnoreCase))
            {
                SetOrClearJsonString(providerNode, "auth_mode", provider.AuthMode);
            }

            plan.Files.Add(new FileEdit(s.RouterPath, router.ToJsonString(JsonOptions), $"Update router provider {provider.Name}", "路由重启后生效"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            plan.Errors.Add(SafeError("Unable to prepare provider changes", ex));
        }

        return plan;
    }
    public ChangePlan PlanModel(WorkspaceSnapshot s, ModelEntry model, string? catalogDraft = null, string? routerDraft = null)
    {
        var plan = new ChangePlan();
        if (model is null || string.IsNullOrWhiteSpace(model.Id))
        {
            plan.Errors.Add("Model slug is required.");
            return plan;
        }

        if (model.Id.Length > 512 || model.Id.Any(char.IsControl))
        {
            plan.Errors.Add("Model slug must be at most 512 characters without control characters.");
        }

        if (string.IsNullOrWhiteSpace(model.UpstreamModel) || model.UpstreamModel.Length > 512 || model.UpstreamModel.Any(char.IsControl))
        {
            plan.Errors.Add("Upstream model must be 1-512 characters without control characters.");
        }

        if (model.ContextWindow <= 0)
        {
            plan.Errors.Add("context_window must be a positive integer.");
        }
        if(model.UpdateCompactionSettings){
            if(model.EffectiveContextWindowPercent is <1 or >100)plan.Errors.Add("有效上下文预算应为 1 到 100 之间的整数百分比。");
            else if(model.AutoCompactTokenLimit is {} limit&&(limit<=0||limit>(long)((decimal)model.ContextWindow*model.EffectiveContextWindowPercent/100)))
                plan.Errors.Add("自动压缩阈值必须为正整数，且不能超过有效上下文预算。");
        }

        if (string.IsNullOrWhiteSpace(model.Provider) ||
            !s.Providers.Any(x => string.Equals(x.Id, model.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            plan.Errors.Add($"Provider does not exist: {model.Provider}.");
        }

        var requestedEfforts = ParseEfforts(model.Efforts).ToList();
        if (requestedEfforts.Any(x => !IsValidEffort(x)))
        {
            plan.Errors.Add($"Unsupported model effort in: {model.Efforts}.");
        }

        if (plan.Errors.Count > 0)
        {
            return plan;
        }

        try
        {
            if (!File.Exists(s.CatalogPath) || !File.Exists(s.RouterPath))
            {
                plan.Errors.Add("Model catalog or router.json was not found.");
                return plan;
            }

            var catalog = ParseJson(catalogDraft ?? ReadText(s.CatalogPath), s.CatalogPath);
            var router = ParseJson(routerDraft ?? ReadText(s.RouterPath), s.RouterPath);
            if(FindModelNodes(catalog).OfType<JsonObject>().Count(n=>string.Equals(FirstNonEmpty(JsonString(n,"slug"),JsonString(n,"id"),JsonString(n,"model_id"),JsonString(n,"alias")),model.Id,StringComparison.OrdinalIgnoreCase))>1){
                plan.Errors.Add("模型编号不唯一，已保留原目录："+model.Id);return plan;
            }
            bool existing=TryFindModelNode(catalog, model.Id, out var modelNode);
            long oldContext=existing?JsonInteger(modelNode,"context_window",0):0;
            long oldMaximum=existing?JsonInteger(modelNode,"max_context_window",oldContext):0;
            long oldCompact=existing?JsonInteger(modelNode,"auto_compact_token_limit",0):0;
            if (!existing)
            {
                if (FindModelArray(catalog) is not { } modelArray)
                {
                    plan.Errors.Add("Model catalog has no writable models array.");
                    return plan;
                }

                modelNode = CreateModelTemplate(modelArray);
                modelNode["visibility"] = "list";
                modelArray.Add(modelNode);
            }

            SetOrClearJsonString(modelNode, "slug", model.Id);
            SetOrClearJsonString(modelNode, "display_name", model.DisplayName);
            SetOrClearJsonString(modelNode,"provider",model.Provider);
            SetOrClearJsonString(modelNode,"upstream_model",model.UpstreamModel);
            if (model.ContextWindow > 0)
            {
                modelNode["context_window"] = model.ContextWindow;
                if(!existing||oldMaximum==oldContext||oldMaximum<model.ContextWindow)modelNode["max_context_window"]=model.ContextWindow;
            }
            if(model.UpdateCompactionSettings){
                modelNode["effective_context_window_percent"]=model.EffectiveContextWindowPercent;
                modelNode["auto_compact_token_limit"]=model.AutoCompactTokenLimit is {} compact?JsonValue.Create(compact):null;
            }else if(!existing){
                modelNode["effective_context_window_percent"]=95;
                modelNode["auto_compact_token_limit"]=(long)((decimal)model.ContextWindow*90/100);
            }else if(oldContext>0&&oldContext!=model.ContextWindow&&oldCompact>0){
                // Preserve the existing compaction ratio when a caller edits only the window.
                long budget=(long)((decimal)model.ContextWindow*Math.Clamp(JsonInteger(modelNode,"effective_context_window_percent",95),1,100)/100);
                modelNode["auto_compact_token_limit"]=Math.Max(1,Math.Min(budget,(long)((decimal)model.ContextWindow*oldCompact/oldContext)));
            }

            SetSupportedReasoningLevels(modelNode, requestedEfforts);
            var currentDefault = JsonString(modelNode, "default_reasoning_level");
            if (requestedEfforts.Count > 0 && !requestedEfforts.Contains(currentDefault, StringComparer.OrdinalIgnoreCase))
            {
                modelNode["default_reasoning_level"] = requestedEfforts[0];
            }
            SetOrClearJsonString(modelNode, "multi_agent_version", model.AgentProtocol);
            plan.Files.Add(new FileEdit(s.CatalogPath, catalog.ToJsonString(JsonOptions), $"Update model catalog entry {model.Id}", "Codex 重启后生效"));

            if (!TryUpdateRouterModel(router, model))
            {
                plan.Errors.Add($"Unable to create route for model {model.Id}.");
                return plan;
            }

            plan.Files.Add(new FileEdit(s.RouterPath, router.ToJsonString(JsonOptions), $"Update route for {model.Id}", "路由重启后生效"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            plan.Errors.Add(SafeError("Unable to prepare model changes", ex));
        }

        return plan;
    }
    public ChangePlan PlanModels(WorkspaceSnapshot snapshot,IEnumerable<ModelEntry> changes)
    {
        var plan=new ChangePlan();var models=changes.ToList();
        if(models.GroupBy(m=>m.Id,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)){
            plan.Errors.Add("多个草稿使用了相同模型编号，请为每个新模型填写唯一编号。");return plan;
        }
        string? catalog=null,router=null;
        foreach(var model in models){
            var part=PlanModel(snapshot,model,catalog,router);
            if(part.Errors.Count>0){plan.Errors.AddRange(part.Errors.Select(error=>model.Id+"："+error));continue;}
            catalog=part.Files.First(f=>f.Path==snapshot.CatalogPath).Content;
            router=part.Files.First(f=>f.Path==snapshot.RouterPath).Content;
        }
        if(plan.Errors.Count>0||models.Count==0)return plan;
        plan.Files.Add(new(snapshot.CatalogPath,catalog!,"保存 "+models.Count+" 个模型草稿","重启 Codex 后读取模型目录"));
        plan.Files.Add(new(snapshot.RouterPath,router!,"保存模型路由映射","运行同步后用于后续请求"));
        return plan;
    }
    public ChangePlan PlanModelDisplayNames(WorkspaceSnapshot snapshot,IReadOnlyDictionary<string,string> changes,string? catalogDraft=null)
    {
        var plan=new ChangePlan();
        if(changes.Count==0)return plan;
        foreach(var (id,name) in changes){
            if(string.IsNullOrWhiteSpace(name)||name.Length>256||name.Any(char.IsControl))plan.Errors.Add($"模型 {id} 的显示名称需为 1–256 个字符，且不能包含换行或控制字符。");
            if(snapshot.Models.Count(m=>m.Id==id)!=1)plan.Errors.Add("模型编号不存在或不唯一："+id);
        }
        if(plan.Errors.Count>0)return plan;
        try{
            var catalog=ParseJson(catalogDraft??ReadText(snapshot.CatalogPath),snapshot.CatalogPath);
            foreach(var (id,name) in changes){
                var nodes=FindModelNodes(catalog).OfType<JsonObject>().Where(n=>FirstNonEmpty(JsonString(n,"slug"),JsonString(n,"id"),JsonString(n,"model_id"),JsonString(n,"alias"))==id).ToList();
                if(nodes.Count!=1){plan.Errors.Add("模型编号不存在或不唯一："+id);continue;}
                nodes[0]["display_name"]=name.Trim();
            }
            if(plan.Errors.Count==0)plan.Files.Add(new(snapshot.CatalogPath,catalog.ToJsonString(JsonOptions),"调整模型显示名称与中括号标签","重启 Codex 后读取新名称；实际模型、来源、密钥与子代理分配不变。"));
        }catch(Exception error)when(error is IOException or UnauthorizedAccessException or FormatException or JsonException){plan.Errors.Add(SafeError("Unable to prepare model display names",error));}
        return plan;
    }

    public ChangePlan PlanModelVisibility(WorkspaceSnapshot snapshot, IReadOnlyDictionary<string,string> changes, string? catalogDraft = null)
    {
        var plan = new ChangePlan();
        if (changes.Count == 0) return plan;
        foreach (var change in changes)
        {
            if (change.Value is not ("list" or "hide")) plan.Errors.Add($"模型 {change.Key} 的显示设置无效，请选择显示或隐藏。");
            if (snapshot.Models.Count(model => string.Equals(model.Id,change.Key,StringComparison.Ordinal)) != 1)
                plan.Errors.Add($"模型编号不存在或不唯一：{change.Key}。请重新读取模型目录。");
        }
        if (plan.Errors.Count > 0) return plan;
        try
        {
            var catalog = ParseJson(catalogDraft ?? ReadText(snapshot.CatalogPath),snapshot.CatalogPath);
            var updates = new List<string>();
            foreach (var change in changes)
            {
                var matches = FindModelNodes(catalog).OfType<JsonObject>().Where(node =>
                    string.Equals(FirstNonEmpty(JsonString(node,"slug"),JsonString(node,"id"),JsonString(node,"model_id"),JsonString(node,"alias")),change.Key,StringComparison.Ordinal)).ToList();
                if (matches.Count != 1) { plan.Errors.Add($"模型编号不存在或不唯一：{change.Key}。请重新读取模型目录。"); continue; }
                matches[0]["visibility"] = change.Value;
                updates.Add($"{change.Key}：{(change.Value == "list" ? "显示" : "隐藏")}");
            }
            if (plan.Errors.Count == 0) plan.Files.Add(new FileEdit(snapshot.CatalogPath,catalog!.ToJsonString(JsonOptions),
                "调整 Codex 模型列表显示 · "+string.Join("；",updates),
                "重启 Codex 后重新读取模型选择列表；隐藏不删除路由，也不会更换已有聊天或默认模型。"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or JsonException)
        { plan.Errors.Add(SafeError("Unable to prepare model visibility",error)); }
        return plan;
    }
    public ChangePlan PlanText(WorkspaceSnapshot s, string path, string content, string description)
    {
        var plan = new ChangePlan();
        var fullPath = NormalizePath(path);
        if (!IsAllowedEditPath(s, fullPath, out var reason))
        {
            plan.Errors.Add(reason);
            return plan;
        }

        try
        {
            ValidatePlannedContent(fullPath, content);
            var effect = fullPath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
                ? "Codex 重启后生效"
                : fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? "按对应服务重载规则生效"
                    : "下次读取技能或指令时生效";
            plan.Files.Add(new FileEdit(fullPath, content, description, effect));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IOException)
        {
            plan.Errors.Add(SafeError("Invalid planned file", ex));
        }

        return plan;
    }

    /// <summary>Opens an editor only when its bytes still match the loaded snapshot.</summary>
    public string ReadDocument(WorkspaceSnapshot snapshot, string path)
    {
        var normalized = NormalizePath(path);
        byte[]? bytes = File.Exists(normalized) ? File.ReadAllBytes(normalized) : null;
        var actual = bytes is null ? MissingHash : HashText(bytes);
        var expected = FindHash(snapshot, normalized);
        if (expected is not null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Content hash conflict: {normalized}");
        snapshot.FileHashes[normalized] = actual;
        return bytes is null ? "" : DecodeText(bytes);
    }

    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    public async Task<ApplyResult> ApplyAsync(WorkspaceSnapshot s, ChangePlan plan)
    {
        using var lease=await AcquireTransactionLeaseAsync();
        if (plan.Errors.Count > 0)
        {
            throw new InvalidOperationException("Change plan contains errors.");
        }

        var roleDirectory = Path.GetFullPath(Path.Combine(_codexHome, "agents"));
        foreach (var edit in plan.Files)
        {
            var requestedPath = Path.GetFullPath(edit.Path);
            if (IsPathUnder(requestedPath, roleDirectory) && !IsPathFreeOfLinks(requestedPath))
            {
                throw new UnauthorizedAccessException($"Role document path contains a symbolic link: {requestedPath}");
            }
        }

        var edits = plan.Files.Select(x => x with { Path = NormalizePath(x.Path) }).ToList();
        if(edits.Any(e=>new[]{s.ConfigPath,s.CatalogPath,s.RouterPath}.Any(p=>string.Equals(NormalizePath(p),e.Path,StringComparison.OrdinalIgnoreCase)))){
            foreach(string dependency in new[]{s.CatalogPath,s.RouterPath})if(FindHash(s,NormalizePath(dependency)) is {} expected)CheckUnchanged(NormalizePath(dependency),expected);
        }
        if (edits.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != edits.Count)
        {
            throw new InvalidOperationException("Change plan contains duplicate file paths.");
        }

        foreach (var edit in edits)
        {
            if (!IsAllowedEditPath(s, edit.Path, out var reason))
            {
                throw new UnauthorizedAccessException(reason);
            }

            ValidatePlannedContent(edit.Path, edit.Content);
        }

        var records = new List<WriteRecord>();
        foreach (var edit in edits)
        {
            var exists = File.Exists(edit.Path);
            var currentHash = exists ? HashFile(edit.Path) : MissingHash;
            var expected = FindHash(s, edit.Path);
            if (expected is not null && !string.Equals(expected, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Content hash conflict: {edit.Path}");
            }

            if (expected is null && exists)
            {
                throw new InvalidOperationException($"Content hash is missing for existing file: {edit.Path}");
            }

            records.Add(new WriteRecord(edit, exists, currentHash));
        }

        Directory.CreateDirectory(_backupRoot);
        var backupDirectory = Path.Combine(_backupRoot, $"backup_{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}"[..52]);
        Directory.CreateDirectory(Path.Combine(backupDirectory, "files"));
        var manifestFiles = new List<BackupFile>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var backupName = $"{i:D4}.bak";
            var backupPath = Path.Combine(backupDirectory, "files", backupName);
            if (record.OriginalExists)
            {
                File.Copy(record.Edit.Path, backupPath, false);
                if(!string.Equals(HashFile(backupPath),record.OriginalHash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Backup conflict: {record.Edit.Path}");
            }
            CheckUnchanged(record.Edit.Path,record.OriginalHash);
            ReportProgress("backed-up",record.Edit.Path);

            manifestFiles.Add(new BackupFile
            {
                TargetPath = record.Edit.Path,
                BackupPath = Path.Combine("files", backupName),
                OriginalExists = record.OriginalExists,
                OriginalHash = record.OriginalHash,
                ResultHash = HashText(record.Edit.Content)
            });
        }

        var manifest = new BackupManifest
        {
            Marker = AppMarker,
            CreatedAtUtc = DateTime.UtcNow,
            Summary = string.Join("; ", edits.Select(x => x.Description)),
            Files = manifestFiles
        };
        await WriteJsonAsync(Path.Combine(backupDirectory, "manifest.json"), manifest);
        var journalPath = Path.Combine(backupDirectory, "journal.json");
        await WriteJsonAsync(journalPath, new JournalState { Marker = AppMarker, Phase = "prepared", Paths = edits.Select(x => x.Path).ToList() });

        var changed = new List<WriteRecord>();
        try
        {
            foreach (var record in records)
            {
                ReportProgress("before-write",record.Edit.Path);
                await WriteAtomicAsync(record.Edit.Path, record.Edit.Content,record.OriginalHash);
                changed.Add(record);
                ReportProgress("written",record.Edit.Path);
                await WriteJsonAsync(journalPath, new JournalState
                {
                    Marker = AppMarker,
                    Phase = "written",
                    Paths = changed.Select(x => x.Edit.Path).ToList()
                });
            }

            await WriteJsonAsync(journalPath, new JournalState { Marker = AppMarker, Phase = "committed", Paths = changed.Select(x => x.Edit.Path).ToList() });
        }
        catch (Exception ex)
        {
            var rollbackFailures = new List<string>();
            foreach (var record in changed.AsEnumerable().Reverse())
            {
                try
                {
                    await RestoreRecordAsync(record, backupDirectory);
                }
                catch
                {
                    rollbackFailures.Add(record.Edit.Path);
                }
            }

            var phase = rollbackFailures.Count == 0 ? "rolled-back" : "partial-rollback";
            await WriteJsonAsync(journalPath, new JournalState { Marker = AppMarker, Phase = phase, Paths = changed.Select(x => x.Edit.Path).ToList() });
            if (rollbackFailures.Count > 0)
            {
                throw new InvalidOperationException($"Apply failed and rollback was incomplete for {rollbackFailures.Count} file(s): {SafeError("write error", ex)}");
            }

            throw new InvalidOperationException($"Apply failed and was rolled back: {SafeError("write error", ex)}");
        }

        return new ApplyResult(backupDirectory, edits.Select(x => x.Effect).ToList());
    }

    public IReadOnlyList<BackupEntry> ListBackups()
    {
        if (!Directory.Exists(_backupRoot))
        {
            return [];
        }

        var result = new List<BackupEntry>();
        foreach (var directory in Directory.EnumerateDirectories(_backupRoot))
        {
            try
            {
                var manifest = ReadManifest(directory);
                if (manifest.Marker != AppMarker)
                {
                    continue;
                }

                result.Add(new BackupEntry(directory, manifest.CreatedAtUtc.ToLocalTime(), (IsIncompleteTransaction(directory)?"[未完成事务 · 可检查并恢复] ":"")+manifest.Summary));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                // Ignore unrelated or partially written directories.
            }
        }

        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task RestoreAsync(string backupDirectory)
    {
        using var lease=await AcquireTransactionLeaseAsync();
        var directory = NormalizePath(backupDirectory);
        if (string.Equals(directory, NormalizePath(_backupRoot), StringComparison.OrdinalIgnoreCase) || !IsUnder(directory, _backupRoot))
        {
            throw new InvalidOperationException("Backup directory is outside the managed backup root.");
        }

        var manifest = ReadManifest(directory);
        if (manifest.Marker != AppMarker)
        {
            throw new InvalidOperationException("Backup does not belong to Codex Control Center.");
        }

        if (manifest.Files.Select(x => NormalizePath(x.TargetPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
        {
            throw new InvalidOperationException("Backup manifest contains duplicate target paths.");
        }

        foreach (var file in manifest.Files)
        {
            var target = NormalizePath(file.TargetPath);
            if (!IsAllowedEditPath(null, target, out var reason))
            {
                throw new InvalidOperationException(reason);
            }

            var payload = NormalizePath(Path.Combine(directory, file.BackupPath));
            if (!IsUnder(payload, directory))
            {
                throw new InvalidOperationException("Backup payload escapes the backup directory.");
            }

            if (file.OriginalExists)
            {
                if (!File.Exists(payload) || !string.Equals(HashFile(payload), file.OriginalHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Backup payload hash mismatch: {target}");
                }
            }

            var exists = File.Exists(target);
            var currentHash = exists ? HashFile(target) : MissingHash;
            if (!string.Equals(currentHash, file.ResultHash, StringComparison.OrdinalIgnoreCase)&&
                !(IsIncompleteTransaction(directory)&&string.Equals(currentHash,file.OriginalHash,StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Restore conflict: {target}");
            }
        }

        var preRestore = Path.Combine(_backupRoot, $"restore_{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(preRestore, "files"));
        var preFiles = new List<BackupFile>();
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            var source = manifest.Files[i];
            var target = NormalizePath(source.TargetPath);
            var exists = File.Exists(target);
            var relative = Path.Combine("files", $"{i:D4}.bak");
            var actualHash=exists?HashFile(target):MissingHash;
            if(!string.Equals(actualHash,source.ResultHash,StringComparison.OrdinalIgnoreCase)&&!(IsIncompleteTransaction(directory)&&string.Equals(actualHash,source.OriginalHash,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException($"Restore conflict: {target}");
            if (exists)
            {
                File.Copy(target, Path.Combine(preRestore, relative), false);
                if(!string.Equals(HashFile(Path.Combine(preRestore,relative)),source.ResultHash,StringComparison.OrdinalIgnoreCase)&&!string.Equals(HashFile(Path.Combine(preRestore,relative)),source.OriginalHash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Backup conflict: {target}");
            }
            CheckUnchanged(target,actualHash);

            preFiles.Add(new BackupFile
            {
                TargetPath = target,
                BackupPath = relative,
                OriginalExists = exists,
                OriginalHash = actualHash,
                ResultHash = source.OriginalHash
            });
        }

        await WriteJsonAsync(Path.Combine(preRestore, "manifest.json"), new BackupManifest
        {
            Marker = AppMarker,
            CreatedAtUtc = DateTime.UtcNow,
            Summary = "State before restore",
            Files = preFiles
        });

        var journalPath = Path.Combine(preRestore, "journal.json");
        await WriteJsonAsync(journalPath, new JournalState { Marker = AppMarker, Phase = "restore-prepared", Paths = manifest.Files.Select(x => x.TargetPath).ToList() });
        var changed = new List<BackupFile>();
        try
        {
            foreach (var source in manifest.Files)
            {
                var target = NormalizePath(source.TargetPath);
                var previous=preFiles.Single(f=>f.TargetPath==target);
                CheckUnchanged(target,previous.OriginalHash);
                if (!source.OriginalExists)
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                else
                {
                    var payload = NormalizePath(Path.Combine(directory, source.BackupPath));
                    await WriteAtomicBytesAsync(target, await File.ReadAllBytesAsync(payload),previous.OriginalHash);
                }

                changed.Add(source);
                await WriteJsonAsync(journalPath, new JournalState { Marker = AppMarker, Phase = "restore-written", Paths = changed.Select(x => x.TargetPath).ToList() });
            }

            await WriteJsonAsync(journalPath, new JournalState { Marker = AppMarker, Phase = "restored", Paths = changed.Select(x => x.TargetPath).ToList() });
            if(IsIncompleteTransaction(directory))await WriteJsonAsync(Path.Combine(directory,"journal.json"),new JournalState{Marker=AppMarker,Phase="recovered",Paths=changed.Select(x=>x.TargetPath).ToList()});
        }
        catch (Exception ex)
        {
            var rollbackFailures = new List<string>();
            foreach (var source in changed.AsEnumerable().Reverse())
            {
                try
                {
                    var pre = preFiles.First(x => string.Equals(x.TargetPath, NormalizePath(source.TargetPath), StringComparison.OrdinalIgnoreCase));
                    await RestoreRawAsync(pre, preRestore);
                }
                catch
                {
                    rollbackFailures.Add(source.TargetPath);
                }
            }

            var phase = rollbackFailures.Count == 0 ? "rolled-back" : "partial-rollback";
            await WriteJsonAsync(journalPath, new JournalState { Marker = AppMarker, Phase = phase, Paths = changed.Select(x => x.TargetPath).ToList() });
            throw new InvalidOperationException($"Restore failed and rollback was {(rollbackFailures.Count == 0 ? "complete" : "incomplete")}: {SafeError("restore error", ex)}");
        }
    }

    private async Task RestoreRecordAsync(WriteRecord record, string backupDirectory)
    {
        var manifest = ReadManifest(backupDirectory);
        var file = manifest.Files.First(x => string.Equals(NormalizePath(x.TargetPath), NormalizePath(record.Edit.Path), StringComparison.OrdinalIgnoreCase));
        await RestoreRawAsync(file, backupDirectory);
    }

    private static async Task RestoreRawAsync(BackupFile file, string backupDirectory)
    {
        var target = NormalizePath(file.TargetPath);
        CheckUnchanged(target,file.ResultHash);
        if (!file.OriginalExists)
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            return;
        }

        var payload = NormalizePath(Path.Combine(backupDirectory, file.BackupPath));
        if (!IsUnder(payload, backupDirectory))
        {
            throw new InvalidOperationException("Backup payload escapes the backup directory.");
        }

        if (!File.Exists(payload) || !string.Equals(HashFile(payload), file.OriginalHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Backup payload hash mismatch: {target}");
        }

        await WriteAtomicBytesAsync(target, await File.ReadAllBytesAsync(payload),file.ResultHash);
    }

    private void LoadProviders(WorkspaceSnapshot snapshot)
    {
        if (!File.Exists(snapshot.RouterPath))
        {
            return;
        }

        try
        {
            var router = ParseJson(ReadText(snapshot.RouterPath), snapshot.RouterPath);
            if (router is not JsonObject root || root["providers"] is not JsonObject providers)
            {
                return;
            }

            foreach (var pair in providers)
            {
                if (pair.Value is not JsonObject table)
                {
                    continue;
                }

                var keyEnvironment = JsonString(table, "key_env");
                var authMode = JsonString(table, "auth_mode");
                if (string.IsNullOrWhiteSpace(authMode))
                {
                    authMode = string.IsNullOrWhiteSpace(keyEnvironment) ? "未验证" : "api_key";
                }

                snapshot.Providers.Add(new ProviderEntry
                {
                    Id = pair.Key,
                    Name = FirstNonEmpty(JsonString(table, "name"), pair.Key),
                    BaseUrl = JsonString(table, "base_url"),
                    AuthMode = authMode,
                    KeyEnvironment = keyEnvironment,
                    RequestProfile = JsonString(table, "request_profile"),
                    ProxyUrl = JsonString(table, "proxy_url"),
                    CredentialStatus = string.IsNullOrWhiteSpace(keyEnvironment) && string.IsNullOrWhiteSpace(JsonString(table, "auth_file")) ? "未验证" : "引用已配置"
                });
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException)
        {
            snapshot.Warnings.Add("router.json providers could not be read.");
        }
    }
    private async Task LoadCatalogAsync(WorkspaceSnapshot snapshot)
    {
        if (!File.Exists(snapshot.CatalogPath))
        {
            snapshot.Warnings.Add("Model catalog file was not found.");
            return;
        }

        JsonNode? catalog;
        JsonNode? router = null;
        try
        {
            catalog = ParseJson(await File.ReadAllTextAsync(snapshot.CatalogPath), snapshot.CatalogPath);
            if (File.Exists(snapshot.RouterPath))
            {
                router = ParseJson(await File.ReadAllTextAsync(snapshot.RouterPath), snapshot.RouterPath);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException)
        {
            snapshot.Warnings.Add("Model catalog or router JSON could not be parsed.");
            return;
        }

        foreach (var node in FindModelNodes(catalog))
        {
            snapshot.Models.Add(ReadModel(node, router));
        }

        foreach (var model in snapshot.Models.Where(x => !x.Mapped))
        {
            snapshot.Warnings.Add($"Model {model.Id} has no router mapping.");
        }
    }
    private void LoadSkills(WorkspaceSnapshot snapshot)
    {
        var roots = new[] { Path.Combine(_codexHome, "skills"), Path.Combine(Path.GetDirectoryName(_codexHome) ?? _codexHome, ".agents", "skills") };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var skillPath in Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories))
            {
                if (skillPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => string.Equals(x, ".system", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var name = Path.GetFileName(Path.GetDirectoryName(skillPath) ?? skillPath);
                var description = ReadSkillDescription(skillPath);
                AddHash(snapshot, skillPath);
                snapshot.Skills.Add(new SkillEntry
                {
                    Name = name,
                    Description = description,
                    Path = skillPath,
                    Source = root.Contains(".agents", StringComparison.OrdinalIgnoreCase) ? ".agents/skills" : ".codex/skills",
                    Editable = !skillPath.Contains("plugins", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
    }

    private async Task ValidateImagePublicFieldsAsync(WorkspaceSnapshot snapshot)
    {
        if (!File.Exists(snapshot.ImageConfigPath))
        {
            return;
        }

        try
        {
            _ = ParseJson(await File.ReadAllTextAsync(snapshot.ImageConfigPath), snapshot.ImageConfigPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException)
        {
            snapshot.Warnings.Add("Image config public fields could not be read.");
        }
    }
    private bool IsAllowedEditPath(WorkspaceSnapshot? s, string path, out string reason)
    {
        reason = "";
        var normalized = NormalizePath(path);
        if(string.Equals(normalized,NormalizePath(Path.Combine(_codexHome,"control-center","runtime","subagent-policy","policy_hook.py")),StringComparison.OrdinalIgnoreCase)){
            if(IsPathFreeOfLinks(normalized))return true;
            reason="Managed hook path contains a symbolic link; original files were preserved.";return false;
        }
        if (IsGlobalMarkdownPath(normalized)) return true;
        var known = _knownEditPaths.Concat(s is null
            ? Array.Empty<string>()
            : new[]
            {
                NormalizePath(s.ConfigPath), NormalizePath(s.CatalogPath), NormalizePath(s.RouterPath),
                NormalizePath(s.ImageConfigPath), NormalizePath(s.AgentsPath),
                NormalizePath(Path.Combine(_codexHome, "control-center", "image-profiles.json")),
                NormalizePath(Path.Combine(_codexHome,"hooks.json")),
                NormalizePath(Path.Combine(_codexHome,"control-center","subagent-policy.json"))
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (known.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var roleDirectory = NormalizePath(Path.Combine(_codexHome, "agents"));
        if (IsUnder(normalized, roleDirectory) && normalized.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) && File.Exists(normalized))
        {
            return true;
        }

        if (string.Equals(Path.GetFileName(normalized), "AGENTS.md", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalized, NormalizePath(Path.Combine(_codexHome, "AGENTS.md")), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var secretsDirectory = NormalizePath(Path.Combine(_codexHome, "secrets"));
        if (IsUnder(normalized, secretsDirectory) &&
            Regex.IsMatch(Path.GetFileName(normalized), "^control-center-[A-Za-z0-9_-]{1,80}\\.dpapi$"))
        {
            return true;
        }

        var skillRoot = NormalizePath(Path.Combine(_codexHome, "skills"));
        var agentsSkillRoot = NormalizePath(Path.Combine(Path.GetDirectoryName(_codexHome) ?? _codexHome, ".agents", "skills"));
        if ((IsUnder(normalized, skillRoot) || IsUnder(normalized, agentsSkillRoot)) &&
            string.Equals(Path.GetFileName(normalized), "SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            var root = IsUnder(normalized, skillRoot) ? skillRoot : agentsSkillRoot;
            var relative = Path.GetRelativePath(root, normalized);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length == 2 && Regex.IsMatch(parts[0], "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$") &&
                !parts[0].Equals(".system", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        reason = $"Path is outside the editable configuration scope: {normalized}";
        return false;
    }


    private void RememberEditPaths(WorkspaceSnapshot snapshot)
    {
        _knownEditPaths.Add(NormalizePath(snapshot.ConfigPath));
        _knownEditPaths.Add(NormalizePath(snapshot.CatalogPath));
        _knownEditPaths.Add(NormalizePath(snapshot.RouterPath));
        _knownEditPaths.Add(NormalizePath(snapshot.ImageConfigPath));
        _knownEditPaths.Add(NormalizePath(snapshot.AgentsPath));
        _knownEditPaths.Add(NormalizePath(Path.Combine(_codexHome, "control-center", "image-profiles.json")));
        _knownEditPaths.Add(NormalizePath(Path.Combine(_codexHome,"hooks.json")));
        _knownEditPaths.Add(NormalizePath(Path.Combine(_codexHome,"control-center","subagent-policy.json")));
    }

    private static void ValidatePlannedContent(string path, string content)
    {
        if (content.Contains('\0'))
        {
            throw new FormatException("Planned content contains a NUL character.");
        }

        if (path.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = content.Trim();
            if (encoded.Length == 0)
            {
                throw new FormatException("DPAPI payload must not be empty.");
            }

            try
            {
                _ = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw new FormatException("DPAPI payload must be valid base64.");
            }

            return;
        }

        if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            TomlEditor.Validate(content, path);
        }
        else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            ParseJson(content, path);
        }
    }

    private static string ResolveCodexHome(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var environment = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return environment;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    private static string ResolveCatalogPath(string config, string configPath)
    {
        if (TomlEditor.TryGetString(config, null, "model_catalog_json", out var configured) &&
            !string.IsNullOrWhiteSpace(configured))
        {
            var candidate = Path.IsPathRooted(configured) ? configured : Path.Combine(Path.GetDirectoryName(configPath) ?? "", configured);
            return NormalizePath(candidate);
        }

        var home = Path.GetDirectoryName(configPath) ?? "";
        var candidates = new[]
        {
            Path.Combine(home, "model-router-sandbox", "combined-models.json"),
            Path.Combine(home, "combined-models.json"),
            Path.Combine(home, "model_catalog.json"),
            Path.Combine(home, "catalog.json")
        };
        return NormalizePath(candidates.FirstOrDefault(File.Exists) ?? candidates[0]);
    }
    private string FindImageConfigPath()
    {
        var candidates = new[]
        {
            Path.Combine(_codexHome, "skills", "i2m-imagegen", "config.json"),
            Path.Combine(_codexHome, "skills", "i2m", "config.json"),
            Path.Combine(_codexHome, "control-center", "i2m-config.json")
        };
        return NormalizePath(candidates.FirstOrDefault(File.Exists) ?? candidates[0]);
    }

    private static string FindRouterPath(string catalogPath)
    {
        var directory = Path.GetDirectoryName(catalogPath) ?? "";
        var candidates = new[]
        {
            Path.Combine(directory, "router.json"),
            Path.Combine(directory, "model-router.json"),
            Path.Combine(Path.GetDirectoryName(directory) ?? directory, "router.json"),
            Path.Combine(directory, "model-router-sandbox", "router.json")
        };
        return NormalizePath(candidates.FirstOrDefault(File.Exists) ?? candidates[0]);
    }

    private static bool TryFindModel(WorkspaceSnapshot s, string id, out ModelEntry model)
    {
        model = s.Models.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))!;
        return model is not null && model.Mapped;
    }

    private static bool EffortAllowedForModel(ModelEntry model, string effort)
    {
        var declared = ParseEfforts(model.Efforts).ToList();
        return declared.Count == 0 || declared.Contains(effort, StringComparer.OrdinalIgnoreCase);
    }
    private static bool IsValidEffort(string value)
    {
        return value is "none" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra";
    }

    private static IEnumerable<string> ParseEfforts(string value)
    {
        return value.Split(new[] { ',', ';', '|', '/', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    private static ModelEntry ReadModel(JsonNode node, JsonNode? router)
    {
        var obj = node as JsonObject ?? new JsonObject();
        var id = FirstNonEmpty(JsonString(obj, "slug"), JsonString(obj, "id"), JsonString(obj, "model_id"), JsonString(obj, "alias"), JsonString(obj, "model"));
        var route = FindRouteNode(router is JsonObject root && root["routes"] is JsonNode routes ? routes : router, id) as JsonObject;
        var efforts = ReadSupportedEfforts(obj);
        return new ModelEntry
        {
            Id = id,
            DisplayName = FirstNonEmpty(JsonString(obj, "display_name"), JsonString(obj, "displayName"), JsonString(obj, "name"), id),
            Provider = FirstNonEmpty(route is null ? "" : JsonString(route, "provider"), JsonString(obj, "provider"), JsonString(obj, "provider_id")),
            UpstreamModel = FirstNonEmpty(route is null ? "" : JsonString(route, "model"), route is null ? "" : JsonString(route, "upstream_model"), JsonString(obj, "upstream_model"), JsonString(obj, "upstreamModel"), JsonString(obj, "model"), JsonString(obj, "target")),
            ContextWindow = JsonInteger(obj, "context_window", JsonInteger(obj, "contextWindow", 0)),
            MaxContextWindow = JsonInteger(obj,"max_context_window",JsonInteger(obj,"context_window",0)),
            AutoCompactTokenLimit = obj["auto_compact_token_limit"] is null?null:JsonInteger(obj,"auto_compact_token_limit",0),
            EffectiveContextWindowPercent = (int)Math.Clamp(JsonInteger(obj,"effective_context_window_percent",95),int.MinValue,int.MaxValue),
            Efforts = efforts,
            AgentProtocol = FirstNonEmpty(JsonString(obj, "multi_agent_version"), JsonString(obj, "agent_protocol"), JsonString(obj, "agentProtocol")),
            Visibility = JsonString(obj,"visibility"),
            Mapped = route is not null
        };
    }

    private static string ReadSupportedEfforts(JsonObject obj)
    {
        if (obj["supported_reasoning_levels"] is JsonArray levels)
        {
            return string.Join(",", levels.OfType<JsonObject>().Select(x => JsonString(x, "effort")).Where(x => x.Length > 0));
        }

        return FirstNonEmpty(JsonString(obj, "efforts"), JsonString(obj, "reasoning_efforts"));
    }
    private static bool TryFindModelNode(JsonNode? root, string id, out JsonObject node)
    {
        node = null!;
        foreach (var candidate in FindModelNodes(root))
        {
            if (candidate is JsonObject obj && string.Equals(FirstNonEmpty(JsonString(obj, "slug"), JsonString(obj, "id"), JsonString(obj, "model_id"), JsonString(obj, "alias")), id, StringComparison.OrdinalIgnoreCase))
            {
                node = obj;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<JsonNode> FindModelNodes(JsonNode? root)
    {
        if (root is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject candidateObj && LooksLikeModel(candidateObj))
                {
                    yield return candidateObj;
                }
            }

            yield break;
        }

        if (root is not JsonObject obj)
        {
            yield break;
        }

        foreach (var property in obj.ToList())
        {
            if (property.Value is JsonArray nested && property.Key is "models" or "catalog" or "entries" or "items")
            {
                foreach (var item in nested)
                {
                    if (item is JsonObject candidate && LooksLikeModel(candidate))
                    {
                        yield return candidate;
                    }
                }
            }
            else if (property.Value is JsonObject nestedObject && property.Key is "models" or "catalog")
            {
                foreach (var item in nestedObject)
                {
                    if (item.Value is JsonObject candidate)
                    {
                        candidate["id"] ??= item.Key;
                        if (LooksLikeModel(candidate))
                        {
                            yield return candidate;
                        }
                    }
                }
            }
        }
    }

    private static JsonArray? FindModelArray(JsonNode? root)
    {
        if (root is JsonArray array)
        {
            return array;
        }

        return (root as JsonObject)?["models"] as JsonArray;
    }

    private static bool LooksLikeModel(JsonObject obj)
    {
        return (obj.ContainsKey("slug") || obj.ContainsKey("id") || obj.ContainsKey("model_id") || obj.ContainsKey("alias")) &&
               (obj.ContainsKey("provider") || obj.ContainsKey("provider_id") || obj.ContainsKey("upstream_model") ||
                obj.ContainsKey("context_window") || obj.ContainsKey("efforts") || obj.ContainsKey("reasoning_efforts") ||
                obj.ContainsKey("display_name") || obj.ContainsKey("name"));
    }

    private static bool HasRoute(JsonNode? router, string id)
    {
        return router is not null && (FindRouteNode(router, id) is not null || (router is JsonObject obj && obj.ContainsKey(id)));
    }

    private static JsonNode? FindRouteNode(JsonNode? root, string id)
    {
        if (root is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (string.Equals(property.Key, id, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }

                if (property.Value is JsonObject child &&
                    string.Equals(FirstNonEmpty(JsonString(child, "id"), JsonString(child, "alias"), JsonString(child, "model_alias")), id, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                if (property.Value is JsonArray array)
                {
                    foreach (var item in array)
                    {
                        if (item is JsonObject candidate &&
                            string.Equals(FirstNonEmpty(JsonString(candidate, "id"), JsonString(candidate, "alias"), JsonString(candidate, "model_alias")), id, StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool TryUpdateRouterModel(JsonNode router, ModelEntry model)
    {
        if (router is not JsonObject root)
        {
            return false;
        }

        if (root["routes"] is not JsonObject routes)
        {
            routes = new JsonObject();
            root["routes"] = routes;
        }

        if (!TryGetJsonObject(routes, model.Id, out var route))
        {
            route = new JsonObject();
            routes[model.Id] = route;
        }

        SetOrClearJsonString(route, "provider", model.Provider);
        SetOrClearJsonString(route, "model", model.UpstreamModel);
        SetOrClearJsonString(route, "upstream_model", model.UpstreamModel);
        SetOrClearJsonString(route, "target", model.UpstreamModel);
        return true;
    }

    private static bool TryGetJsonObject(JsonObject parent, string key, out JsonObject value)
    {
        value = null!;
        if (parent.TryGetPropertyValue(key, out var node) && node is JsonObject obj)
        {
            value = obj;
            return true;
        }

        return false;
    }
    private static bool TryFindProviderNode(JsonNode? root, string id, out JsonObject node)
    {
        node = null!;
        if (root is not JsonObject obj)
        {
            return false;
        }

        if (obj["providers"] is JsonObject providers && providers[id] is JsonObject direct)
        {
            node = direct;
            return true;
        }

        foreach (var property in obj)
        {
            if (property.Value is JsonObject candidate &&
                string.Equals(FirstNonEmpty(JsonString(candidate, "id"), JsonString(candidate, "provider_id"), property.Key), id, StringComparison.OrdinalIgnoreCase))
            {
                node = candidate;
                return true;
            }
        }

        return false;
    }

    private static JsonObject CreateModelTemplate(JsonArray models)
    {
        // A new service must not inherit another model's identity, private instructions or modalities.
        return new JsonObject{
            ["description"]="Custom model configured in Codex Control Center.",
            ["supported_in_api"]=true,["visibility"]="list",["priority"]=100,
            ["shell_type"]="unified_exec",["apply_patch_tool_type"]="freeform",
            ["base_instructions"]="You are Codex, a coding assistant working with the user in a shared workspace.",
            ["supports_reasoning_summaries"]=true,["default_reasoning_summary"]="none",
            ["support_verbosity"]=false,["supports_parallel_tool_calls"]=true,["prefer_websockets"]=false,
            ["experimental_supported_tools"]=new JsonArray(),["input_modalities"]=new JsonArray("text"),
            ["truncation_policy"]=new JsonObject{["mode"]="tokens",["limit"]=10000}
        };
    }

    private static void SetSupportedReasoningLevels(JsonObject node, IReadOnlyList<string> efforts)
    {
        if (efforts.Count == 0)
        {
            SetOrClearJsonString(node, "supported_reasoning_levels", null);
            return;
        }

        var existing = node["supported_reasoning_levels"] as JsonArray;
        var template = existing?.OfType<JsonObject>().FirstOrDefault()?.DeepClone() as JsonObject;
        var updated = new JsonArray();
        foreach (var effort in efforts)
        {
            var current = existing?.OfType<JsonObject>().FirstOrDefault(x => string.Equals(JsonString(x, "effort"), effort, StringComparison.OrdinalIgnoreCase))?.DeepClone() as JsonObject
                ?? template?.DeepClone() as JsonObject
                ?? new JsonObject();
            current["effort"] = effort;
            if (!current.ContainsKey("description"))
            {
                current["description"] = effort switch
                {
                    "none" => "No additional reasoning effort",
                    _ => $"{effort} reasoning effort"
                };
            }

            updated.Add(current);
        }

        node["supported_reasoning_levels"] = updated;
    }

    private static void SetOrClearJsonString(JsonObject obj, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            obj.Remove(key);
            return;
        }

        obj[key] = value;
    }
    private static void SetJsonString(JsonObject obj, string key, string? value)
    {
        SetOrClearJsonString(obj, key, value);
    }
    private static string ReadSkillDescription(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, @"(?im)^\s*description:\s*(.+?)\s*$");
            if (match.Success)
            {
                return match.Groups[1].Value.Trim('"', '\'');
            }

            var heading = text.Split('\n').FirstOrDefault(x => x.StartsWith("#", StringComparison.Ordinal));
            return heading?.TrimStart('#').Trim() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    private static string GetString(TomlTable table, string? parent, string key)
    {
        if (parent is not null)
        {
            table = GetTable(table, parent);
        }

        return table.TryGetValue(key, out var value) && value is string text ? text : "";
    }

    private static long GetInteger(TomlTable table, string? parent, string key, long fallback)
    {
        if (parent is not null)
        {
            table = GetTable(table, parent);
        }

        return table.TryGetValue(key, out var value) && value is long number ? number : fallback;
    }

    private static bool GetBoolean(TomlTable table, string? parent, string key, out bool value)
    {
        if (parent is not null)
        {
            table = GetTable(table, parent);
        }

        table.TryGetValue(key, out var raw);
        value = raw is bool flag && flag;
        return raw is bool;
    }

    private static TomlTable GetTable(TomlTable root, string name)
    {
        return root.TryGetValue(name, out var value) && value is TomlTable table ? table : new TomlTable();
    }

    private static JsonNode ParseJson(string text, string path)
    {
        return JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = false }, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new FormatException($"{path}: JSON root is null.");
    }

    private static string JsonString(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var value) && value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : "";
    }

    private static long JsonInteger(JsonObject obj, string key, long fallback)
    {
        return obj.TryGetPropertyValue(key, out var value) && value is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var number) ? number : fallback;
    }

    private static bool GetBoolean(JsonNode node, string key)
    {
        return node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) && value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var flag) && flag;
    }

    private static string ReadText(string path) => File.ReadAllText(path);
    private static string ReadTomlString(string content, string? table, string key)
    {
        return TomlEditor.TryGetString(content, table, key, out var value) ? value : "";
    }

    private static async Task WriteAtomicBytesAsync(string path, byte[] content,string expectedHash)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".codex-control-center-{Guid.NewGuid():N}.tmp");
        try{await File.WriteAllBytesAsync(temporary, content);CheckUnchanged(path,expectedHash);
            if(File.Exists(path)){try{File.Replace(temporary,path,null);}catch(PlatformNotSupportedException){CheckUnchanged(path,expectedHash);File.Move(temporary,path,true);}}
            else File.Move(temporary,path);
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static Task WriteAtomicAsync(string path, string content,string expectedHash)
    {
        return WriteAtomicBytesAsync(path,Encoding.UTF8.GetBytes(content),expectedHash);
    }
    private static void CheckUnchanged(string path,string expected)
    {
        string actual=File.Exists(path)?HashFile(path):MissingHash;
        if(Directory.Exists(path)||!string.Equals(actual,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"File changed during transaction: {path}");
    }
    private static bool IsIncompleteTransaction(string directory)
    {
        string file=Path.Combine(directory,"journal.json");if(!File.Exists(file))return false;
        var journal=JsonSerializer.Deserialize<JournalState>(File.ReadAllText(file),JsonOptions);
        return journal?.Marker==AppMarker&&journal.Phase is "prepared" or "written" or "restore-prepared" or "restore-written" or "partial-rollback";
    }
    private async Task<FileStream> AcquireTransactionLeaseAsync()
    {
        Directory.CreateDirectory(_stateHome);var started=DateTime.UtcNow;
        while(true){try{return new FileStream(Path.Combine(_stateHome,"configuration.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException)when(DateTime.UtcNow-started<TimeSpan.FromSeconds(3)){await Task.Delay(50);}}
    }
    private void ReportProgress(string stage,string path){try{Progress?.Invoke(this,new(stage,path));}catch{/* UI notifications never decide transaction success. */}}

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static BackupManifest ReadManifest(string directory)
    {
        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Backup manifest is missing.", path);
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new FormatException("Backup manifest is empty.");
        if (manifest.Marker != AppMarker)
        {
            throw new FormatException("Backup marker is invalid.");
        }

        if (manifest.Summary is null || manifest.Files is null || manifest.Files.Any(file => file is null ||
            string.IsNullOrWhiteSpace(file.TargetPath) || string.IsNullOrWhiteSpace(file.BackupPath) ||
            string.IsNullOrWhiteSpace(file.OriginalHash) || string.IsNullOrWhiteSpace(file.ResultHash)))
            throw new FormatException("Backup manifest contains invalid file entries.");

        return manifest;
    }

    private static string FindHash(WorkspaceSnapshot snapshot, string path)
    {
        return snapshot.FileHashes.FirstOrDefault(x => string.Equals(NormalizePath(x.Key), path, StringComparison.OrdinalIgnoreCase)).Value!;
    }

    private static void AddHash(WorkspaceSnapshot snapshot, string path)
    {
        var normalized = NormalizePath(path);
        snapshot.FileHashes[normalized] = File.Exists(normalized) ? HashFile(normalized) : MissingHash;
    }

    private static string HashFile(string path) => HashText(File.ReadAllBytes(path));

    private static string HashText(string text) => HashText(Encoding.UTF8.GetBytes(text));

    private static string HashText(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            return (new FileInfo(fullPath).ResolveLinkTarget(true)?.FullName ?? fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (Directory.Exists(fullPath))
        {
            return (new DirectoryInfo(fullPath).ResolveLinkTarget(true)?.FullName ?? fullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent) && parent != fullPath)
        {
            return Path.Combine(NormalizePath(parent), Path.GetFileName(fullPath));
        }

        return fullPath;
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(NormalizePath(root), NormalizePath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string SafeError(string prefix, Exception ex)
    {
        var message = ex switch
        {
            FormatException => "invalid content",
            JsonException => "invalid JSON",
            IOException => "file I/O failure",
            UnauthorizedAccessException => "access denied",
            _ => "unexpected failure"
        };
        return $"{prefix}: {message}.";
    }

    private sealed record WriteRecord(FileEdit Edit, bool OriginalExists, string OriginalHash);

    private sealed class BackupManifest
    {
        public string Marker { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string Summary { get; set; } = "";
        public List<BackupFile> Files { get; set; } = [];
    }

    private sealed class BackupFile
    {
        public string TargetPath { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public bool OriginalExists { get; set; }
        public string OriginalHash { get; set; } = MissingHash;
        public string ResultHash { get; set; } = MissingHash;
    }

    private sealed class JournalState
    {
        public string Marker { get; set; } = "";
        public string Phase { get; set; } = "";
        public List<string> Paths { get; set; } = [];
    }
}






















