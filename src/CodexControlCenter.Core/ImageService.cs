using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexControlCenter.Core;

public sealed class ImageProfile
{
    public string Provider { get; set; } = "i2m";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string Size { get; set; } = "1024x1024";
    public string Quality { get; set; } = "high";
    public string Format { get; set; } = "png";
    public int Timeout { get; set; } = 600;
    public string CredentialFile { get; set; } = "";
}

public sealed class ImageService
{
    private const string ProfileRelativePath = "control-center/image-profiles.json";
    private const string I2MConfigRelativePath = "skills/i2m-imagegen/config.json";
    private const string I2MCredentialFileName = "control-center-image-i2m.dpapi";
    private const string OfficialCredentialFileName = "control-center-image-official.dpapi";
    private const string I2MFallbackModel = "gpt-image-2.5-sunburst";
    // Verified directly against the official OpenAI Image generation guide on 2026-10-26.
    private const string OfficialDefaultModel = "gpt-image-2.5-sunburst";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _codexHome;
    private readonly string _appRoot;

    public ImageService(string codexHome, string appRoot)
    {
        _codexHome = Path.GetFullPath(codexHome);
        _appRoot = Path.GetFullPath(appRoot);
    }

    public string DispatcherPath
    {
        get
        {
            var direct = Path.Combine(_appRoot, "adapters", "image-provider", "image_dispatch.py");
            if (File.Exists(direct))
            {
                return Path.GetFullPath(direct);
            }

            var directory = new DirectoryInfo(_appRoot);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "adapters", "image-provider", "image_dispatch.py");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }

