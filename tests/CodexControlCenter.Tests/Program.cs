using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodexControlCenter.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("custom wallpaper metadata, thumbnails, cancellation and cache invalidation", WallpaperCatalogTests.RunAsync),
    ("temporary wallpaper lease isolation, revocation and composition geometry", WallpaperPreviewLeaseTests.RunAsync),
    ("wallpaper image library, validation, conflicts and recovery", WallpaperLibraryTests.RunAsync),
    ("wallpaper target isolation, renderer lifecycle and runtime status", WallpaperConnectionTests.RunAsync),
    ("multi-model edits, compaction consistency, atomic rejection and quick navigation", ModelEditingTests.RunAsync),
    ("stable managed hook commands, precise trust scope, idempotence and restore", ManagedHookTrustTests.RunAsync),
    ("runtime configuration activation distinguishes saved, stale, legacy and failed", RouterActivationTests.RunAsync),
    ("load and map fake catalog/router", LoadAndMapAsync),
    ("preserve TOML comments and unknown fields", PreserveUnknownAsync),
    ("provider/model roundtrip", ProviderModelRoundtripAsync),
    ("credential ciphertext transaction", CredentialTransactionAsync),
    ("schema validation rules", SchemaValidationAsync),
    ("reject content hash conflict", ConflictAsync),
    ("backup restore and missing-file deletion", BackupRestoreAsync),
    ("reject illegal default model", IllegalDefaultAsync),
    ("scope and syntax validation", ScopeAndValidationAsync),
    ("reject tampered or foreign backup", BackupSecurityAsync)
    ,("global subagent policy integration", SubagentPolicyAsync)
    ,("managed subagent policy instruction boundaries", ManagedPolicyInstructionsAsync)
    ,("transaction boundaries, recovery and config watchers", TransactionRegressionTests.RunAsync)
    ,("invalid image JSON is rejected without writes", ImageServiceValidationTests.RunAsync)
    ,("preferences recovery and safe actionable feedback", UsabilityRegressionTests.RunAsync)
    ,("image process success, bounded output, timeout and cancellation", ImageGenerationRunnerTests.RunAsync)
    ,("consistent snapshots, editor conflicts and malformed backup recovery", SnapshotRegressionTests.RunAsync)
    ,("model visibility, composed edits, conflicts and byte-exact restore", ModelVisibilityTests.RunAsync)
    ,("global Markdown discovery, editing, creation, scope and recovery", GlobalMarkdownTests.RunAsync)
    ,("Markdown sources, read-only managed docs and project instruction folders", ExpandedMarkdownTests.RunAsync)
    ,("unified knowledge document categories, batch save and path safety", KnowledgeDocumentsTests.RunAsync)
};

var failed = 0;
int filterIndex=Array.IndexOf(args,"--filter");if(filterIndex>=0&&filterIndex+1<args.Length)tests=tests.Where(t=>t.Name.Contains(args[filterIndex+1],StringComparison.OrdinalIgnoreCase)).ToList();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}: {ex}");
    }
}

Console.WriteLine(failed == 0 ? $"ALL {tests.Count} TESTS PASSED" : $"{failed} TEST(S) FAILED");
return failed == 0 ? 0 : 1;

static async Task LoadAndMapAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    Assert(s.Models.Count == 3, $"Models={s.Models.Count}");
    Assert(s.Models.Count(x => x.Mapped) == 2, "mapped count");
    Assert(s.Providers.Select(x => x.Id).OrderBy(x => x).SequenceEqual(new[] { "a", "b" }), "provider ids");
    Assert(s.Providers.All(x => x.Name.StartsWith("Router ")), "router provider name extension");
    Assert(s.MainModel == "model-a" && s.MainEffort == "high", "main defaults");
    Assert(s.AgentModel == "model-b" && s.AgentEffort == "max" && s.AgentConcurrency == 20, "agent defaults");
    Assert(Path.GetFileName(s.AgentsPath) == "AGENTS.md", "AgentsPath");
    var sample = s.Models.Single(x => x.Id == "model-a");
    Assert(sample.Provider == "a" && sample.UpstreamModel == "up-a", "route overlay");
    Assert(sample.Efforts.Split(',', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[] { "none", "high", "max" }), "efforts");
}

