using System.Security.Cryptography;
using System.Text.Json;

namespace CodexControlCenter.Core;

public sealed partial class RuntimeService
{
    public async Task InstallRouterReloadSupportAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        var script = FindAdapterScript("install-router-runtime.ps1") ?? throw new FileNotFoundException("未找到路由更新脚本，请保留完整管理台目录。");
        var home = ResolveRouterHome(snapshot) ?? throw new InvalidOperationException("未找到路由目录。");
        var info = new System.Diagnostics.ProcessStartInfo {
            FileName = ResolvePowerShellExecutable(), UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var value in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            "-RouterHome", home, "-CodexHome", snapshot.CodexHome }) info.ArgumentList.Add(value);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("未能启动路由更新程序。");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        // Never expose raw shell exception/source output to the UI.
        if (process.ExitCode != 0) throw new InvalidOperationException("路由自动加载支持安装失败。现有运行进程未停止，请检查目录权限和路由模块兼容性。");
    }

    /// <summary>Only a local health request; never invokes an upstream or reads a credential.</summary>
    public async Task<HealthResult> CheckRouterConfigurationAsync(WorkspaceSnapshot snapshot, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        HealthResult Result(string status, string detail) => new("配置生效", status, false, detail, now);
        try
        {
            var path = ResolveConfigPath(snapshot, ResolveRouterHome(snapshot));
            if (path is null || !File.Exists(path)) return Result("无法核对", "未找到本地路由配置文件。");
            var uri = ResolveLocalHealthUri(snapshot);
            // A runtime endpoint must remain loopback even for caller-supplied snapshot URLs.
            if (uri.Host != "127.0.0.1" && uri.Host != "localhost" && uri.Host != "[::1]")
                return Result("无法核对", "只允许检查本机路由状态。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(5000);
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Result("未确认", "路由未返回成功状态；配置文件已保存不代表服务已生效。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16385];
            int size = 0;
            while (size < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(size), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                size += count;
            }
            if (size == buffer.Length) return Result("无法核对", "路由状态响应超出限制。");
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, size));
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)return Result("无法核对","路由状态格式无效；未将它视为配置已生效。");
            if (!doc.RootElement.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object)
                return Result("运行配置未确认", "当前服务未提供配置指纹。保存后可能仍使用旧路由或旧密钥；需在当前任务完成后重启本地服务。");
            if (runtime.TryGetProperty("reload_error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var message = error.GetString() switch
                {
                    "credential_load_failed" => "新密钥加载失败。检查该来源的加密凭据；新请求已停止，正在进行的请求继续。",
                    "token_change_requires_restart" or "listener_or_token_change_requires_restart" => "监听地址或本地认证已改变，需要在当前任务结束后重启路由。",
                    "configuration_changed_during_reload" => "配置在加载期间再次变化，请稍后重新检查。",
                    _ => "配置加载失败。新请求已停止，正在进行的请求继续；修正配置后将自动恢复。"
                };
                return Result("保存但未生效", message);
            }
            if (!runtime.TryGetProperty("config_sha256", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.String)
                return Result("未确认", "服务没有返回可核对的配置指纹。");
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actual, fingerprint.GetString(), StringComparison.OrdinalIgnoreCase))
                return Result("等待加载", "文件已保存，运行配置仍不同步；稍后刷新将再次核对。");
            if(runtime.TryGetProperty("config_synced",out var synced)&&synced.ValueKind==JsonValueKind.False)
                return Result("等待加载","路由仍在核对配置；正在运行的任务继续，后续状态会自动刷新。");
            if(runtime.TryGetProperty("credential_status",out var credentialState)&&credentialState.ValueKind==JsonValueKind.String){
                string state=credentialState.GetString()??"";
                if(state is "degraded" or "unavailable"){
                    var ids=new List<string>();
                    if(runtime.TryGetProperty("unavailable_providers",out var unavailable)&&unavailable.ValueKind==JsonValueKind.Array)
                        foreach(var item in unavailable.EnumerateArray())if(item.ValueKind==JsonValueKind.Object&&item.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String){
                            string value=id.GetString()??"";if(System.Text.RegularExpressions.Regex.IsMatch(value,"^[A-Za-z0-9_-]{1,64}$"))ids.Add(snapshot.Providers.FirstOrDefault(p=>p.Id==value)?.Name??value);
                        }
                    return Result(state=="degraded"?"部分来源需处理":"凭据来源不可用","运行配置已同步；"+(ids.Count>0?string.Join("、",ids.Select(UserFeedback.Redact)):"部分来源")+"的凭据无法加载。请在模型接入修正，其他健康来源继续使用。");
                }
                if(state=="ready")return new("配置生效","配置与凭据来源已加载",true,
                    "运行配置与磁盘一致，已读取当前凭据来源；环境变量来源每 15 秒重新检查。是否有模型权限或余额，仍以实际调用结果为准。",now);
            }
            return new HealthResult("配置生效", "运行配置已同步", true,
                "磁盘与运行路由的配置指纹一致；此版本未单独报告凭据加载状态，模型实际可用性仍以真实请求结果为准。", now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        { return Result("未确认", "暂时无法核对运行配置；未发送模型请求，下一轮继续检查。"); }
    }

    public static bool ChangesAffectRouter(WorkspaceSnapshot snapshot, IEnumerable<FileEdit> files)
    {
        if(string.IsNullOrWhiteSpace(snapshot.RouterPath))return false;
        return files.Any(edit=>string.Equals(Path.GetFullPath(edit.Path),Path.GetFullPath(snapshot.RouterPath),StringComparison.OrdinalIgnoreCase)||
            snapshot.Providers.Any(provider=>string.Equals(Path.GetFullPath(edit.Path),Path.GetFullPath(Path.Combine(snapshot.CodexHome,"secrets","control-center-"+provider.Id+".dpapi")),StringComparison.OrdinalIgnoreCase)));
    }
}
