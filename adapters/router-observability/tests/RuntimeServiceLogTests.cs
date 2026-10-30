using System.Text;
using CodexControlCenter.Core;

internal static class RuntimeServiceLogTests
{
    public static async Task RunAsync()
    {
        await TestMergedSourcesAndSchemaToleranceAsync();
        await TestTimeAndObservedAtAsync();
        await TestStatusClassificationAsync();
        await TestHealthFailureIsolationAsync();
        await TestRotationAndFileFiltersAsync();
        await TestTailAndDefaultLimitsAsync();
        Console.WriteLine("RuntimeService request-log tests passed");
    }

    private static async Task TestMergedSourcesAndSchemaToleranceAsync()
    {
        var fixture = CreateFixture("merge");
        try
        {
            var eventsPath = Path.Combine(fixture.RouterHome, "events.ndjson");
            var stdoutPath = Path.Combine(fixture.RouterHome, "router-managed.stdout.log");
            await File.WriteAllTextAsync(
                eventsPath,
                """
                {"event":"router_request","request_id":"event-only","time":"2026-10-26T10:00:00Z","total_ms":12.5,"status":200,"model":null,"provider":null,"cancelled":false}
                {"status":"listening","host":"127.0.0.1","provider":"must-not-become-a-request"}
                {"event":"observer_started","request_id":"must-not-become-a-request"}
                {"request_id":"schema-tolerated","time":false,"status":{"code":200},"model":[],"provider":123,"total_ms":"not-a-number"}
                not-json
                """,
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                stdoutPath,
                """
                {"model_alias":"stdout-model","provider":"stdout-provider","status":503,"duration":"2ms","time":"2026-10-26T11:00:00Z"}
                {"status":"error","message":"unrelated process error"}
                """,
                Encoding.UTF8);

            var observedFileTime = new DateTime(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(eventsPath, observedFileTime);
            var records = fixture.Service.ReadRecentRequests(fixture.Snapshot, 20);

            Assert(records.Count == 3, "events and stdout request rows should both survive without accepting non-request JSON.");
            Assert(records[0].Model == "stdout-model" && records[0].Provider == "stdout-provider", "Newer stdout metadata must remain attached to its own record.");
            Assert(records[0].Source == "路由日志 · router-managed.stdout.log", "stdout source label is incorrect.");
            Assert(records.Any(record => record.RequestId == "event-only"), "observer request_id was not preserved.");

            var eventOnly = records.Single(record => record.RequestId == "event-only");
            Assert(eventOnly.Model == "未采集" && eventOnly.Provider == "未采集", "An observer event was incorrectly associated with unrelated stdout metadata.");
            Assert(eventOnly.Source == "旁路观测 · events.ndjson", "observer source label is incorrect.");
            Assert(eventOnly.ObservedAt is not null, "ObservedAt collection metadata is missing.");
            Assert(Math.Abs((eventOnly.ObservedAt!.Value - new DateTimeOffset(observedFileTime)).TotalSeconds) < 1,
                "ObservedAt must use the file timestamp as collection metadata.");

            var tolerated = records.Single(record => record.RequestId == "schema-tolerated");
            Assert(tolerated.Model == "未采集" && tolerated.Provider == "未采集", "Wrong schema types must degrade to 未采集.");
            Assert(tolerated.Status == "未采集" && tolerated.Time == "未采集", "Wrong schema types must not invent status or request time.");
            Assert(tolerated.Duration == "未采集", "An invalid duration string must not be shown as measured time.");
            Assert(!records.Any(record => record.Source.Contains("must-not-become-a-request", StringComparison.Ordinal)), "Source leaked fixture text.");
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    private static async Task TestTimeAndObservedAtAsync()
    {
        var fixture = CreateFixture("time");
        try
        {
            var eventsPath = Path.Combine(fixture.RouterHome, "events.ndjson");
            await File.WriteAllTextAsync(
                eventsPath,
                """
                {"request_id":"valid","time":"2026-10-26T18:00:00+08:00","status":200}
                {"request_id":"invalid","time":"not-a-time","status":200}
                {"request_id":"missing","status":200}
                {"request_id":"numeric","time":123456,"status":200}
                """,
                Encoding.UTF8);
            var observedFileTime = new DateTime(2026, 10, 26, 2, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(eventsPath, observedFileTime);

            var records = fixture.Service.ReadRecentRequests(fixture.Snapshot, 10)
                .ToDictionary(record => record.RequestId!);
            Assert(records["valid"].Time == "2026-10-26T18:00:00+08:00", "A legal request timestamp must be preserved.");
            Assert(records["invalid"].Time == "未采集", "An invalid timestamp must not be shown as request time.");
            Assert(records["missing"].Time == "未采集", "A missing timestamp must not be inferred.");
            Assert(records["numeric"].Time == "未采集", "A non-string timestamp must not become request time.");
            foreach (var record in records.Values)
            {
                Assert(record.ObservedAt == new DateTimeOffset(observedFileTime), "File time must only populate ObservedAt.");
            }
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    private static async Task TestStatusClassificationAsync()
    {
        var fixture = CreateFixture("status");
        try
        {
            var eventsPath = Path.Combine(fixture.RouterHome, "events.ndjson");
            await File.WriteAllTextAsync(
                eventsPath,
                """
                {"event":"router_request","request_id":"success","status":200}
                {"event":"router_request","request_id":"server-error","status":503}
                {"event":"router_request","request_id":"cancelled","status":499,"cancelled":true}
                {"event":"router_request","request_id":"interrupted","status":499,"cancelled":false}
                """,
                Encoding.UTF8);

            var records = fixture.Service.ReadRecentRequests(fixture.Snapshot, 10)
                .ToDictionary(record => record.RequestId!);

            Assert(records["success"].Status == "200" && records["success"].StatusText == "HTTP 200 · 成功", "HTTP success classification is unclear.");
            Assert(records["server-error"].StatusText == "HTTP 503 · 服务不可用", "HTTP server failure classification is unclear.");
            Assert(records["cancelled"].Status == "cancelled", "Observer cancellation must remain distinguishable in raw status.");
            Assert(records["cancelled"].StatusText == "已取消", "Cancellation text must not be presented as an HTTP result.");
            Assert(records["interrupted"].StatusText == "HTTP 499 · 客户端中断", "A non-cancelled HTTP 499 must remain an HTTP result.");
            Assert(RuntimeService.DescribeStatus("200") == "HTTP 200 · 成功", "DescribeStatus did not classify HTTP success.");
            Assert(RuntimeService.DescribeStatus("cancelled").Contains("取消", StringComparison.Ordinal), "DescribeStatus did not classify cancellation.");
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    private static async Task TestHealthFailureIsolationAsync()
    {
        var fixture = CreateFixture("health-failure");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.RouterHome, "router.json"),
                "{\"host\":\"127.0.0.1\",\"port\":1}");
            fixture.Snapshot.Providers.Add(new ProviderEntry
            {
                Id = "wb-probe",
                Name = "WorkBuddy",
                BaseUrl = "http://127.0.0.1:1"
            });

            var health = await fixture.Service.CheckLocalAsync(fixture.Snapshot);
            Assert(health.Count == 3, "A failed WorkBuddy probe must not suppress other health results.");
            Assert(health.Any(result => result.Name == "本地路由" && result.Status == "离线"), "Unreachable local health should return a contained offline result.");
            Assert(health.Any(result => result.Name == "WorkBuddy" && !result.Online), "Unreachable WorkBuddy health should return a contained failure result.");

            var provider = await fixture.Service.CheckProviderAsync(fixture.Snapshot.Providers[0]);
            Assert(provider.Status == "不可达", "Provider connection failure should be contained as a health result.");
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }
    private static async Task TestRotationAndFileFiltersAsync()
    {
        var rotated = CreateFixture("rotation");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rotated.RouterHome, "events.ndjson.1"), "{\"request_id\":\"backup-1\",\"model\":\"backup-one\",\"status\":200}\n");
            await File.WriteAllTextAsync(Path.Combine(rotated.RouterHome, "events.ndjson.2"), "{\"request_id\":\"backup-2\",\"model\":\"backup-two\",\"status\":200}\n");
            await File.WriteAllTextAsync(Path.Combine(rotated.RouterHome, "events.ndjson.3"), "{\"request_id\":\"backup-3\",\"model\":\"backup-three\",\"status\":200}\n");
            await File.WriteAllTextAsync(Path.Combine(rotated.RouterHome, "events.ndjson.4"), "{\"request_id\":\"ignored-4\",\"model\":\"ignored-four\",\"status\":200}\n");

            var rotatedRecords = rotated.Service.ReadRecentRequests(rotated.Snapshot, 20);
            Assert(rotatedRecords.Count == 3, "Rotation reads must be limited to the main event file and three backups.");
            Assert(rotatedRecords.Any(record => record.RequestId == "backup-1"), "events.ndjson.1 was not read.");
            Assert(rotatedRecords.Any(record => record.RequestId == "backup-2"), "events.ndjson.2 was not read.");
            Assert(rotatedRecords.Any(record => record.RequestId == "backup-3"), "events.ndjson.3 was not read.");
            Assert(!rotatedRecords.Any(record => record.RequestId?.StartsWith("ignored-", StringComparison.Ordinal) == true), "Out-of-range rotation files were read.");
        }
        finally
        {
            DeleteFixture(rotated.Root);
        }

        var recent = CreateFixture("recent-files");
        try
        {
            var baseTime = new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < 25; index++)
            {
                var path = Path.Combine(recent.RouterHome, $"router-{index:00}.stdout.log");
                await File.WriteAllTextAsync(path, $"{{\"request_id\":\"file-{index:00}\",\"model\":\"model-{index:00}\",\"status\":200}}\n");
                File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(index));
            }

            await File.WriteAllTextAsync(Path.Combine(recent.RouterHome, "other.stdout.log"), "{\"model\":\"must-ignore\",\"status\":200}\n");
            Directory.CreateDirectory(Path.Combine(recent.RouterHome, "nested"));
            await File.WriteAllTextAsync(Path.Combine(recent.RouterHome, "nested", "router-nested.stdout.log"), "{\"model\":\"must-ignore\",\"status\":200}\n");

            var records = recent.Service.ReadRecentRequests(recent.Snapshot, 200);
            Assert(records.Count == 20, "Recent stdout file discovery must enforce its file-count bound.");
            Assert(records.All(record => record.Source.StartsWith("路由日志 · router-", StringComparison.Ordinal)), "A non-router or nested stdout file was read.");
            Assert(records.Any(record => record.Model == "model-24"), "The newest stdout file must be included.");
            Assert(!records.Any(record => record.Model == "model-04"), "The oldest files beyond the bound must be excluded.");
            Assert(!records.Any(record => record.Model == "must-ignore"), "Filename filtering admitted an unrelated log.");
        }
        finally
        {
            DeleteFixture(recent.Root);
        }
    }

    private static async Task TestTailAndDefaultLimitsAsync()
    {
        var fixture = CreateFixture("limits");
        try
        {
            var stdoutPath = Path.Combine(fixture.RouterHome, "router-tail.stdout.log");
            var padding = new string('x', 300 * 1024);
            await File.WriteAllTextAsync(
                stdoutPath,
                $"{{\"request_id\":\"outside-tail\",\"model\":\"old\",\"status\":200}}\n" +
                $"{{\"request_id\":\"padding\",\"model\":\"{padding}\",\"status\":200}}\n" +
                "{\"request_id\":\"inside-tail\",\"model\":\"recent\",\"status\":200}\n",
                Encoding.UTF8);

            var tailRecords = fixture.Service.ReadRecentRequests(fixture.Snapshot, 20);
            Assert(tailRecords.Count == 1 && tailRecords[0].RequestId == "inside-tail", "Per-file reads must use a bounded tail.");
            Assert(!tailRecords.Any(record => record.RequestId == "outside-tail"), "A record outside the bounded tail was read.");

            var eventsPath = Path.Combine(fixture.RouterHome, "events.ndjson");
            var lines = new List<string>();
            for (var index = 0; index < 35; index++)
            {
                lines.Add($"{{\"request_id\":\"line-{index:00}\",\"model\":\"model-{index:00}\",\"status\":200}}");
            }

            await File.WriteAllTextAsync(eventsPath, string.Join('\n', lines) + "\n");
            File.Delete(stdoutPath);
            Assert(fixture.Service.ReadRecentRequests(fixture.Snapshot).Count == 30, "The default API limit must remain 30.");
            Assert(fixture.Service.ReadRecentRequests(fixture.Snapshot, 100).Count == 35, "The UI limit of 100 must be accepted.");
        }
        finally
        {
            DeleteFixture(fixture.Root);
        }
    }

    private static (string Root, string RouterHome, WorkspaceSnapshot Snapshot, RuntimeService Service) CreateFixture(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"runtime-service-log-test-{name}-{Guid.NewGuid():N}");
        var routerHome = Path.Combine(root, "model-router");
        Directory.CreateDirectory(routerHome);
        var snapshot = new WorkspaceSnapshot
        {
            RouterPath = Path.Combine(routerHome, "router.mjs"),
            ConfigPath = Path.Combine(routerHome, "router.json")
        };
        return (root, routerHome, snapshot, new RuntimeService());
    }

    private static void DeleteFixture(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}