static async Task SubagentPolicyAsync()
{
    await using var f=await FakeWorkspace.CreateAsync();
    var s=await f.Service.LoadAsync();
    string hooksPath=Path.Combine(f.CodexHome,"hooks.json");
    await File.WriteAllTextAsync(hooksPath,"""{"custom_field":"preserve","hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"echo preserve"}]}]}}""");
    s=await f.Service.LoadAsync();
    string fakeApp=Path.Combine(f.Root,"app"),script=Path.Combine(fakeApp,"adapters","subagent-policy","policy_hook.py");Directory.CreateDirectory(Path.GetDirectoryName(script)!);await File.WriteAllTextAsync(script,"# isolated fixture");
    var service=new SubagentPolicyService(f.CodexHome,fakeApp);
    var initial=service.Load(s);Assert(!initial.Enabled,"policy defaults off");
    var policy=new SubagentRoutingPolicy{Enabled=true,Primary=new(){Provider="a",Model="model-a",Effort="high"},Overflow=new(){Provider="b",Model="model-b",Effort="max"}};
    string python=Environment.ProcessPath!;
    var plan=service.Plan(s,policy,python);Assert(plan.Errors.Count==0,string.Join(";",plan.Errors));
    Assert(plan.Files.Select(x=>x.Path).Distinct().Count()==plan.Files.Count,"policy no duplicate files");
    string originalConfig=await File.ReadAllTextAsync(f.ConfigPath);
    var result=await f.Service.ApplyAsync(s,plan);
    Assert(service.Load(s).Enabled,"enabled policy saved");
    var hooks=JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!;
    Assert(hooks["custom_field"]!.ToString()=="preserve","hook unknown field preserved");
    Assert(hooks["hooks"]!["PreToolUse"]!.AsArray().Count==2,"existing hook preserved");
    Assert((await File.ReadAllTextAsync(f.ConfigPath)).Contains("# keep root comment"),"config comments retained");
    Assert((await File.ReadAllTextAsync(s.AgentsPath)).Contains("全局子代理模型分流"),"policy instruction added");
    await f.Service.RestoreAsync(result.BackupDirectory);
    Assert(!File.Exists(service.PolicyPath),"policy missing-file restore");Assert(await File.ReadAllTextAsync(f.ConfigPath)==originalConfig,"default config restored");
    s=await f.Service.LoadAsync();
    policy.Primary.Provider="b";Assert(service.Plan(s,policy,python).Errors.Count>0,"provider mismatch blocked");policy.Primary.Provider="a";
    var defaults=f.Service.PlanDefaults(s,"model-a","high","model-b","max",5);
    var merged=service.Plan(s,policy,python,defaults.Files.Single(f=>f.Path==s.ConfigPath).Content);
    Assert(merged.Files.Single(f=>f.Path==s.ConfigPath).Content.Contains("max_concurrent_threads_per_session = 5"),"default and policy compose");
    var enabling=service.Plan(s,policy,python);await f.Service.ApplyAsync(s,enabling);s=await f.Service.LoadAsync();policy.Enabled=false;
    var disabling=service.Plan(s,policy,"");Assert(disabling.Errors.Count==0,"disable does not require Python");await f.Service.ApplyAsync(s,disabling);
    hooks=JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!;
    Assert(hooks["hooks"]!["PreToolUse"]!.AsArray().Count==1,"disable preserves unrelated hook");
    Assert(!(await File.ReadAllTextAsync(s.AgentsPath)).Contains("全局子代理模型分流"),"policy instructions removed on disable");
    s=await f.Service.LoadAsync();policy.Enabled=true;policy.Mode="failure_fallback";
    var fallback=service.Plan(s,policy,python);Assert(fallback.Errors.Count==0,"fallback mode validation failed");
    Assert(fallback.Files.Single(file=>file.Path==service.PolicyPath).Content.Contains("failure_fallback"),"fallback mode not serialized");
    Assert(fallback.Files.Single(file=>file.Path==s.AgentsPath).Content.Contains("无需再次询问")&&fallback.Files.Single(file=>file.Path==s.AgentsPath).Content.Contains("不会自行创建"),"fallback guidance omitted authorization or runtime boundary");
    string guidance=fallback.Files.Single(file=>file.Path==s.AgentsPath).Content;
    Assert(guidance.Contains(SubagentPolicyService.SpawnArguments(policy.Primary))&&guidance.Contains(SubagentPolicyService.SpawnArguments(policy.Overflow)),"managed instructions must include exact JSON spawn arguments");
    Assert(guidance.Contains("禁止自行拼接")&&guidance.Contains("前五项")&&guidance.Contains("尚未取得代理 ID"),"model identity and shortlist guidance missing");
    foreach(var choice in new[]{new SubagentModelChoice{Provider="minimax",Model="MiniMax-M3.1-Flash",Effort="max"},new SubagentModelChoice{Provider="commandcode",Model="commandcode/deepseek-v4.1-flash",Effort="max"}}){
        var parsed=JsonNode.Parse(SubagentPolicyService.SpawnArguments(choice))!;
        Assert(parsed["model"]!.ToString()==choice.Model,"source prefix must never be added to model ID");
        Assert(parsed["reasoning_effort"]!.ToString()==choice.Effort&&parsed["fork_context"]!.GetValue<bool>()==false,"effort and fresh-context settings lost");
    }
    await f.Service.ApplyAsync(s,fallback);Assert(service.Load(s).Mode=="failure_fallback","fallback mode roundtrip failed");
    policy.Mode="invalid";Assert(service.Plan(s,policy,python).Errors.Count>0,"invalid routing mode accepted");
}

