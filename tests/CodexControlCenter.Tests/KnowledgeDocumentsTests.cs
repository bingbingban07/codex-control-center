using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using CodexControlCenter.Core;

internal static class KnowledgeDocumentsTests
{
    public static async Task RunAsync()
    {
        await DiscoveryAndClassificationAsync();
        await RoleHashAndTomlValidationAsync();
        await BatchRestoreAsync();
        await BatchConflictAsync();
        await RangeAndLinkRejectionAsync();
        SlugBoundaries();
    }

    private static async Task DiscoveryAndClassificationAsync()
    {
        await using var f = await FakeWorkspace.CreateAsync();
        string rootAgents = Path.Combine(f.CodexHome, "AGENTS.md");
        string project = Path.Combine(f.Root, "project");
        string projectAgents = Path.Combine(project, "AGENTS.md");
        string pluginDocument = Path.Combine(f.CodexHome, "plugins", "cache", "sample", "README.md");
        string systemSkill = Path.Combine(f.CodexHome, "skills", ".system", "hidden", "SKILL.md");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.GetDirectoryName(pluginDocument)!);
        await File.WriteAllTextAsync(rootAgents, "# root\n");
        await File.WriteAllTextAsync(projectAgents, "# project\n");
        await File.WriteAllTextAsync(pluginDocument, "# plugin reference\n");

        var snapshot = await f.Service.LoadAsync();
        f.Service.RegisterMarkdownFolder(snapshot, project);
        snapshot.GlobalMarkdownFiles.Add(new GlobalMarkdownEntry(
            "forged duplicate",
            systemSkill,
            "project instructions",
            "",
            "项目指令",
            true));
        snapshot.GlobalMarkdownFiles.Add(new GlobalMarkdownEntry(
            "read-only duplicate",
            rootAgents,
            "managed reference",
            "",
            "全局配置",
            false));

        string configBefore = await File.ReadAllTextAsync(f.ConfigPath);
        var documents = f.Service.GetKnowledgeDocuments(snapshot);
        foreach(var listed in documents)
        {
            var single=f.Service.DescribeKnowledgeDocument(snapshot,listed.Path);
            Require(single==listed,"single-file description differs from full discovery: "+listed.Path);
        }
        Require(documents.Select(document => document.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == documents.Count,
            "knowledge documents were not deduplicated by normalized path");
        Require(documents.Count(document => document.Path.Equals(systemSkill, StringComparison.OrdinalIgnoreCase)) == 1,
            "system skill duplicate remained in the list");
        Require(!documents.Single(document => document.Path.Equals(systemSkill, StringComparison.OrdinalIgnoreCase)).Editable,
            "a project-imported duplicate made a system document editable");
        Require(!documents.Single(document => document.Path.Equals(rootAgents, StringComparison.OrdinalIgnoreCase)).Editable,
            "read-only metadata did not win duplicate resolution");
        Require(documents.Single(document => document.Path.Equals(pluginDocument, StringComparison.OrdinalIgnoreCase)) is
            { Category: DocumentCategory.Skills, Source: "插件文档", Editable: false },
            "plugin document was not classified as a read-only skill document");
        Require(documents.Any(document => document.Category == DocumentCategory.Skills && document.Path.EndsWith(Path.Combine("skill-a", "SKILL.md"), StringComparison.OrdinalIgnoreCase)),
            "personal skill was not merged from the skill snapshot");
        Require(documents.Any(document => document.Category == DocumentCategory.Skills && document.Path.EndsWith(Path.Combine("skill-b", "SKILL.md"), StringComparison.OrdinalIgnoreCase)),
            "shared skill was not merged from the skill snapshot");
        Require(documents.Single(document => document.Path.Equals(projectAgents, StringComparison.OrdinalIgnoreCase)).Name.Contains("project", StringComparison.OrdinalIgnoreCase) &&
            documents.Single(document => document.Path.Equals(rootAgents, StringComparison.OrdinalIgnoreCase)).Name == "AGENTS.md",
            "same-named root and project documents were not disambiguated with relative paths");
        Require(documents.Single(document => document.Path.Equals(Path.Combine(f.CodexHome, "agents", "reviewer.toml"), StringComparison.OrdinalIgnoreCase))
            .Category == DocumentCategory.Roles, "role TOML was not discovered");
        Require(await File.ReadAllTextAsync(f.ConfigPath) == configBefore && !File.Exists(Path.Combine(f.CodexHome, "AGENTS.override.md")),
            "discovery changed real configuration or created a file");
    }

    private static async Task RoleHashAndTomlValidationAsync()
    {
        await using var f = await FakeWorkspace.CreateAsync();
        var snapshot = await f.Service.LoadAsync();
        string role = Path.Combine(f.CodexHome, "agents", "reviewer.toml");
        snapshot.FileHashes.Remove(role);

        var described = f.Service.DescribeKnowledgeDocument(snapshot, role);
        Require(described.Category == DocumentCategory.Roles && snapshot.FileHashes.ContainsKey(Path.GetFullPath(role)),
            "first role discovery did not register a transaction hash");
        Require(f.Service.ReadKnowledgeDocument(snapshot, role).Contains("name = \"reviewer\""),
            "role TOML could not be read through the knowledge API");

        var invalid = f.Service.PlanKnowledgeDocuments(snapshot, new Dictionary<string, string>
        {
            [role] = "name = [invalid"
        });
        Require(invalid.Files.Count == 0 && invalid.Errors.Any(error => error.Contains(role, StringComparison.OrdinalIgnoreCase)),
            "invalid TOML was accepted or its error omitted the path");
    }

