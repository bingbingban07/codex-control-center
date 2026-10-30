using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexControlCenter.Core;

public sealed record HealthResult(string Name, string Status, bool Online, string Detail, DateTimeOffset CheckedAt);
public sealed record RequestRecord(
    string Model,
    string Provider,
    string Status,
    string Duration,
    string Time,
    string Source = "",
    DateTimeOffset? ObservedAt = null,
    string? RequestId = null)
{
    public string StatusText => RuntimeService.DescribeStatus(Status);
}

public sealed partial class RuntimeService
{
    private const int ProbeTimeoutMilliseconds = 5_000;
    private const int MaxRecentRequests = 200;
    private const int MaxEventFiles = 4;
    private const int MaxStdoutFiles = 20;
    private const long MaxBytesPerLogFile = 256 * 1024;
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<List<HealthResult>> CheckLocalAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        var localUri = ResolveLocalHealthUri(snapshot);
        var localTask = ProbeLocalRouterAsync(localUri, ct);
        var workBuddyProbes = FindWorkBuddyProviders(snapshot)
            .Select(provider => ProbeWorkBuddyAsync(provider, ct))
            .ToArray();

        await Task.WhenAll(new Task[] { localTask }.Concat(workBuddyProbes)).ConfigureAwait(false);

        var results = new List<HealthResult> { await localTask.ConfigureAwait(false) };
        if (results[0].Online)
            results.Add(await CheckRouterConfigurationAsync(snapshot, ct).ConfigureAwait(false));
        foreach (var probe in workBuddyProbes)
        {
            results.AddRange(await probe.ConfigureAwait(false));
        }