static async Task ManagedPolicyInstructionsAsync()
{
    const string start="<!-- codex-control-center:subagent-policy:start -->";
    const string end="<!-- codex-control-center:subagent-policy:end -->";
    const string legacyTitle="## 默认子代理与失败备用（受管理）";
    const string activeTitle="## 全局子代理模型分流（受管理）";

    static int Count(string text,string value)
    {
        int count=0,index=0;
        while((index=text.IndexOf(value,index,StringComparison.Ordinal))>=0){count++;index+=value.Length;}
        return count;
    }

    await using var f=await FakeWorkspace.CreateAsync();
    string agentsPath=Path.Combine(f.CodexHome,"AGENTS.md");
    await File.WriteAllTextAsync(agentsPath,"# User instructions\nKeep delegations focused.\n");
    var s=await f.Service.LoadAsync();
    string fakeApp=Path.Combine(f.Root,"app"),script=Path.Combine(fakeApp,"adapters","subagent-policy","policy_hook.py");
    Directory.CreateDirectory(Path.GetDirectoryName(script)!);await File.WriteAllTextAsync(script,"# isolated fixture");
    var service=new SubagentPolicyService(f.CodexHome,fakeApp);
    var policy=new SubagentRoutingPolicy{Enabled=true,Primary=new(){Provider="a",Model="model-a",Effort="high"},Overflow=new(){Provider="b",Model="model-b",Effort="max"}};
    string python=Environment.ProcessPath!;

    var first=service.Plan(s,policy,python);
    Assert(first.Errors.Count==0,string.Join(";",first.Errors));
    string firstContent=first.Files.Single(file=>file.Path==s.AgentsPath).Content;
    Assert(Count(firstContent,start)==1&&Count(firstContent,end)==1,"first save has exactly one managed block");
    Assert(firstContent.Contains("启用了全局分流，模型选择以本节为准"),"managed policy priority is unclear");
    Assert(firstContent.Contains("# User instructions")&&firstContent.Contains("Keep delegations focused."),"user instructions were removed");
    await f.Service.ApplyAsync(s,first);
    s=await f.Service.LoadAsync();
    var repeated=service.Plan(s,policy,python);
    Assert(repeated.Errors.Count==0,string.Join(";",repeated.Errors));
    Assert(repeated.Files.All(file=>file.Path!=s.AgentsPath),"saving the same policy again should be idempotent");
    string repeatedContent=await File.ReadAllTextAsync(s.AgentsPath);
    Assert(Count(repeatedContent,start)==1&&Count(repeatedContent,end)==1,"repeated save duplicated managed markers");

    string legacyContent="# Before\nKeep this paragraph.\n"+legacyTitle+"\nOld generated fallback rule.\n"+end+"\n# After\nKeep this too.\n";
    await File.WriteAllTextAsync(s.AgentsPath,legacyContent);
    s=await f.Service.LoadAsync();
    var legacy=service.Plan(s,policy,python);
    Assert(legacy.Errors.Count==0,string.Join(";",legacy.Errors));
    string legacyResult=legacy.Files.Single(file=>file.Path==s.AgentsPath).Content;
    Assert(!legacyResult.Contains(legacyTitle)&&!legacyResult.Contains("Old generated fallback rule."),"legacy block without start marker was not removed");
    Assert(legacyResult.Contains("# Before")&&legacyResult.Contains("Keep this paragraph.")&&legacyResult.Contains("# After")&&legacyResult.Contains("Keep this too."),"content around legacy block was lost");
    Assert(Count(legacyResult,start)==1&&Count(legacyResult,end)==1,"legacy cleanup did not leave one managed block");

    string imageSection="<!-- codex-control-center:image-generation:start -->\n## Image generation\nKeep image instructions.\n<!-- codex-control-center:image-generation:end -->\n";
    string multiBlock="# User preface\n"+start+"\n"+activeTitle+"\nOld generated concurrent rule.\n"+end+"\n"+
        imageSection+"\n"+legacyTitle+"\nOld generated fallback rule.\n"+end+"\n# User ending\nKeep ending.\n";
    await File.WriteAllTextAsync(s.AgentsPath,multiBlock);
    s=await f.Service.LoadAsync();
    var multiple=service.Plan(s,policy,python);
    Assert(multiple.Errors.Count==0,string.Join(";",multiple.Errors));
    string multipleResult=multiple.Files.Single(file=>file.Path==s.AgentsPath).Content;
    Assert(Count(multipleResult,start)==1&&Count(multipleResult,end)==1,"multiple old blocks were not consolidated");
    Assert(!multipleResult.Contains("Old generated concurrent rule.")&&!multipleResult.Contains("Old generated fallback rule."),"old managed text remained");
    Assert(multipleResult.Contains("# User preface")&&multipleResult.Contains("# User ending")&&multipleResult.Contains("Keep ending."),"unmanaged instructions were lost");
    Assert(multipleResult.Contains("image-generation:start")&&multipleResult.Contains("## Image generation")&&multipleResult.Contains("Keep image instructions.")&&multipleResult.Contains("image-generation:end"),"image-generation block was altered");
    await f.Service.ApplyAsync(s,multiple);

    string malformed="# Keep this\n"+start+"\n"+activeTitle+"\nIncomplete generated section.\n";
    await File.WriteAllTextAsync(s.AgentsPath,malformed);
    s=await f.Service.LoadAsync();
    string configBefore=await File.ReadAllTextAsync(f.ConfigPath);
    string hooksPath=Path.Combine(f.CodexHome,"hooks.json");
    string hooksBefore=await File.ReadAllTextAsync(hooksPath);
    var missingEnd=service.Plan(s,policy,python);
    Assert(missingEnd.Errors.Count>0,"missing end marker was accepted");
    await AssertThrows<InvalidOperationException>(()=>f.Service.ApplyAsync(s,missingEnd),"malformed managed block must not be applied");
    Assert(await File.ReadAllTextAsync(s.AgentsPath)==malformed,"missing end marker changed AGENTS.md");
    Assert(await File.ReadAllTextAsync(f.ConfigPath)==configBefore&&await File.ReadAllTextAsync(hooksPath)==hooksBefore,"malformed policy plan wrote other files");

    string cleanupInput="# Preserve before\n"+legacyTitle+"\nLegacy content.\n"+end+"\n"+
        start+"\n"+activeTitle+"\nCurrent content.\n"+end+"\n"+imageSection+"# Preserve after\n";
    await File.WriteAllTextAsync(s.AgentsPath,cleanupInput);
    s=await f.Service.LoadAsync();policy.Enabled=false;
    var disabled=service.Plan(s,policy,"");
    Assert(disabled.Errors.Count==0,string.Join(";",disabled.Errors));
    await f.Service.ApplyAsync(s,disabled);
    string disabledContent=await File.ReadAllTextAsync(s.AgentsPath);
    Assert(!disabledContent.Contains(start)&&!disabledContent.Contains(end)&&!disabledContent.Contains(legacyTitle)&&!disabledContent.Contains(activeTitle),"disable left managed policy content");
    Assert(disabledContent.Contains("# Preserve before")&&disabledContent.Contains("# Preserve after")&&disabledContent.Contains("image-generation:start")&&disabledContent.Contains("Keep image instructions."),"disable removed unrelated or image-generation content");
}

