using System.Text.Json.Nodes;
using CodexControlCenter.Core;

public static class ImageServiceValidationTests
{
    public static Task RunAsync()
    {
        InvalidJsonIsRejectedWithoutWrites();
        NonObjectJsonIsRejectedWithoutWrites();
        UnknownFieldsArePreserved();
        MissingFilesUseDefaults();
        return Task.CompletedTask;
    }

    private static void InvalidJsonIsRejectedWithoutWrites()
    {
        using var f = Fixture.Create();
        File.WriteAllText(f.ProfilePath, "{ invalid");
        var before = File.ReadAllText(f.ProfilePath);

        AssertThrows<FormatException>(() => f.Service.Load("i2m"), "invalid profile JSON must throw a safe FormatException");
        var plan = f.Service.Plan(f.Snapshot, f.ValidProfile);
        Assert(plan.Errors.Count > 0 && plan.Files.Count == 0, "invalid profile must return Plan.Errors without edits");
        Assert(File.ReadAllText(f.ProfilePath) == before, "invalid profile must not be written");
        Assert(!File.Exists(f.AgentsPath), "invalid profile must not create AGENTS.md");
    }

    private static void NonObjectJsonIsRejectedWithoutWrites()
    {
        using var f = Fixture.Create();
        File.WriteAllText(f.ProfilePath, "[]");
        AssertThrows<FormatException>(() => f.Service.Load("i2m"), "non-object profile must throw a safe FormatException");

        File.WriteAllText(f.ProfilePath, """{"active_provider":"i2m","profiles":{}}""");
        File.WriteAllText(f.I2MConfigPath, "null");
        AssertThrows<FormatException>(() => f.Service.Load("i2m"), "non-object i2m config must throw a safe FormatException");
        File.WriteAllText(f.I2MConfigPath, "{ invalid");
        var plan = f.Service.Plan(f.Snapshot, f.ValidProfile);
        Assert(plan.Errors.Count > 0 && plan.Files.Count == 0, "invalid i2m config must return Plan.Errors without edits");
        Assert(!File.Exists(f.AgentsPath), "invalid i2m config must not create AGENTS.md");
    }

    private static void UnknownFieldsArePreserved()
    {
        using var f = Fixture.Create();
        File.WriteAllText(f.ProfilePath, """
            {"active_provider":"i2m","profile_unknown":{"keep":true},"profiles":{"i2m":{"provider":"i2m","profile_field_unknown":42}}}
            """);
        File.WriteAllText(f.I2MConfigPath, """
            {"base_url":"https://sub.114514.one/v1","config_unknown":{"keep":true}}
            """);

        var plan = f.Service.Plan(f.Snapshot, f.ValidProfile);
        Assert(plan.Errors.Count == 0, string.Join(";", plan.Errors));
        var profile = JsonNode.Parse(FindEdit(plan, f.ProfilePath).Content)!;
        var config = JsonNode.Parse(FindEdit(plan, f.I2MConfigPath).Content)!;
        Assert(profile["profile_unknown"]?["keep"]?.GetValue<bool>() == true, "profile unknown root field preserved");
        Assert(profile["profiles"]?["i2m"]?["profile_field_unknown"]?.GetValue<int>() == 42, "profile unknown provider field preserved");
        Assert(config["config_unknown"]?["keep"]?.GetValue<bool>() == true, "i2m config unknown field preserved");
    }

    private static void MissingFilesUseDefaults()
    {
        using var f = Fixture.Create();
        var i2m = f.Service.Load("i2m");
        Assert(i2m.Provider == "i2m", "missing i2m profile uses i2m provider default");
        Assert(i2m.BaseUrl == "https://sub.114514.one/v1" && i2m.Model == "gpt-image-2.5-sunburst", "missing i2m config uses defaults");
        Assert(i2m.CredentialFile == Path.Combine(f.CodexHome, "secrets", "control-center-image-i2m.dpapi"), "missing i2m credential uses default reference");

        var official = f.Service.Load("official");
        Assert(official.Provider == "official" && official.BaseUrl == "https://api.openai.com/v1", "missing official profile uses defaults");
        Assert(official.Model == "gpt-image-2.5-sunburst", "missing official profile uses default model");
    }

    private static FileEdit FindEdit(ChangePlan plan, string path)
    {
        return plan.Files.Single(x => string.Equals(Path.GetFullPath(x.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; private init; } = "";
        public string CodexHome => Path.Combine(Root, ".codex");
        public string ProfilePath => Path.Combine(CodexHome, "control-center", "image-profiles.json");
        public string I2MConfigPath => Path.Combine(CodexHome, "skills", "i2m-imagegen", "config.json");
        public string AgentsPath => Path.Combine(CodexHome, "AGENTS.md");
        public WorkspaceSnapshot Snapshot { get; private init; } = null!;
        public ImageService Service { get; private init; } = null!;
        public ImageProfile ValidProfile { get; private init; } = null!;

        public static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodexControlCenter.ImageServiceValidationTests", Guid.NewGuid().ToString("N"));
            var codexHome = Path.Combine(root, ".codex");
            Directory.CreateDirectory(Path.Combine(codexHome, "control-center"));
            Directory.CreateDirectory(Path.Combine(codexHome, "skills", "i2m-imagegen"));
            Directory.CreateDirectory(Path.Combine(codexHome, "secrets"));

            var snapshot = new WorkspaceSnapshot
            {
                CodexHome = codexHome,
                ImageConfigPath = Path.Combine(codexHome, "skills", "i2m-imagegen", "config.json"),
                AgentsPath = Path.Combine(codexHome, "AGENTS.md")
            };
            return new Fixture
            {
                Root = root,
                Snapshot = snapshot,
                Service = new ImageService(codexHome, root),
                ValidProfile = new ImageProfile
                {
                    Provider = "i2m",
                    BaseUrl = "https://sub.114514.one/v1",
                    Model = "gpt-image-2.5-sunburst",
                    Size = "1024x1024",
                    Quality = "high",
                    Format = "png",
                    Timeout = 600,
                    CredentialFile = Path.Combine(codexHome, "secrets", "control-center-image-i2m.dpapi")
                }
            };
        }

        public void Dispose()
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
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}