        return results;
    }

    public async Task<HealthResult> CheckProviderAsync(ProviderEntry provider, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ct.ThrowIfCancellationRequested();

        var checkedAt = DateTimeOffset.UtcNow;
        if (!TryCreateBaseUri(provider.BaseUrl, out var baseUri))
        {
            return new HealthResult(
                ProviderDisplayName(provider),
                "配置异常",
                false,
                "Base URL 无效；未发送请求，模型可用性未验证。",
                checkedAt);
        }

        var modelsUri = BuildModelsUri(baseUri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeoutMilliseconds);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            request.Headers.Authorization = null;
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            checkedAt = DateTimeOffset.UtcNow;
            return status switch
            {
                401 => new HealthResult(
                    ProviderDisplayName(provider),
                    "可达 · 鉴权待验",
                    true,
                    "未携带凭据的 GET models 返回 401；连接可达，鉴权和模型可用性未验证。",
                    checkedAt),
                404 => new HealthResult(
                    ProviderDisplayName(provider),
                    "可达 · 未实现",
                    true,
                    "未携带凭据的 GET models 返回 404；连接可达，但该端点未实现，模型可用性未验证。",
                    checkedAt),
                >= 200 and < 300 => new HealthResult(
                    ProviderDisplayName(provider),
                    "可达",
                    true,
                    $"未携带凭据的 GET models 返回 HTTP {status}；仅确认连接可达，未确认模型可用性。",
                    checkedAt),
                _ => new HealthResult(
                    ProviderDisplayName(provider),
                    "可达 · 异常响应",
                    true,
                    $"未携带凭据的 GET models 返回 HTTP {status}；连接可达，鉴权和模型可用性未验证。",
                    checkedAt)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new HealthResult(
                ProviderDisplayName(provider),
                "超时",
                false,
                $"未携带凭据的 GET models 在 {ProbeTimeoutMilliseconds / 1000} 秒内未完成；模型可用性未验证。",
                DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            return new HealthResult(
                ProviderDisplayName(provider),
                "不可达",
                false,
                $"未携带凭据的 GET models 连接失败（{SafeError(ex.Message)}）；模型可用性未验证。",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthResult(
                ProviderDisplayName(provider),
                "检查异常",
                false,
                $"未携带凭据的 GET models 检查失败（{SafeError(ex.Message)}）；后续监测继续。",
                DateTimeOffset.UtcNow);
        }
    }

    public List<RequestRecord> ReadRecentRequests(WorkspaceSnapshot snapshot, int limit = 30)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var boundedLimit = Math.Clamp(limit, 1, MaxRecentRequests);
        var routerHome = ResolveRouterHome(snapshot);
        if (routerHome is null || !Directory.Exists(routerHome))
        {
            return [];
        }

        var candidates = new List<TaggedRequestRecord>();
        foreach (var path in DiscoverEventLogs(routerHome))
        {
            candidates.AddRange(ReadTailRecords(path, boundedLimit, "旁路观测"));
        }

        foreach (var path in DiscoverStdoutLogs(routerHome))
        {
            candidates.AddRange(ReadTailRecords(path, boundedLimit, "路由日志"));
        }

        return candidates
            .OrderByDescending(candidate => candidate.SortAt)
            .ThenByDescending(candidate => candidate.Record.ObservedAt ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Order)
            .Take(boundedLimit)
            .Select(candidate => candidate.Record)
            .ToList();
    }

    public static string DescribeStatus(string status)
    {
        var normalized = status?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized == "未采集")
        {
            return "状态未采集";
        }

        if (normalized.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            return "已取消";
        }

        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            var category = code switch
            {
                >= 200 and < 300 => "成功",
                >= 300 and < 400 => "重定向",
                401 => "鉴权失败",
                404 => "未找到",
                408 => "请求超时",
                429 => "限流",
                499 => "客户端中断",
                >= 400 and < 500 => "客户端错误",
                >= 500 and < 600 => code == 503 ? "服务不可用" : code == 504 ? "网关超时" : "服务端错误",
                _ => "非标准状态"
            };
            return $"HTTP {code} · {category}";
        }

        if (normalized.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("success", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return "成功";
        }

        if (normalized.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return "失败";
        }

        return Redact(normalized);
    }

    public Task StartRouterAsync(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var localUri = ResolveLocalHealthUri(snapshot);
        return StartRouterCoreAsync(snapshot, localUri);
    }

    private static async Task StartRouterCoreAsync(WorkspaceSnapshot snapshot, Uri localHealthUri)
    {
        if (await IsHealthyAsync(localHealthUri, CancellationToken.None).ConfigureAwait(false))
        {
            return;
        }

        var routerHome = ResolveRouterHome(snapshot);
        if (routerHome is null)
        {
            throw new InvalidOperationException("无法从 WorkspaceSnapshot 确定路由目录。");
        }

        var managedScript = FindAdapterScript("start-managed-router.ps1");
        var useManagedScript = managedScript is not null && File.Exists(managedScript);
        var scriptPath = useManagedScript
            ? managedScript!
            : Path.Combine(routerHome, "launch-router.ps1");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("找不到可安全启动的路由脚本。", scriptPath);
        }

        var configPath = ResolveConfigPath(snapshot, routerHome);
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellExecutable(),
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? routerHome,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);

        if (useManagedScript)
        {
            AddArgument(startInfo, "-RouterHome", routerHome);
            startInfo.ArgumentList.Add("-UseObserver");
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                AddArgument(startInfo, "-ConfigPath", configPath);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.CodexHome))
            {
                AddArgument(startInfo, "-CodexHome", snapshot.CodexHome);
            }
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("路由启动进程未创建。");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"无法启动路由脚本：{SafeError(ex.Message)}", ex);
        }
    }

    private static async Task<bool> IsHealthyAsync(Uri healthUri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2_000);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            request.Headers.Authorization = null;
            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            return (int)response.StatusCode == 200;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<HealthResult> ProbeLocalRouterAsync(Uri healthUri, CancellationToken ct)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeoutMilliseconds);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUri);
            request.Headers.Authorization = null;
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            var online = (int)response.StatusCode == 200;
            checkedAt = DateTimeOffset.UtcNow;
            return new HealthResult(
                "本地路由",
                online ? "在线" : "异常响应",
                online,
                online
                    ? "GET health 返回 HTTP 200；仅确认进程在线，鉴权未测试。"
                    : $"GET health 返回 HTTP {(int)response.StatusCode}；鉴权未测试。",
                checkedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new HealthResult(
                "本地路由",
                "超时",
                false,
                $"GET health 在 {ProbeTimeoutMilliseconds / 1000} 秒内未完成；鉴权未测试。",
                DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            return new HealthResult(
                "本地路由",
                "离线",
                false,
                $"GET health 连接失败（{SafeError(ex.Message)}）；鉴权未测试。",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthResult(
                "本地路由",
                "检查异常",
                false,
                $"GET health 检查失败（{SafeError(ex.Message)}）；后续监测继续。",
                DateTimeOffset.UtcNow);
        }
    }

    private static async Task<List<HealthResult>> ProbeWorkBuddyAsync(ProviderEntry provider, CancellationToken ct)
    {
        var name = ProviderDisplayName(provider);
        var checkedAt = DateTimeOffset.UtcNow;
        if (!TryCreateBaseUri(provider.BaseUrl, out var baseUri))
        {
            return
            [
                new HealthResult(name, "配置异常", false, "Base URL 无效。", checkedAt),
                new HealthResult($"{name} 鉴权", "未测试", false, "未发送凭据；鉴权和模型可用性未验证。", checkedAt)
            ];
        }

        var tcpOnline = false;
        string tcpDetail;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(ProbeTimeoutMilliseconds);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(baseUri.Host, baseUri.Port, timeout.Token).ConfigureAwait(false);
                tcpOnline = true;
                tcpDetail = "TCP 可连接";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                tcpDetail = "TCP 连接超时";
            }
            catch (SocketException ex)
            {
                tcpDetail = $"TCP 连接失败（{SafeError(ex.Message)}）";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tcpDetail = $"TCP 检查异常（{SafeError(ex.Message)}）";
            }
        }

        var httpOnline = false;
        var httpDetail = "HTTP 未测试";
        if (tcpOnline)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeoutMilliseconds);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildHealthUri(baseUri));
                request.Headers.Authorization = null;
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                httpOnline = true;
                httpDetail = $"HTTP 可达（{(int)response.StatusCode}）";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                httpDetail = "HTTP 探测超时";
            }
            catch (HttpRequestException ex)
            {
                httpDetail = $"HTTP 探测失败（{SafeError(ex.Message)}）";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                httpDetail = $"HTTP 检查异常（{SafeError(ex.Message)}）";
            }
        }

        var online = tcpOnline && httpOnline;
        return
        [
            new HealthResult(
                name,
                online ? "在线" : "异常",
                online,
                $"{tcpDetail}；{httpDetail}。健康检查不测试鉴权或模型可用性。",
                DateTimeOffset.UtcNow),
            new HealthResult(
                $"{name} 鉴权",
                "未测试",
                false,
                "未发送凭据；鉴权和模型可用性未验证。",
                DateTimeOffset.UtcNow)
        ];
    }

    private static IEnumerable<ProviderEntry> FindWorkBuddyProviders(WorkspaceSnapshot snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in snapshot.Providers ?? [])
        {
            if (provider is null)
            {
                continue;
            }

            var providerId = provider.Id ?? string.Empty;
            var providerName = provider.Name ?? string.Empty;
            var isWorkBuddy =
                providerId.Contains("workbuddy", StringComparison.OrdinalIgnoreCase) ||
                providerName.Contains("workbuddy", StringComparison.OrdinalIgnoreCase) ||
                providerId.Equals("wb", StringComparison.OrdinalIgnoreCase) ||
                (TryCreateBaseUri(provider.BaseUrl, out var uri) && uri.Port == 8788);
            if (!isWorkBuddy)
            {
                continue;
            }

            var key = $"{provider.Id}\n{provider.BaseUrl}";
            if (seen.Add(key))
            {
                yield return provider;
            }
        }
    }

    private static Uri ResolveLocalHealthUri(WorkspaceSnapshot snapshot)
    {
        if (TryCreateAbsoluteHttpUri(snapshot.RouterPath, out var explicitUri))
        {
            return BuildHealthUri(explicitUri);
        }

        var routerHome = ResolveRouterHome(snapshot);
        var configPath = ResolveConfigPath(snapshot, routerHome);
        if (configPath is not null && File.Exists(configPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = document.RootElement;
                var router = root;
                if ((!root.TryGetProperty("host", out _) || !root.TryGetProperty("port", out _)) &&
                    root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("router", out var configured))
                {
                    router = configured;
                }

                if (router.ValueKind == JsonValueKind.Object)
                {
                    var host = router.TryGetProperty("host", out var hostValue) && hostValue.ValueKind == JsonValueKind.String
                        ? hostValue.GetString()
                        : null;
                    var port = root.TryGetProperty("port", out var rootPort) && rootPort.TryGetInt32(out var rootParsedPort)
                        ? rootParsedPort
                        : router.TryGetProperty("port", out var portValue) && portValue.TryGetInt32(out var parsedPort)
                            ? parsedPort
                            : 18748;

                    if (!string.IsNullOrWhiteSpace(host) && port is > 0 and <= 65535)
                    {
                        const string loopbackHost = "127.0.0.1";
                        return new UriBuilder(Uri.UriSchemeHttp, loopbackHost, port, "/health").Uri;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // The fixed loopback probe below remains safe and does not expose configuration content.
            }
        }

        return new Uri("http://127.0.0.1:18748/health");
    }

    private static string? ResolveRouterHome(WorkspaceSnapshot snapshot)
    {
        foreach (var candidate in new[] { snapshot.RouterPath, snapshot.ConfigPath })
        {
            if (string.IsNullOrWhiteSpace(candidate) || TryCreateAbsoluteHttpUri(candidate, out _))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                return Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Try the next snapshot field.
            }
        }

        return null;
    }

    private static string? ResolveConfigPath(WorkspaceSnapshot snapshot, string? routerHome)
    {
        if (!string.IsNullOrWhiteSpace(routerHome))
        {
            var routerConfig = Path.Combine(routerHome, "router.json");
            if (File.Exists(routerConfig))
            {
                return routerConfig;
            }
        }

        foreach (var candidate in new[] { snapshot.RouterPath, snapshot.ConfigPath })
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                TryCreateAbsoluteHttpUri(candidate, out _) ||
                !File.Exists(candidate) ||
                !string.Equals(Path.GetExtension(candidate), ".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Path.GetFullPath(candidate);
        }

        return null;
    }

    private sealed record TaggedRequestRecord(RequestRecord Record, DateTimeOffset SortAt, int Order);

    private static IReadOnlyList<string> DiscoverEventLogs(string routerHome)
    {
        var paths = new List<string> { Path.Combine(routerHome, "events.ndjson") };
        for (var index = 1; index < MaxEventFiles; index++)
        {
            paths.Add(Path.Combine(routerHome, $"events.ndjson.{index}"));
        }

        return paths.Where(File.Exists).Take(MaxEventFiles).ToList();
    }

    private static IReadOnlyList<string> DiscoverStdoutLogs(string routerHome)
    {
        try
        {
            return Directory.EnumerateFiles(routerHome, "router*.stdout.log", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, ModifiedAt: SafeFileTime(path)))
                .OrderByDescending(item => item.ModifiedAt)
                .ThenByDescending(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxStdoutFiles)
                .Select(item => item.Path)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return [];
        }
    }

    private static DateTimeOffset SafeFileTime(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static List<TaggedRequestRecord> ReadTailRecords(string path, int limit, string sourceKind)
    {
        if (limit <= 0)
        {
            return [];
        }

        string tail;
        var startedAtFileStart = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var offset = Math.Max(0, length - MaxBytesPerLogFile);
            startedAtFileStart = offset == 0;
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, leaveOpen: false);
            tail = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }

        var observedAt = SafeFileTime(path);
        if (observedAt == DateTimeOffset.MinValue)
        {
            observedAt = DateTimeOffset.UtcNow;
        }

        var source = BuildSourceLabel(path, sourceKind);
        var lines = tail.Split('\n');
        var records = new List<TaggedRequestRecord>();
        for (var index = lines.Length - 1; index >= 0 && records.Count < limit; index--)
        {
            if (index == 0 && !startedAtFileStart)
            {
                continue;
            }

            var line = lines[index].TrimEnd('\r', '\n', '\0');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = ParseRequestRecord(line, source, observedAt);
            if (record is not null)
            {
                records.Add(new TaggedRequestRecord(record, ParseSortTime(record.Time) ?? observedAt, records.Count));
            }
        }

        return records;
    }

    private static RequestRecord? ParseRequestRecord(string line, string source, DateTimeOffset observedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !LooksLikeRequestRecord(root))
            {
                return null;
            }

            var model = ReadStringField(root, "model", "model_alias");
            var provider = ReadStringField(root, "provider", "provider_id");
            var rawStatus = ReadTextField(root, "status", "status_code", "http_status");
            var cancelled = ReadBoolean(root, "cancelled") == true ||
                rawStatus?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true;
            var status = cancelled ? "cancelled" : rawStatus ?? "未采集";
            var duration = ReadDuration(root);
            var time = ReadRequestTime(root);
            var requestId = ReadStringField(root, "request_id", "requestId");

            return new RequestRecord(
                Redact(model ?? "未采集"),
                Redact(provider ?? "未采集"),
                Redact(status),
                Redact(duration ?? "未采集"),
                Redact(time.Text),
                source,
                observedAt,
                requestId is null ? null : Redact(requestId));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool LooksLikeRequestRecord(JsonElement root)
    {
        var hasEvent = root.TryGetProperty("event", out _);
        var eventName = ReadStringField(root, "event");
        if (hasEvent && !string.Equals(eventName, "router_request", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var status = ReadTextField(root, "status", "status_code", "http_status")?.Trim();
        if (status is not null && NonRequestStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(eventName, "router_request", StringComparison.OrdinalIgnoreCase) ||
            HasAnyField(root, "request_id", "requestId", "model", "model_alias", "provider", "provider_id", "duration", "duration_ms", "total_ms") ||
            root.TryGetProperty("cancelled", out _);
    }

    private static readonly string[] NonRequestStatuses =
    [
        "listening",
        "started",
        "starting",
        "ready",
        "startup",
        "shutdown",
        "shutting_down",
        "stopped",
        "observer_started",
        "config_loaded",
        "connected",
        "health",
        "healthy",
        "ping"
    ];

    private static bool HasAnyField(JsonElement root, params string[] names) =>
        names.Any(name => root.TryGetProperty(name, out _));

    private static (string Text, DateTimeOffset? Value) ReadRequestTime(JsonElement root)
    {
        var text = ReadStringField(root, "time", "timestamp", "checked_at");
        if (text is null)
        {
            return ("未采集", null);
        }

        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsed))
        {
            return (text, parsed.ToUniversalTime());
        }

        return ("未采集", null);
    }

    private static DateTimeOffset? ParseSortTime(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static bool? ReadBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string BuildSourceLabel(string path, string sourceKind)
    {
        var fileName = Path.GetFileName(path);
        fileName = Redact(fileName);
        fileName = Regex.Replace(fileName, @"[^\p{L}\p{N}._-]+", "_");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "日志文件";
        }

        if (fileName.Length > 80)
        {
            fileName = fileName[..80];
        }

        return $"{sourceKind} · {fileName}";
    }
    private static string? ReadStringField(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }
    private static string? ReadTextField(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            else if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetRawText();
            }
        }

        return null;
    }

    private static string? ReadDuration(JsonElement root)
    {
        foreach (var name in new[] { "total_ms", "duration_ms" })
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var number) &&
                double.IsFinite(number) &&
                number >= 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{number:0.###} ms");
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                    double.IsFinite(number) &&
                    number >= 0)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"{number:0.###} ms");
                }

                var match = Regex.Match(text, @"^\s*(\d+(?:\.\d+)?)\s*ms\s*$", RegexOptions.IgnoreCase);
                if (match.Success &&
                    double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                    double.IsFinite(number))
                {
                    return string.Create(CultureInfo.InvariantCulture, $"{number:0.###} ms");
                }
            }
        }

        return ReadStringField(root, "duration");
    }

    private static Uri BuildHealthUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = "/health",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static Uri BuildModelsUri(Uri baseUri)
    {
        var path = baseUri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(baseUri)
        {
            Path = $"{path}/models",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static bool TryCreateBaseUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!TryCreateAbsoluteHttpUri(value, out uri))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return true;
    }

    private static bool TryCreateAbsoluteHttpUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            Credentials = null,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(ProbeTimeoutMilliseconds)
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static string ProviderDisplayName(ProviderEntry provider) =>
        string.IsNullOrWhiteSpace(provider.Name)
            ? (string.IsNullOrWhiteSpace(provider.Id) ? "Provider" : provider.Id)
            : provider.Name;

    private static string SafeError(string message)
    {
        var safe = Redact(message);
        return safe.Length <= 240 ? safe : safe[..240];
    }

    private static string Redact(string value)
    {
        var result = Regex.Replace(value, @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [redacted]");
        result = Regex.Replace(
            result,
            @"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|token|secret|authorization|password)(\s*[:=]\s*)\S+",
            "$1$2[redacted]");
        result = Regex.Replace(result, @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", "[redacted]");
        result = Regex.Replace(result, @"[\x00-\x1f\x7f]+", " ").Trim();
        return result.Length <= 512 ? result : result[..512];
    }

    private static string? FindAdapterScript(string fileName)
    {
        var roots = new List<string> { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (var root in roots)
        {
            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(Path.GetFullPath(root));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                continue;
            }

            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "adapters", "router-observability", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string ResolvePowerShellExecutable()
    {
        foreach (var executable in new[] { "pwsh.exe", "powershell.exe" })
        {
            var found = FindOnPath(executable);
            if (found is not null)
            {
                return found;
            }
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(systemDirectory))
        {
            var windowsPowerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(windowsPowerShell))
            {
                return windowsPowerShell;
            }
        }

        return "powershell.exe";
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(segment.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }
}