static async Task PreserveUnknownAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    var plan = f.Service.PlanDefaults(s, "model-b", "max", "model-a", "none", 7);
    Assert(plan.Errors.Count == 0, string.Join(";", plan.Errors));
    await f.Service.ApplyAsync(s, plan);
    var config = await File.ReadAllTextAsync(f.ConfigPath);
    Assert(config.Contains("# keep root comment") && config.Contains("unknown_root = 42"), "TOML root preservation");
    Assert(config.Contains("unknown_agent = true") && config.Contains("unknown_provider = { keep = true }"), "TOML nested preservation");
    var catalog = await File.ReadAllTextAsync(f.CatalogPath);
    var router = await File.ReadAllTextAsync(f.RouterPath);
    Assert(catalog.Contains("\"catalog_unknown\"") && router.Contains("\"router_unknown\""), "JSON unknown fields");
}

static async Task ProviderModelRoundtripAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    var provider = s.Providers.Single(x => x.Id == "a");
    provider.Name = "Renamed Router A";
    provider.BaseUrl = "https://example.invalid/v2";
    provider.KeyEnvironment = "CODEX_TEST_KEY_A";
    provider.RequestProfile = "";
    provider.ProxyUrl = "";
    var providerPlan = f.Service.PlanProvider(s, provider);
    Assert(providerPlan.Errors.Count == 0, string.Join(";", providerPlan.Errors));
    Assert(providerPlan.Files.All(x => x.Path == f.RouterPath), "PlanProvider must not touch config.toml");
    await f.Service.ApplyAsync(s, providerPlan);
    var routerText = await File.ReadAllTextAsync(f.RouterPath);
    Assert(routerText.Contains("\"name\": \"Renamed Router A\""), "provider name extension");
    Assert(routerText.Contains("\"custom_provider_field\""), "provider unknown field");
    Assert(!routerText.Contains("\"env_key\"") && !routerText.Contains("\"wire_api\""), "wrong provider keys");
    Assert(!routerText.Contains("\"auth_mode\""), "api_key default must remain implicit");
    s = await f.Service.LoadAsync();
    var newProviderPlan = f.Service.PlanProvider(s, new ProviderEntry { Id = "c", Name = "Router C", BaseUrl = "https://api-c.example/v1", AuthMode = "api_key", KeyEnvironment = "CODEX_TEST_KEY_C" });
    Assert(newProviderPlan.Errors.Count == 0, string.Join(";", newProviderPlan.Errors));
    await f.Service.ApplyAsync(s, newProviderPlan);
    s = await f.Service.LoadAsync();
    Assert(s.Providers.Any(x => x.Id == "c" && x.Name == "Router C"), "new router provider");

    s = await f.Service.LoadAsync();
    var model = s.Models.Single(x => x.Id == "model-a");
    model.DisplayName = "Model A Edited";
    model.UpstreamModel = "up-a-edited";
    model.Efforts = "none high max";
    model.AgentProtocol = "";
    model.ContextWindow = 123456;
    var modelPlan = f.Service.PlanModel(s, model);
    Assert(modelPlan.Errors.Count == 0, string.Join(";", modelPlan.Errors));
    await f.Service.ApplyAsync(s, modelPlan);

    var catalog = JsonNode.Parse(await File.ReadAllTextAsync(f.CatalogPath))!;
    var modelNode = catalog["models"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["slug"]!.GetValue<string>() == "model-a");
    Assert(modelNode["display_name"]!.GetValue<string>() == "Model A Edited", "model display");
    Assert(modelNode["context_window"]!.GetValue<long>() == 123456, "model context");
    Assert(modelNode["model_unknown"] is not null, "model unknown field");
    Assert(modelNode["multi_agent_version"] is null, "clear protocol");
    var levels = modelNode["supported_reasoning_levels"]!.AsArray().Select(x => x!.AsObject()).ToList();
    Assert(levels.Select(x => x["effort"]!.GetValue<string>()).SequenceEqual(new[] { "none", "high", "max" }), "supported levels");
    Assert(levels.Single(x => x["effort"]!.GetValue<string>() == "high")["description"]!.GetValue<string>() == "High description", "level description");
    Assert(levels.Single(x => x["effort"]!.GetValue<string>() == "high")["level_unknown"] is not null, "level unknown");
    Assert(modelNode["default_reasoning_level"]!.GetValue<string>() == "high", "default effort preserved when still supported");

    var routes = JsonNode.Parse(await File.ReadAllTextAsync(f.RouterPath))!["routes"]!.AsObject();
    var route = routes["model-a"]!.AsObject();
    Assert(route["model"]!.GetValue<string>() == "up-a-edited" && route["provider"]!.GetValue<string>() == "a", "route roundtrip");
    Assert(route["max_output_tokens"]!.GetValue<long>() == 4096, "route unknown/limit preserved");

    s = await f.Service.LoadAsync();
    var newModelPlan = f.Service.PlanModel(s, new ModelEntry { Id = "model-new", DisplayName = "New", Provider = "a", UpstreamModel = "up-new", Efforts = "none high max", ContextWindow = 300000 });
    Assert(newModelPlan.Errors.Count == 0, string.Join(";", newModelPlan.Errors));
    await f.Service.ApplyAsync(s, newModelPlan);
    catalog = JsonNode.Parse(await File.ReadAllTextAsync(f.CatalogPath))!;
    var newNode = catalog["models"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["slug"]!.GetValue<string>() == "model-new");
    Assert(newNode["shell_type"]!.GetValue<string>() == "unified_exec" && newNode["supported_in_api"]!.GetValue<bool>(), "new model clones required template fields");
}