            return Path.GetFullPath(direct);
        }
    }

    public string ActiveProvider
    {
        get
        {
            var root = ReadJsonObject(ProfilePath);
            var value = GetString(root, "active_provider", "activeProvider");
            return NormalizeProvider(string.IsNullOrWhiteSpace(value) ? "official" : value);
        }
    }

    public ImageProfile Load(string provider)
    {
        var normalized = NormalizeProvider(provider);
        var root = ReadJsonObject(ProfilePath);
        var profiles = GetObject(root, "profiles") ?? GetObject(root, "providers");
        var saved = FindProviderObject(profiles, normalized);
        var defaults = DefaultProfile(normalized);
        return MergeProfile(defaults, saved);
    }

    public ChangePlan Plan(WorkspaceSnapshot snapshot, ImageProfile profile)
    {
        var plan = new ChangePlan();
        if(!EnhancementMode.IsEnabled(_codexHome)){plan.Errors.Add("当前为原版模式。请先在设置与备份中启用增强模式，再应用受管理生图配置。");return plan;}
        var selectedProvider = NormalizeProvider(profile.Provider);
        var selected = NormalizeProfile(profile, selectedProvider);
        ValidateProfile(selected, plan.Errors);
        if (plan.Errors.Count > 0)
        {
            return plan;
        }

        var profilePath = ProfilePath;
        var i2mConfigPath = ResolvePath(snapshot.ImageConfigPath, Path.Combine(_codexHome, I2MConfigRelativePath));
        var agentsPath = ResolveAgentsPath(snapshot);

        try
        {
            // Register the exact pre-edit bytes so ApplyAsync can make a reversible backup.
            RegisterOriginalHash(snapshot, profilePath);
            RegisterOriginalHash(snapshot, i2mConfigPath);
            RegisterOriginalHash(snapshot, agentsPath);

            var profilesJson = BuildProfilesJson(profilePath, selectedProvider, selected);
            var i2mProfile = selectedProvider == "i2m" ? selected : Load("i2m");
            var i2mConfigJson = BuildI2MConfigJson(i2mConfigPath, i2mProfile);
            var agentsText = BuildAgentsText(agentsPath, selectedProvider);

            AddIfChanged(
                plan.Files,
                profilePath,
                profilesJson,
                "保存生图来源配置",
                "写入 active_provider 与 i2m/official profile 字段；不写入凭据内容。");
            AddIfChanged(
                plan.Files,
                i2mConfigPath,
                i2mConfigJson,
                "同步 i2m / 114514 配置",
                "只更新生图端点、模型、尺寸、质量、格式、超时和 credential_file 引用。");
            AddIfChanged(
                plan.Files,
                agentsPath,
                agentsText,
                "同步 AGENTS.md 生图受管理段落",
                "只替换默认生图受管理段落，保留其余内容。");
        }
        catch (FormatException ex)
        {
            plan.Errors.Add(ex.Message);
            plan.Files.Clear();
        }
        return plan;
    }

    private string ProfilePath => Path.Combine(_codexHome, ProfileRelativePath);

    private ImageProfile DefaultProfile(string provider)
    {
        if (provider == "official")
        {
            return new ImageProfile
            {
                Provider = "official",
                BaseUrl = "https://api.openai.com/v1",
                Model = OfficialDefaultModel,
                Size = "1024x1024",
                Quality = "high",
                Format = "png",
                Timeout = 600,
                CredentialFile = Path.Combine(_codexHome, "secrets", OfficialCredentialFileName)
            };
        }

        var config = ReadJsonObject(Path.Combine(_codexHome, I2MConfigRelativePath));
        var credentialFile = GetString(config, "credential_file", "credentialFile", "CredentialFile");
        return new ImageProfile
        {
            Provider = "i2m",
            BaseUrl = string.IsNullOrWhiteSpace(GetString(config, "base_url", "baseUrl"))
                ? "https://sub.114514.one/v1"
                : GetString(config, "base_url", "baseUrl")!,
            Model = string.IsNullOrWhiteSpace(GetString(config, "model", "Model"))
                ? I2MFallbackModel
                : GetString(config, "model", "Model")!,
            Size = string.IsNullOrWhiteSpace(GetString(config, "size", "Size")) ? "1024x1024" : GetString(config, "size", "Size")!,
            Quality = string.IsNullOrWhiteSpace(GetString(config, "quality", "Quality")) ? "high" : GetString(config, "quality", "Quality")!,
            Format = string.IsNullOrWhiteSpace(GetString(config, "output_format", "format", "Format")) ? "png" : GetString(config, "output_format", "format", "Format")!,
            Timeout = GetInt(config, 600, "timeout", "Timeout"),
            CredentialFile = string.IsNullOrWhiteSpace(credentialFile)
                ? Path.Combine(_codexHome, "secrets", I2MCredentialFileName)
                : credentialFile
        };
    }

    private static ImageProfile MergeProfile(ImageProfile defaults, JsonObject? saved)
    {
        if (saved is null)
        {
            return defaults;
        }

        return new ImageProfile
        {
            Provider = NormalizeProvider(GetString(saved, "provider", "Provider") ?? defaults.Provider),
            BaseUrl = GetString(saved, "base_url", "baseUrl", "BaseUrl") ?? defaults.BaseUrl,
            Model = GetString(saved, "model", "Model") ?? defaults.Model,
            Size = GetString(saved, "size", "Size") ?? defaults.Size,
            Quality = GetString(saved, "quality", "Quality") ?? defaults.Quality,
            Format = GetString(saved, "format", "output_format", "outputFormat", "Format") ?? defaults.Format,
            Timeout = GetInt(saved, defaults.Timeout, "timeout", "Timeout"),
            CredentialFile = GetString(saved, "credential_file", "credentialFile", "CredentialFile") ?? defaults.CredentialFile
        };
    }

    private static ImageProfile NormalizeProfile(ImageProfile profile, string provider)
    {
        return new ImageProfile
        {
            Provider = provider,
            BaseUrl = profile.BaseUrl?.Trim() ?? "",
            Model = profile.Model?.Trim() ?? "",
            Size = string.IsNullOrWhiteSpace(profile.Size) ? "1024x1024" : profile.Size.Trim(),
            Quality = string.IsNullOrWhiteSpace(profile.Quality) ? "high" : profile.Quality.Trim().ToLowerInvariant(),
            Format = string.IsNullOrWhiteSpace(profile.Format) ? "png" : profile.Format.Trim().ToLowerInvariant(),
            Timeout = profile.Timeout,
            CredentialFile = profile.CredentialFile?.Trim() ?? ""
        };
    }

    private void ValidateProfile(ImageProfile profile, List<string> errors)
    {
        if (profile.Provider == "i2m")
        {
            if (!string.Equals(profile.BaseUrl, "https://sub.114514.one/v1", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("i2m BaseUrl must be https://sub.114514.one/v1.");
            }
            if (profile.Quality == "xhigh")
            {
                errors.Add("i2m quality xhigh is not supported by the existing image_api.py.");
            }
        }
        else
        {
            if (!string.Equals(profile.BaseUrl, "https://api.openai.com/v1", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("official BaseUrl must be exactly https://api.openai.com/v1.");
            }
        }

        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            errors.Add("Model is required.");
        }
        if (!IsValidSize(profile.Size))
        {
            errors.Add("Size must be auto or WIDTHxHEIGHT.");
        }
        if (profile.Quality is not ("low" or "medium" or "high" or "xhigh" or "max" or "auto"))
        {
            errors.Add("Quality must be low, medium, high, xhigh, max, or auto.");
        }
        if (profile.Format is not ("png" or "jpeg" or "webp"))
        {
            errors.Add("Format must be png, jpeg, or webp.");
        }
        if (profile.Timeout is < 1 or > 1800)
        {
            errors.Add("Timeout must be between 1 and 1800 seconds.");
        }
        if (!string.IsNullOrWhiteSpace(profile.CredentialFile) && !IsCredentialReference(profile.CredentialFile))
        {
            errors.Add("CredentialFile must be an absolute Windows .dpapi path under .codex/secrets.");
        }
    }

    private bool IsCredentialReference(string value)
    {
        try
        {
            var path = Path.GetFullPath(value);
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".dpapi", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var allowedRoots = new[]
            {
                Path.Combine(_codexHome, "secrets"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "secrets")
            };
            return allowedRoots.Any(root => IsUnder(path, root));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidSize(string size)
    {
        if (string.Equals(size, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return System.Text.RegularExpressions.Regex.IsMatch(size, "^[1-9][0-9]{0,4}x[1-9][0-9]{0,4}$");
    }

    private string BuildProfilesJson(string path, string activeProvider, ImageProfile selected)
    {
        var root = ReadJsonObject(path);
        var profiles = GetObject(root, "profiles");
        if (profiles is null)
        {
            profiles = new JsonObject();
            root["profiles"] = profiles;
        }

        foreach (var provider in new[] { "i2m", "official" })
        {
            var profile = provider == activeProvider ? selected : Load(provider);
            var existing = FindProviderObject(profiles, provider) ?? new JsonObject();
            SetProfileFields(existing, profile);
            SetProviderObject(profiles, provider, existing);
        }

        root["active_provider"] = activeProvider;
        return root.ToJsonString(JsonOptions) + Environment.NewLine;
    }

    private string BuildI2MConfigJson(string path, ImageProfile profile)
    {
        var root = ReadJsonObject(path);
        root["base_url"] = profile.BaseUrl;
        root["model"] = profile.Model;
        root["credential_file"] = profile.CredentialFile;
        root["size"] = profile.Size;
        root["quality"] = profile.Quality;
        root["output_format"] = profile.Format;
        root["timeout"] = profile.Timeout;
        return root.ToJsonString(JsonOptions) + Environment.NewLine;
    }

    private string BuildAgentsText(string path, string activeProvider)
    {
        var original = File.Exists(path) ? File.ReadAllText(path) : "";
        const string start = "<!-- codex-control-center:image-generation:start -->";
        const string end = "<!-- codex-control-center:image-generation:end -->";
        var helper = Path.GetFullPath(Path.Combine(_codexHome, "skills", "i2m-imagegen", "scripts", "image_api.py"));
        var command = $"python \"{DispatcherPath}\" --codex-home \"{_codexHome}\" --prompt-file \"<绝对路径>\\prompt.txt\" --out \"<绝对路径>\\image.png\"";
        var section =
            start + Environment.NewLine +
            "## 默认生图接口（受管理）" + Environment.NewLine +
            Environment.NewLine +
            $"- 当前来源：{(activeProvider == "official" ? "official OpenAI Images API" : "i2m / 114514")}" + Environment.NewLine +
            "- 默认来源只读取 `codexHome/control-center/image-profiles.json` 的 `active_provider`。" + Environment.NewLine +
            $"- 统一调用命令（脚本与 Codex Home 均为绝对路径）：`{command}`" + Environment.NewLine +
            "- 参考图编辑在命令末尾追加可重复的 `--image <绝对路径>`；先用 `--dry-run` 做不计费测试。" + Environment.NewLine +
            $"- i2m helper（绝对路径）：`{helper}`。选择 i2m 时继续遵守 i2m-imagegen 技能约束。" + Environment.NewLine +
            "- 用户当次明确指定其他来源、模型或服务时，以当次要求为准；不得擅自切换服务或模型。" + Environment.NewLine +
            "- 失败、超时或结果不确定时不得自动重试付费请求，也不得自动改用其他付费来源；如实报告不确定性。" + Environment.NewLine +
            "- 代理不得读取、输出、解密或传播明文密钥；DPAPI 脚本只在实际发起请求时于内存中解密当前用户凭据。" + Environment.NewLine +
            "- 每次生成后必须检查并展示实际图片，核对格式与像素尺寸；不静默缩放，不覆盖已有输出。" + Environment.NewLine +
            "- 与当前用户任务无关的生成不因本段落获得自动授权。" + Environment.NewLine +
            end;

        var startIndex = original.IndexOf(start, StringComparison.Ordinal);
        var endIndex = original.IndexOf(end, StringComparison.Ordinal);
        if (startIndex >= 0 && endIndex > startIndex)
        {
            return original[..startIndex] + section + original[(endIndex + end.Length)..];
        }

        var legacy = System.Text.RegularExpressions.Regex.Match(
            original,
            @"(?m)^##[ \t]+默认生图接口[^\r\n]*(?:\r?\n)?");
        if (legacy.Success)
        {
            var headingPattern = new System.Text.RegularExpressions.Regex(@"(?m)^##[ \t]+");
            var nextHeading = headingPattern.Match(original, legacy.Index + legacy.Length);
            var legacyEnd = nextHeading.Success ? nextHeading.Index : original.Length;
            return original[..legacy.Index] + section + Environment.NewLine + original[legacyEnd..];
        }

        if (string.IsNullOrWhiteSpace(original))
        {
            return section + Environment.NewLine;
        }

        return original.TrimEnd() + Environment.NewLine + Environment.NewLine + section + Environment.NewLine;
    }

    private static void SetProfileFields(JsonObject target, ImageProfile profile)
    {
        target["provider"] = profile.Provider;
        target["base_url"] = profile.BaseUrl;
        target["model"] = profile.Model;
        target["size"] = profile.Size;
        target["quality"] = profile.Quality;
        target["format"] = profile.Format;
        target["timeout"] = profile.Timeout;
        target["credential_file"] = profile.CredentialFile;
    }

    private static void SetProviderObject(JsonObject profiles, string provider, JsonObject value)
    {
        profiles[provider] = value;
    }

    private static JsonObject? FindProviderObject(JsonObject? profiles, string provider)
    {
        if (profiles is null)
        {
            return null;
        }
        foreach (var pair in profiles)
        {
            if (NormalizeProvider(pair.Key) == provider && pair.Value is JsonObject value)
            {
                return value;
            }
        }
        return null;
    }

    private static void AddIfChanged(
        List<FileEdit> files,
        string path,
        string content,
        string description,
        string effect)
    {
        if (File.Exists(path) && PlannedContentEquals(path, File.ReadAllText(path), content))
        {
            return;
        }

        files.Add(new FileEdit(path, content, description, effect));
    }

    private static bool PlannedContentEquals(string path, string existing, string planned)
    {
        if (string.Equals(existing, planned, StringComparison.Ordinal))
        {
            return true;
        }

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var existingJson = JsonNode.Parse(existing)?.ToJsonString();
                var plannedJson = JsonNode.Parse(planned)?.ToJsonString();
                return string.Equals(existingJson, plannedJson, StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return string.Equals(
            existing.Replace("\r\n", "\n"),
            planned.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
    }

    private static void RegisterOriginalHash(WorkspaceSnapshot snapshot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var hash = File.Exists(fullPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)))
            : "<missing>";
        snapshot.FileHashes.TryAdd(fullPath, hash);
    }

    private static string ResolvePath(string? configured, string fallback)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? fallback : configured);
    }

    private string ResolveAgentsPath(WorkspaceSnapshot snapshot)
    {
        _ = snapshot;
        return Path.Combine(_codexHome, "AGENTS.md");
    }

    private static JsonObject ReadJsonObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new FormatException($"{path}: image config JSON root must be an object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{path}: invalid image config JSON.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FormatException($"{path}: image config JSON could not be read.", ex);
        }
    }

    private static JsonObject? GetObject(JsonObject? value, params string[] names)
    {
        if (value is null)
        {
            return null;
        }
        foreach (var name in names)
        {
            if (value.TryGetPropertyValue(name, out var node) && node is JsonObject child)
            {
                return child;
            }
        }
        return null;
    }

    private static string? GetString(JsonObject? value, params string[] names)
    {
        if (value is null)
        {
            return null;
        }
        foreach (var name in names)
        {
            if (value.TryGetPropertyValue(name, out var node) && node is JsonValue jsonValue)
            {
                try
                {
                    return jsonValue.GetValue<string>();
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return null;
    }

    private static int GetInt(JsonObject? value, int fallback, params string[] names)
    {
        if (value is null)
        {
            return fallback;
        }
        foreach (var name in names)
        {
            if (value.TryGetPropertyValue(name, out var node) && node is JsonValue jsonValue)
            {
                try
                {
                    return jsonValue.GetValue<int>();
                }
                catch (InvalidOperationException)
                {
                    if (int.TryParse(jsonValue.ToString(), out var parsed))
                    {
                        return parsed;
                    }
                }
            }
        }
        return fallback;
    }

    private static string NormalizeProvider(string provider)
    {
        var value = provider.Trim().ToLowerInvariant();
        return value switch
        {
            "i2m" or "114514" or "i2m/114514" or "i2m-114514" => "i2m",
            "official" or "openai" or "openai-images" => "official",
            _ => throw new ArgumentException($"Unsupported image provider: {provider}")
        };
    }
}


