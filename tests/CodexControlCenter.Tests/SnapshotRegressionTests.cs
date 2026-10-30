using System.Text;
using System.Text.Json.Nodes;
using CodexControlCenter.Core;

internal static class SnapshotRegressionTests
{
    public static async Task RunAsync()
    {
        await using var fixture = await FakeWorkspace.CreateAsync();
        var service = fixture.Service;
        var snapshot = await service.LoadAsync();
        string path = snapshot.AgentsPath;
        await File.WriteAllTextAsync(path, "外部编辑后的文档");
        ExpectConflict(() => service.ReadDocument(snapshot, path));
        snapshot = await service.LoadAsync();
        Require(service.ReadDocument(snapshot, path) == "外部编辑后的文档", "fresh document not readable");

        // A new editor captures its own exact bytes, including BOM-aware decoding.
        string role = Path.Combine(fixture.CodexHome, "agents", "new-role.toml");
        await File.WriteAllTextAsync(role, "name = \"中文角色\"\n", new UnicodeEncoding(false, true));
        string text = service.ReadDocument(snapshot, role);
        Require(text == "name = \"中文角色\"\n", "document BOM decoding changed");
        await File.WriteAllTextAsync(role, "name = \"external\"\n");
        var plan = service.PlanText(snapshot, role, text, "edit role");
        await ExpectConflictAsync(() => service.ApplyAsync(snapshot, plan));
        Require((await File.ReadAllTextAsync(role)).Contains("external"), "external document overwritten");

        bool injected = false;
        void MutateWhileLoading(object? sender, ConfigurationProgress progress)
        {
            if (progress.Stage != "snapshot-read" || injected) return;
            injected = true;
            File.AppendAllText(fixture.ConfigPath, "\n# external during load\n");
        }
        service.Progress += MutateWhileLoading;
        try { await ExpectConflictAsync(() => service.LoadAsync()); }
        finally { service.Progress -= MutateWhileLoading; }
        Require(injected, "load mutation was not exercised");
        snapshot = await service.LoadAsync();
        Require(snapshot.Models.Count > 0, "load did not recover after conflict");

        var result = await service.ApplyAsync(snapshot,
            service.PlanDefaults(snapshot, "model-b", "max", "model-a", "none", 7));
        string manifestPath = Path.Combine(result.BackupDirectory, "manifest.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        // A malformed history entry must not prevent loading the whole application.
        foreach (JsonNode? files in new JsonNode?[] { null, new JsonArray((JsonNode?)null) })
        {
            manifest["Files"] = files;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            Require(service.ListBackups().Count == 0, "malformed backup was listed");
            bool rejected = false;
            try { await service.RestoreAsync(result.BackupDirectory); }
            catch (FormatException) { rejected = true; }
            Require(rejected, "malformed backup was not rejected before restore");
            await service.LoadAsync();
        }
    }

    private static void ExpectConflict(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) when (IsConflict(error)) { return; }
        throw new InvalidOperationException("expected content conflict");
    }
    private static async Task ExpectConflictAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (IsConflict(error)) { return; }
        throw new InvalidOperationException("expected content conflict");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static bool IsConflict(Exception error) =>
        error.Message.Contains("hash conflict", StringComparison.OrdinalIgnoreCase) ||
        error.Message.Contains("changed during transaction", StringComparison.OrdinalIgnoreCase);
}