static async Task CredentialTransactionAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var secretPath = f.SecretPath;
    var originalCiphertext = Convert.ToBase64String("original-fake-ciphertext"u8);
    var replacementCiphertext = Convert.ToBase64String("replacement-fake-ciphertext"u8);
    await File.WriteAllTextAsync(secretPath, originalCiphertext);
    var originalSecretBytes = await File.ReadAllBytesAsync(secretPath);
    var originalConfigBytes = await File.ReadAllBytesAsync(f.ConfigPath);

    var s = await f.Service.LoadAsync();
    s.FileHashes.TryAdd(secretPath, HashBytes(originalSecretBytes));
    var defaults = f.Service.PlanDefaults(s, "model-b", "max", "model-a", "none", 6);
    var credential = f.Service.PlanText(s, secretPath, replacementCiphertext, "Store encrypted provider credential");
    Assert(defaults.Errors.Count == 0 && credential.Errors.Count == 0, string.Join(";", defaults.Errors.Concat(credential.Errors)));
    var combined = new ChangePlan();
    combined.Files.AddRange(defaults.Files);
    combined.Files.AddRange(credential.Files);
    var applied = await f.Service.ApplyAsync(s, combined);
    Assert((await File.ReadAllTextAsync(secretPath)) == replacementCiphertext, "credential ciphertext applied with config");
    await f.Service.RestoreAsync(applied.BackupDirectory);
    Assert((await File.ReadAllBytesAsync(secretPath)).SequenceEqual(originalSecretBytes), "credential raw bytes restored");
    Assert((await File.ReadAllBytesAsync(f.ConfigPath)).SequenceEqual(originalConfigBytes), "config restored with credential transaction");

    s = await f.Service.LoadAsync();
    s.FileHashes.TryAdd(secretPath, HashBytes(originalSecretBytes));
    var conflictDefaults = f.Service.PlanDefaults(s, "model-a", "high", "model-b", "max", 5);
    var conflictCredential = f.Service.PlanText(s, secretPath, replacementCiphertext, "Store encrypted provider credential");
    combined = new ChangePlan();
    combined.Files.AddRange(conflictDefaults.Files);
    combined.Files.AddRange(conflictCredential.Files);
    await File.WriteAllTextAsync(secretPath, Convert.ToBase64String("external-fake-ciphertext"u8));
    var configBeforeConflict = await File.ReadAllTextAsync(f.ConfigPath);
    await AssertThrows<InvalidOperationException>(() => f.Service.ApplyAsync(s, combined), "credential hash conflict");
    Assert(await File.ReadAllTextAsync(f.ConfigPath) == configBeforeConflict, "credential conflict leaves config unchanged");

    var invalidContent = f.Service.PlanText(s, secretPath, "not base64!", "Invalid ciphertext");
    Assert(invalidContent.Files.Count == 0 && invalidContent.Errors.Count > 0, "invalid DPAPI base64 rejected");
    var invalidPath = Path.Combine(f.CodexHome, "secrets", "control-center-bad.id.dpapi");
    s.FileHashes.TryAdd(invalidPath, "<missing>");
    Assert(f.Service.PlanText(s, invalidPath, replacementCiphertext, "Invalid credential path").Errors.Count > 0, "invalid credential id rejected");
}

