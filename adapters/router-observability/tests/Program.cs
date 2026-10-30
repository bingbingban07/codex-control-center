using System.Net;
using System.Net.Sockets;
using System.Text;
using CodexControlCenter.Core;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

var fixture = Path.Combine(Path.GetTempPath(), $"runtime-service-test-{Guid.NewGuid():N}");
var routerHome = Path.Combine(fixture, "model-router");
Directory.CreateDirectory(routerHome);

TcpListener? listener = null;
try
{
    var configToml = Path.Combine(fixture, "config.toml");
    await File.WriteAllTextAsync(configToml, "this is deliberately not JSON\n");

    listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;

    var routerJson = Path.Combine(routerHome, "router.json");
    await File.WriteAllTextAsync(
        routerJson,
        "{" + "\"host\":\"127.0.0.1\",\"port\":" + port + "}",
        Encoding.UTF8);

    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var requestBuffer = new byte[4096];
        _ = await stream.ReadAsync(requestBuffer);
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(response);
    });

    var routerPath = Path.Combine(routerHome, "router.mjs");
    await File.WriteAllTextAsync(routerPath, "// fixture only\n");
    var snapshot = new WorkspaceSnapshot
    {
        RouterPath = routerPath,
        ConfigPath = configToml,
    };

    var service = new RuntimeService();
    var health = await service.CheckLocalAsync(snapshot);
    await serverTask;

    var local = health.Single(item => item.Name == "本地路由");
    Assert(local.Online, "Windows RouterPath did not resolve to router.json host/port.");
    Assert(local.Status == "在线", "A router JSON HTTP 200 probe must be online.");
    Assert(!local.Detail.Contains(configToml, StringComparison.OrdinalIgnoreCase), "Health detail exposed a fixture path.");

    var eventsPath = Path.Combine(routerHome, "events.ndjson");
    await File.WriteAllTextAsync(
        eventsPath,
        """
        {"request_id":"id-1","time":"2026-10-26T00:00:00Z","total_ms":12.5,"status":200,"model":null,"provider":null}
        {"model_alias":"model-x","provider":"provider-x","status":499}
        {"model":"Bearer abcdefghijklmnopqrstuvwxyz0123456789","provider":"token=very-secret","status":"ok","duration":"7ms","time":"t"}

        """,
        Encoding.UTF8);

    var recent = service.ReadRecentRequests(snapshot, 3);
    Assert(recent.Count == 3, "Bounded tail parsing did not return all fixture records.");
    Assert(recent[2].Model == "未采集", "Missing model must be 未采集.");
    Assert(recent[2].Duration == "12.5 ms", "Recorded duration was not preserved.");
    Assert(recent[1].Model == "model-x", "stdout-style model_alias was not read.");
    Assert(recent[1].Duration == "未采集", "Missing duration must not be calculated.");
    Assert(!recent[0].Model.Contains("abcdefghijklmnopqrstuvwxyz0123456789", StringComparison.Ordinal), "Bearer value was not redacted.");
    Assert(!recent[0].Provider.Contains("very-secret", StringComparison.Ordinal), "token value was not redacted.");

    File.Delete(eventsPath);
    var stdoutPath = Path.Combine(routerHome, "router-20261026.stdout.log");
    await File.WriteAllTextAsync(
        stdoutPath,
        "{\"model_alias\":\"legacy-model\",\"provider\":\"legacy-provider\",\"status\":200}\n",
        Encoding.UTF8);
    var legacy = service.ReadRecentRequests(snapshot, 1);
    Assert(legacy.Count == 1 && legacy[0].Model == "legacy-model", "Windows stdout log fallback returned no rows.");

    using var cancelled = new CancellationTokenSource();
    await cancelled.CancelAsync();
    try
    {
        await service.CheckProviderAsync(
            new ProviderEntry { Id = "cancelled", BaseUrl = "http://127.0.0.1:1/v1" },
            cancelled.Token);
        throw new InvalidOperationException("Cancelled provider check did not throw.");
    }
    catch (OperationCanceledException)
    {
        // Expected: caller cancellation is preserved.
    }

    await RuntimeServiceLogTests.RunAsync();

    Console.WriteLine("RuntimeService Windows path tests passed");
}
finally
{
    listener?.Stop();
    if (Directory.Exists(fixture))
    {
        Directory.Delete(fixture, recursive: true);
    }
}