    private static async Task BatchRestoreAsync()
    {
        await using var f = await FakeWorkspace.CreateAsync();
        string agents = Path.Combine(f.CodexHome, "AGENTS.md");
        string skill = Path.Combine(f.CodexHome, "skills", "skill-a", "SKILL.md");
        string role = Path.Combine(f.CodexHome, "agents", "reviewer.toml");
        await File.WriteAllTextAsync(agents, "# Original\r\nKeep CRLF.\r\n", new UTF8Encoding(true));
        byte[] agentsOriginal = await File.ReadAllBytesAsync(agents);
        byte[] skillOriginal = await File.ReadAllBytesAsync(skill);
        byte[] roleOriginal = await File.ReadAllBytesAsync(role);
        var snapshot = await f.Service.LoadAsync();

        var plan = f.Service.PlanKnowledgeDocuments(snapshot, new Dictionary<string, string>
        {
            [agents] = "# Updated instructions\r\n",
            [skill] = "---\nname: skill-a\ndescription: Updated\n---\n",
            [role] = "name = \"reviewer\"\ndescription = \"Updated\"\ndeveloper_instructions = \"Review carefully.\"\n"
        });
        Require(plan.Errors.Count == 0 && plan.Files.Count == 3,
            "cross-category document plan failed: " + string.Join("; ", plan.Errors));

        var applied = await f.Service.ApplyAsync(snapshot, plan);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(applied.BackupDirectory, "manifest.json")))!;
        Require(manifest["Files"]!.AsArray().Count == 3, "cross-category save did not create one combined backup");
        await f.Service.RestoreAsync(applied.BackupDirectory);
        Require((await File.ReadAllBytesAsync(agents)).SequenceEqual(agentsOriginal), "restore did not preserve Markdown bytes and line endings");
        Require((await File.ReadAllBytesAsync(skill)).SequenceEqual(skillOriginal), "restore did not restore the skill");
        Require((await File.ReadAllBytesAsync(role)).SequenceEqual(roleOriginal), "restore did not restore role TOML");

        snapshot = await f.Service.LoadAsync();
        string newSkill = f.Service.NewPersonalSkillPath("fresh-skill");
        Require(f.Service.DescribeKnowledgeDocument(snapshot, newSkill) is
            { Category: DocumentCategory.Skills, Source: "个人技能", Editable: true }, "new personal skill draft was not describable");
        Require(f.Service.ReadKnowledgeDocument(snapshot, newSkill) == "" && !File.Exists(newSkill),
            "reading a skill draft created its file");
        var create = f.Service.PlanKnowledgeDocuments(snapshot, new Dictionary<string, string>
        {
            [newSkill] = "---\nname: fresh-skill\ndescription: Fresh\n---\n"
        });
        Require(create.Errors.Count == 0 && create.Files.Count == 1, "new personal skill could not be planned");
        var created = await f.Service.ApplyAsync(snapshot, create);
        Require(File.Exists(newSkill), "new personal skill was not written");
        await f.Service.RestoreAsync(created.BackupDirectory);
        Require(!File.Exists(newSkill), "restore did not remove a newly created skill file");
    }

    private static async Task BatchConflictAsync()
    {
        await using var f = await FakeWorkspace.CreateAsync();
        string agents = Path.Combine(f.CodexHome, "AGENTS.md");
        string skill = Path.Combine(f.CodexHome, "skills", "skill-a", "SKILL.md");
        await File.WriteAllTextAsync(agents, "# Original instructions\n");
        var snapshot = await f.Service.LoadAsync();
        var plan = f.Service.PlanKnowledgeDocuments(snapshot, new Dictionary<string, string>
        {
            [agents] = "# Planned instructions\n",
            [skill] = "---\nname: skill-a\ndescription: Planned\n---\n"
        });
        Require(plan.Errors.Count == 0 && plan.Files.Count == 2, "conflict fixture plan failed");

        const string external = "---\nname: skill-a\ndescription: External edit\n---\n";
        await File.WriteAllTextAsync(skill, external);
        bool rejected = false;
        try
        {
            await f.Service.ApplyAsync(snapshot, plan);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Require(rejected, "external conflict did not reject the whole batch");
        Require(await File.ReadAllTextAsync(agents) == "# Original instructions\n" && await File.ReadAllTextAsync(skill) == external,
            "a batch conflict wrote another file or overwrote the external edit");
    }

    private static async Task RangeAndLinkRejectionAsync()
    {
        await using var f = await FakeWorkspace.CreateAsync();
        var snapshot = await f.Service.LoadAsync();
        string outside = Path.Combine(f.Root, "outside.md");
        string system = Path.Combine(f.CodexHome, "skills", ".system", "hidden", "SKILL.md");
        var escaped = f.Service.PlanKnowledgeDocuments(snapshot, new Dictionary<string, string> { [outside] = "escape" });
        var readOnly = f.Service.PlanKnowledgeDocuments(snapshot, new Dictionary<string, string> { [system] = "overwrite" });
        Require(escaped.Files.Count == 0 && escaped.Errors.Any(error => error.Contains(outside, StringComparison.OrdinalIgnoreCase)),
            "out-of-scope path was accepted or error omitted its path");
        Require(readOnly.Files.Count == 0 && readOnly.Errors.Any(error => error.Contains(system, StringComparison.OrdinalIgnoreCase)),
            "system skill path was writable or error omitted its path");

        string role = Path.Combine(f.CodexHome, "agents", "reviewer.toml");
        string linkedRoleDirectory = Path.Combine(f.CodexHome, "agents", "linked");
        string linkedRole = Path.Combine(linkedRoleDirectory, "reviewer.toml");
        string roleTargetDirectory = Path.Combine(f.Root, "linked-role-target");
        Directory.CreateDirectory(roleTargetDirectory);
        await File.WriteAllTextAsync(Path.Combine(roleTargetDirectory, "reviewer.toml"), "name = \"reviewer\"\n");
        CreateJunction(linkedRoleDirectory, roleTargetDirectory);
        var afterLink = await f.Service.LoadAsync();
        Require(!f.Service.GetKnowledgeDocuments(afterLink).Any(document => document.Path.Equals(linkedRole, StringComparison.OrdinalIgnoreCase)),
            "role discovery followed a symbolic link");
        var linkedPlan = f.Service.PlanKnowledgeDocuments(afterLink, new Dictionary<string, string>
        {
            [linkedRole] = "name = \"linked\"\n"
        });
        Require(linkedPlan.Files.Count == 0 && linkedPlan.Errors.Any(error => error.Contains(linkedRole, StringComparison.OrdinalIgnoreCase)),
            "symbolic-link role path was accepted");
        Directory.Delete(linkedRoleDirectory, false);
        Require(File.Exists(Path.Combine(roleTargetDirectory, "reviewer.toml")), "removing the junction deleted its fixture target");

        string agentsDirectory = Path.Combine(f.CodexHome, "agents");
        string movedAgentsDirectory = Path.Combine(f.CodexHome, "agents-target");
        var currentSnapshot = await f.Service.LoadAsync();
        var junctionPlan = f.Service.PlanKnowledgeDocuments(currentSnapshot, new Dictionary<string, string>
        {
            [role] = "name = \"reviewer\"\ndescription = \"Changed\"\n"
        });
        Directory.Move(agentsDirectory, movedAgentsDirectory);
        try
        {
            CreateJunction(agentsDirectory, movedAgentsDirectory);
            bool rejected = false;
            try
            {
                await f.Service.ApplyAsync(currentSnapshot, junctionPlan);
            }
            catch (UnauthorizedAccessException)
            {
                rejected = true;
            }
            Require(rejected, "ApplyAsync followed an agents directory junction created after planning");
            Require((await File.ReadAllTextAsync(Path.Combine(movedAgentsDirectory, "reviewer.toml"))).Contains("description = \"Review work\""),
                "junction rejection modified the role target");
        }
        finally
        {
            if (Directory.Exists(agentsDirectory) && (File.GetAttributes(agentsDirectory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(agentsDirectory, false);
            if (Directory.Exists(movedAgentsDirectory))
                Directory.Move(movedAgentsDirectory, agentsDirectory);
        }
    }

    private static void SlugBoundaries()
    {
        using var temp = new TemporaryService();
        string minimum = temp.Service.NewPersonalSkillPath("a1");
        Require(minimum.EndsWith(Path.Combine("skills", "a1", "SKILL.md"), StringComparison.OrdinalIgnoreCase),
            "minimum skill slug did not produce a bounded path");
        string path = temp.Service.NewPersonalSkillPath(new string('a', 61));
        Require(path.EndsWith(Path.Combine("skills", new string('a', 61), "SKILL.md"), StringComparison.OrdinalIgnoreCase),
            "maximum skill slug did not produce a bounded path");
        Require(!Directory.Exists(Path.GetDirectoryName(path)), "skill path helper created a directory");
        foreach (string invalid in new[] { "a", new string('a', 62), "Upper", "a_b", "-a", "a/../b" })
        {
            bool rejected = false;
            try
            {
                temp.Service.NewPersonalSkillPath(invalid);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }
            Require(rejected, "invalid skill slug was accepted: " + invalid);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void CreateJunction(string path, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{path}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Unable to start cmd.exe to create a test junction.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Unable to create isolated test junction: {output} {error}");
    }

    private sealed class TemporaryService : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "KnowledgeDocumentSlugTests", Guid.NewGuid().ToString("N"));
        public ConfigurationService Service { get; }

        public TemporaryService()
        {
            Directory.CreateDirectory(root);
            Service = new ConfigurationService(Path.Combine(root, ".codex"), Path.Combine(root, "state"));
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