static async Task SchemaValidationAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    Assert(f.Service.PlanDefaults(s, "model-a", "ultra", "model-b", "max", 2).Errors.Count > 0, "declared model effort list enforced");
    s.Models.Single(x => x.Id == "model-b").Efforts = "";
    Assert(f.Service.PlanDefaults(s, "model-a", "high", "model-b", "ultra", 2).Errors.Count == 0, "undeclared effort list accepts valid effort");

    s = await f.Service.LoadAsync();
    var model = s.Models.Single(x => x.Id == "model-a");
    model.ContextWindow = 0;
    model.UpstreamModel = "";
    Assert(f.Service.PlanModel(s, model).Errors.Count >= 2, "model positive context and upstream required");
    model.ContextWindow = 100;
    model.UpstreamModel = "up-a";
    model.Id = "bad\u0001alias";
    Assert(f.Service.PlanModel(s, model).Errors.Count > 0, "model alias control characters rejected");

    var provider = s.Providers.Single(x => x.Id == "a");
    provider.KeyEnvironment = "BAD-NAME";
    provider.RequestProfile = "responses";
    Assert(f.Service.PlanProvider(s, provider).Errors.Count >= 2, "router provider schema enforced");
    provider.Id="../outside";provider.RequestProfile="";provider.KeyEnvironment="VALID_KEY";
    Assert(f.Service.PlanProvider(s,provider).Errors.Count>0,"provider id rejected");
    provider.Id="a";provider.AuthMode="chatgpt";provider.BaseUrl="https://example.invalid/v1";
    Assert(f.Service.PlanProvider(s,provider).Errors.Count>0,"ChatGPT endpoint restricted");
    provider.BaseUrl="https://chatgpt.com/backend-api/codex";
    Assert(f.Service.PlanProvider(s,provider).Errors.Count>0,"new ChatGPT login reference required");
}
static async Task ConflictAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    var plan = f.Service.PlanDefaults(s, "model-b", "max", "model-a", "none", 4);
    await File.WriteAllTextAsync(f.ConfigPath, (await File.ReadAllTextAsync(f.ConfigPath)) + Environment.NewLine + "# external edit" + Environment.NewLine);
    var before = await File.ReadAllTextAsync(f.ConfigPath);
    await AssertThrows<InvalidOperationException>(() => f.Service.ApplyAsync(s, plan), "hash conflict");
    Assert(await File.ReadAllTextAsync(f.ConfigPath) == before, "conflict must not write");
}

static async Task BackupRestoreAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var originalConfigBytes = await File.ReadAllBytesAsync(f.ConfigPath);
    Assert(originalConfigBytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "fixture BOM");
    var s = await f.Service.LoadAsync();
    var defaults = f.Service.PlanDefaults(s, "model-b", "max", "model-a", "none", 8);
    var defaultsResult = await f.Service.ApplyAsync(s, defaults);

    s = await f.Service.LoadAsync();
    var profilePath = Path.Combine(f.CodexHome, "control-center", "image-profiles.json");
    Assert(s.FileHashes[profilePath] == "<missing>", "missing hash convention");
    var profilePlan = f.Service.PlanText(s, profilePath, """{"active_provider":"i2m","profiles":{}}""", "Create image profile");
    Assert(profilePlan.Errors.Count == 0, string.Join(";", profilePlan.Errors));
    var profileResult = await f.Service.ApplyAsync(s, profilePlan);
    Assert(File.Exists(profilePath), "new profile created");

    await f.Service.RestoreAsync(profileResult.BackupDirectory);
    Assert(!File.Exists(profilePath), "restore deletes originally missing file");

    var restorePre = f.Service.ListBackups().First(x => x.Directory != profileResult.BackupDirectory && x.Summary == "State before restore");
    await f.Service.RestoreAsync(restorePre.Directory);
    Assert(File.Exists(profilePath), "pre-restore manifest ResultHash allows re-restoring");

    await f.Service.RestoreAsync(profileResult.BackupDirectory);
    await f.Service.RestoreAsync(defaultsResult.BackupDirectory);
    Assert(await File.ReadAllBytesAsync(f.ConfigPath) is var restored && restored.SequenceEqual(originalConfigBytes), "raw BOM bytes restored");
}

static async Task IllegalDefaultAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    var missing = f.Service.PlanDefaults(s, "__missing__", "high", "model-a", "max", 2);
    Assert(missing.Files.Count == 0 && missing.Errors.Any(x => x.Contains("Unknown or unmapped")), "missing model rejected");
    var unmapped = f.Service.PlanDefaults(s, "model-unmapped", "high", "model-a", "max", 2);
    Assert(unmapped.Files.Count == 0 && unmapped.Errors.Count > 0, "unmapped model rejected");
}

static async Task ScopeAndValidationAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    var role = Path.Combine(f.CodexHome, "agents", "reviewer.toml");
    Assert(s.FileHashes.ContainsKey(role), "role hash loaded");
    var validRole = f.Service.PlanText(s, role, "name = \"reviewer\"\ndescription = \"Review work\"\ndeveloper_instructions = \"Review only.\"\n", "Edit role");
    Assert(validRole.Errors.Count == 0, string.Join(";", validRole.Errors));
    var invalidRole = f.Service.PlanText(s, role, "name = [invalid", "Invalid role");
    Assert(invalidRole.Files.Count == 0 && invalidRole.Errors.Count > 0, "invalid role TOML rejected");

    var newSkill = Path.Combine(f.CodexHome, "skills", "new-skill", "SKILL.md");
    s.FileHashes[newSkill] = "<missing>";
    var skillPlan = f.Service.PlanText(s, newSkill, "---\nname: new-skill\ndescription: Test\n---\n", "Create skill");
    Assert(skillPlan.Errors.Count == 0, string.Join(";", skillPlan.Errors));
    Assert(s.FileHashes[newSkill] == "<missing>", "PlanText preserves existing hash");

    var unsafeSkill = Path.Combine(f.CodexHome, "skills", "..", "escape", "SKILL.md");
    Assert(f.Service.PlanText(s, unsafeSkill, "# no", "Escape").Errors.Count > 0, "unsafe skill path rejected");
}

static async Task BackupSecurityAsync()
{
    await using var f = await FakeWorkspace.CreateAsync();
    var s = await f.Service.LoadAsync();
    var plan = f.Service.PlanDefaults(s, "model-b", "max", "model-a", "none", 3);
    var result = await f.Service.ApplyAsync(s, plan);
    var manifestPath = Path.Combine(result.BackupDirectory, "manifest.json");
    var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
    var payloadRelative = manifest["Files"]![0]!["BackupPath"]!.GetValue<string>();
    var payload = Path.Combine(result.BackupDirectory, payloadRelative);
    await File.AppendAllTextAsync(payload, "tamper");
    s = await f.Service.LoadAsync();
    var current = await File.ReadAllTextAsync(f.ConfigPath);
    await AssertThrows<InvalidOperationException>(() => f.Service.RestoreAsync(result.BackupDirectory), "payload hash mismatch");
    Assert(await File.ReadAllTextAsync(f.ConfigPath) == current, "tampered restore leaves target unchanged");

    var foreign = Path.Combine(f.Root, "foreign-backup");
    Directory.CreateDirectory(foreign);
    await File.WriteAllTextAsync(Path.Combine(foreign, "manifest.json"), await File.ReadAllTextAsync(manifestPath));
    await AssertThrows<InvalidOperationException>(() => f.Service.RestoreAsync(foreign), "foreign backup root rejected");
}

static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task AssertThrows<T>(Func<Task> action, string message) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

sealed class FakeWorkspace : IAsyncDisposable
{
    public string Root { get; private init; } = "";
    public string CodexHome => Path.Combine(Root, ".codex");
    public string StateHome => Path.Combine(Root, "state");
    public string ConfigPath => Path.Combine(CodexHome, "config.toml");
    public string CatalogPath => Path.Combine(CodexHome, "model-router-sandbox", "combined-models.json");
    public string RouterPath => Path.Combine(CodexHome, "model-router-sandbox", "router.json");
    public string SecretPath => Path.Combine(CodexHome, "secrets", "control-center-test.dpapi");
    public ConfigurationService Service { get; private init; } = null!;

    public static async Task<FakeWorkspace> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexControlCenter.Tests", Guid.NewGuid().ToString("N"));
        var codexHome = Path.Combine(root, ".codex");
        var routeDir = Path.Combine(codexHome, "model-router-sandbox");
        Directory.CreateDirectory(routeDir);
        Directory.CreateDirectory(Path.Combine(codexHome, "agents"));
        Directory.CreateDirectory(Path.Combine(codexHome, "secrets"));
        Directory.CreateDirectory(Path.Combine(codexHome, "skills", "skill-a"));
        Directory.CreateDirectory(Path.Combine(codexHome, "skills", ".system", "hidden"));
        Directory.CreateDirectory(Path.Combine(root, ".agents", "skills", "skill-b"));

        var config = """
# keep root comment
model = "model-a" # keep trailing
model_reasoning_effort = "high"
model_catalog_json = "model-router-sandbox/combined-models.json"
unknown_root = 42

[agents]
default_subagent_model = "model-b"
default_subagent_reasoning_effort = "max"
max_concurrent_threads_per_session = 20
unknown_agent = true

[model_providers.host_router]
name = "Host router"
base_url = "http://127.0.0.1:18748/v1"
unknown_provider = { keep = true }
""";
        await File.WriteAllTextAsync(Path.Combine(codexHome, "config.toml"), config, new UTF8Encoding(true));

        var catalog = """
{
  "schema_version": 9,
  "catalog_unknown": {"keep": true},
  "models": [
    {
      "slug": "model-a",
      "display_name": "Model A",
      "description": "Model A description",
      "default_reasoning_level": "high",
      "supported_reasoning_levels": [
        {"effort": "none", "description": "None description", "level_unknown": 1},
        {"effort": "high", "description": "High description", "level_unknown": 2},
        {"effort": "max", "description": "Max description", "level_unknown": 3}
      ],
      "context_window": 100000,
      "multi_agent_version": "v1",
      "shell_type": "unified_exec",
      "supported_in_api": true,
      "model_unknown": {"keep": true}
    },
    {
      "slug": "model-b",
      "display_name": "Model B",
      "description": "Model B description",
      "default_reasoning_level": "none",
      "supported_reasoning_levels": [
        {"effort": "none", "description": "None description"},
        {"effort": "high", "description": "High description"},
        {"effort": "max", "description": "Max description"}
      ],
      "context_window": 200000
    },
    {
      "slug": "model-unmapped",
      "display_name": "Unmapped",
      "supported_reasoning_levels": [{"effort": "high", "description": "High"}]
    }
  ]
}
""";
        await File.WriteAllTextAsync(Path.Combine(routeDir, "combined-models.json"), catalog);

        var router = """
{
  "host": "127.0.0.1",
  "port": 18748,
  "auth_token_file": "C:/fake/router.token",
  "router_unknown": {"keep": true},
  "providers": {
    "a": {
      "base_url": "https://api-a.example/v1",
      "key_env": "CODEX_TEST_KEY_A",
      "name": "Router A",
      "custom_provider_field": {"keep": true}
    },
    "b": {
      "base_url": "https://api-b.example/v1",
      "key_env": "CODEX_TEST_KEY_B",
      "name": "Router B"
    }
  },
  "routes": {
    "model-a": {"provider": "a", "model": "up-a", "max_output_tokens": 4096, "route_unknown": true},
    "model-b": {"provider": "b", "model": "up-b"}
  }
}
""";
        await File.WriteAllTextAsync(Path.Combine(routeDir, "router.json"), router);
        await File.WriteAllTextAsync(Path.Combine(codexHome, "agents", "reviewer.toml"), "name = \"reviewer\"\ndescription = \"Review work\"\ndeveloper_instructions = \"Review only.\"\n");
        await File.WriteAllTextAsync(Path.Combine(codexHome, "skills", "skill-a", "SKILL.md"), "---\nname: skill-a\ndescription: Skill A\n---\n");
        await File.WriteAllTextAsync(Path.Combine(codexHome, "skills", ".system", "hidden", "SKILL.md"), "---\nname: hidden\n---\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".agents", "skills", "skill-b", "SKILL.md"), "---\nname: skill-b\ndescription: Skill B\n---\n");

        return new FakeWorkspace
        {
            Root = root,
            Service = new ConfigurationService(codexHome, Path.Combine(root, "state"))
        };
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }
}






