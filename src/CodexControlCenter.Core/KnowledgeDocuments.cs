using System.Text;
using System.Text.RegularExpressions;

namespace CodexControlCenter.Core;

public enum DocumentCategory
{
    Instructions,
    Skills,
    Roles
}

public sealed record KnowledgeDocumentEntry(
    string Name,
    string Path,
    DocumentCategory Category,
    string Source,
    string Format,
    bool Editable,
    string Effect);

public sealed partial class ConfigurationService
{
    private const int MaxKnowledgeDocumentBytes = 2 * 1024 * 1024;
    private static readonly Regex PersonalSkillSlugPattern = new("^[a-z0-9][a-z0-9-]{1,60}$", RegexOptions.CultureInvariant);

    private sealed record KnowledgeDocumentCandidate(
        string Name,
        string Path,
        DocumentCategory Category,
        string Source,
        string Format,
        bool Editable,
        string Effect);

    public IReadOnlyList<KnowledgeDocumentEntry> GetKnowledgeDocuments(WorkspaceSnapshot snapshot)
        => DiscoverKnowledgeDocuments(snapshot,null);

    private IReadOnlyList<KnowledgeDocumentEntry> DiscoverKnowledgeDocuments(WorkspaceSnapshot snapshot,string? requestedPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = new List<KnowledgeDocumentCandidate>();
        bool Requested(string path)
        {
            if(requestedPath is null)return true;
            try{return string.Equals(GetFullPath(path),requestedPath,StringComparison.OrdinalIgnoreCase);}
            catch(Exception error) when(error is ArgumentException or IOException or NotSupportedException){return false;}
        }

        foreach (var markdown in snapshot.GlobalMarkdownFiles)
        {
            if(!Requested(markdown.Path))continue;
            if (!TryGetSafeFullPath(markdown.Path, out var fullPath) || !IsReadableMarkdown(fullPath))
                continue;

            var discovered = DescribeGlobalMarkdown(fullPath);
            string source = discovered.Source;
            bool editable = markdown.Editable && IsGlobalMarkdownPath(fullPath) &&
                !IsManagedKnowledgePath(fullPath) && !IsFileReadOnly(fullPath);
            candidates.Add(new KnowledgeDocumentCandidate(
                discovered.Name,
                fullPath,
                CategoryForSource(source),
                source,
                "Markdown",
                editable,
                discovered.Effect));
        }

        foreach (var skill in snapshot.Skills)
        {
            if(!Requested(skill.Path))continue;
            if (!TryGetSafeFullPath(skill.Path, out var fullPath) || !File.Exists(fullPath) ||
                !Path.GetFileName(fullPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                !IsSkillDocumentPath(fullPath))
                continue;

            string sharedRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_codexHome) ?? _codexHome, ".agents", "skills"));
            string systemRoot = Path.GetFullPath(Path.Combine(_codexHome, "skills", ".system"));
            string source = IsPathUnder(fullPath, systemRoot)
                ? "系统技能"
                : IsPathUnder(fullPath, sharedRoot) ? "共享技能" : "个人技能";
            bool editable = skill.Editable && IsGlobalMarkdownPath(fullPath) &&
                !IsManagedKnowledgePath(fullPath) && !IsFileReadOnly(fullPath);
            candidates.Add(new KnowledgeDocumentCandidate(
                skill.Name,
                fullPath,
                DocumentCategory.Skills,
                source,
                "Markdown",
                editable,
                "按技能引用与 Codex 的技能加载规则生效。"));
        }

        string rolesDirectory = Path.Combine(_codexHome, "agents");
        if (Directory.Exists(rolesDirectory) && IsPathFreeOfLinks(rolesDirectory))
        {
            try
            {
                foreach (string rolePath in Directory.EnumerateFiles(rolesDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if(!Requested(rolePath))continue;
                    if (!Path.GetExtension(rolePath).Equals(".toml", StringComparison.OrdinalIgnoreCase) ||
                        !TryGetSafeFullPath(rolePath, out var fullPath))
                        continue;

                    bool canHash = EnsureKnowledgeHash(snapshot, fullPath);
                    bool editable = canHash && !IsFileReadOnly(fullPath);
                    candidates.Add(new KnowledgeDocumentCandidate(
                        Path.GetFileName(fullPath),
                        fullPath,
                        DocumentCategory.Roles,
                        "角色文件",
                        "TOML",
                        editable,
                        "Codex 重启后生效。"));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An inaccessible role directory contributes no entries; other snapshot sources remain usable.
            }
        }

        var grouped = candidates
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var selected = group
                    .OrderBy(candidate => candidate.Editable)
                    .ThenBy(candidate => candidate.Source, StringComparer.OrdinalIgnoreCase)
                    .First();
                bool editable = group.All(candidate => candidate.Editable) &&
                    !IsManagedKnowledgePath(selected.Path) && !IsFileReadOnly(selected.Path);
                return new KnowledgeDocumentEntry(
                    selected.Name,
                    selected.Path,
                    selected.Category,
                    selected.Source,
                    selected.Format,
                    editable,
                    selected.Effect);
            })
            .ToList();

        var duplicateNames = grouped
            .GroupBy(entry => Path.GetFileName(entry.Path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(entry => entry.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Single-file reads still use the same visible name as the full list;
        // detect name collisions from snapshot metadata without filesystem scans.
        if(requestedPath is not null&&snapshot.GlobalMarkdownFiles.Select(entry=>entry.Path)
            .Concat(snapshot.Skills.Select(entry=>entry.Path))
            .Where(path=>Path.GetFileName(path).Equals(Path.GetFileName(requestedPath),StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count()>1)duplicateNames.Add(requestedPath);

        return grouped
            .Select(entry => duplicateNames.Contains(entry.Path)
                ? entry with { Name = GetRelativeKnowledgeName(entry) }
                : entry with { Name = Path.GetFileName(entry.Path) })
            .OrderBy(entry => entry.Category)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public KnowledgeDocumentEntry DescribeKnowledgeDocument(WorkspaceSnapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string fullPath = GetFullPath(path);
        return DescribeKnowledgeDocument(snapshot, fullPath, DiscoverKnowledgeDocuments(snapshot,fullPath));
    }

    public string ReadKnowledgeDocument(WorkspaceSnapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var document = DescribeKnowledgeDocument(snapshot, path);
        if (!TryGetSafeFullPath(document.Path, out var fullPath))
            throw new UnauthorizedAccessException($"Document path is unsafe: {document.Path}");

        try
        {
            if (File.Exists(fullPath) && new FileInfo(fullPath).Length > MaxKnowledgeDocumentBytes)
                throw new IOException($"Document exceeds 2 MiB: {fullPath}");

            EnsureKnowledgeHash(snapshot, fullPath);
            return ReadDocument(snapshot, fullPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Unable to read document {fullPath}: {error.Message}", error);
        }
    }

    public ChangePlan PlanKnowledgeDocuments(WorkspaceSnapshot snapshot, IReadOnlyDictionary<string, string> changes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var plan = new ChangePlan();
        if (changes is null)
        {
            plan.Errors.Add("Document changes are required.");
            return plan;
        }

        var resolved = new List<(string InputPath, string FullPath, string Content)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            string fullPath;
            try
            {
                fullPath = GetFullPath(change.Key);
            }
            catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
            {
                plan.Errors.Add($"Invalid document path '{change.Key}': {error.Message}");
                continue;
            }

            if (!seenPaths.Add(fullPath))
            {
                plan.Errors.Add($"Ambiguous duplicate document path: {fullPath}");
                continue;
            }

            resolved.Add((change.Key, fullPath, change.Value));
        }

        var documents = GetKnowledgeDocuments(snapshot);
        foreach (var change in resolved)
        {
            try
            {
                var document = DescribeKnowledgeDocument(snapshot, change.FullPath, documents);
                if (!document.Editable)
                {
                    plan.Errors.Add($"Document is read-only: {change.FullPath}");
                    continue;
                }

                if (!TryGetSafeFullPath(change.FullPath, out var safePath))
                {
                    plan.Errors.Add($"Document path contains a symbolic link: {change.FullPath}");
                    continue;
                }

                if (change.Content is null)
                {
                    plan.Errors.Add($"Document content is missing: {change.FullPath}");
                    continue;
                }

                if (Encoding.UTF8.GetByteCount(change.Content) > MaxKnowledgeDocumentBytes)
                {
                    plan.Errors.Add($"Document content exceeds 2 MiB: {safePath}");
                    continue;
                }

                EnsureKnowledgeHash(snapshot, safePath);
                var itemPlan = document.Format.Equals("Markdown", StringComparison.OrdinalIgnoreCase)
                    ? PlanGlobalMarkdown(snapshot, safePath, change.Content)
                    : PlanText(snapshot, safePath, change.Content, $"编辑角色文件 · {document.Name}");

                if (itemPlan.Errors.Count > 0)
                {
                    plan.Errors.AddRange(itemPlan.Errors.Select(error => $"{safePath}: {error}"));
                    continue;
                }

                plan.Files.AddRange(itemPlan.Files);
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
                plan.Errors.Add($"{change.FullPath}: {error.Message}");
            }
        }

        if (plan.Errors.Count > 0)
            plan.Files.Clear();

        return plan;
    }

    public string NewPersonalSkillPath(string slug)
    {
        if (slug is null || !PersonalSkillSlugPattern.IsMatch(slug))
            throw new ArgumentException("Skill slug must contain 2 to 61 lowercase letters, digits, or hyphens and start with a letter or digit.", nameof(slug));

        return Path.GetFullPath(Path.Combine(_codexHome, "skills", slug, "SKILL.md"));
    }

    private KnowledgeDocumentEntry DescribeKnowledgeDocument(
        WorkspaceSnapshot snapshot,
        string fullPath,
        IReadOnlyList<KnowledgeDocumentEntry> documents)
    {
        if (!TryGetSafeFullPath(fullPath, out var safePath))
            throw new UnauthorizedAccessException($"Document path contains a symbolic link: {fullPath}");

        var existing = documents.FirstOrDefault(document =>
            string.Equals(document.Path, safePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            EnsureKnowledgeHash(snapshot, safePath);
            return existing;
        }

        if (IsPersonalSkillPath(safePath, out var slug) && !File.Exists(safePath))
        {
            EnsureKnowledgeHash(snapshot, safePath);
            return new KnowledgeDocumentEntry(slug, safePath, DocumentCategory.Skills, "个人技能", "Markdown", true,
                "按技能引用与 Codex 的技能加载规则生效。");
        }

        if (!File.Exists(safePath) && safePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && IsGlobalMarkdownPath(safePath))
        {
            EnsureKnowledgeHash(snapshot, safePath);
            var markdown = DescribeGlobalMarkdown(safePath);
            bool editable = markdown.Editable && !IsManagedKnowledgePath(safePath);
            return new KnowledgeDocumentEntry(markdown.Name, safePath, CategoryForSource(markdown.Source), markdown.Source,
                "Markdown", editable, markdown.Effect);
        }

        throw new UnauthorizedAccessException($"Document is outside the discovered knowledge-document scope: {safePath}");
    }

    private bool EnsureKnowledgeHash(WorkspaceSnapshot snapshot, string path)
    {
        if (FindHash(snapshot, path) is not null)
            return true;

        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxKnowledgeDocumentBytes)
                return false;
            AddHash(snapshot, path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryGetSafeFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = GetFullPath(path);
            return IsPathFreeOfLinks(fullPath);
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            fullPath = "";
            return false;
        }
    }

    private static string GetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A document path is required.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static bool IsPathFreeOfLinks(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return false;

            for (string? directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                 !string.IsNullOrEmpty(directory);
                 directory = Path.GetDirectoryName(directory))
            {
                if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return false;
                string? parent = Path.GetDirectoryName(directory);
                if (parent is null || parent.Equals(directory, StringComparison.OrdinalIgnoreCase))
                    break;
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool IsPersonalSkillPath(string path, out string slug)
    {
        slug = "";
        string root = Path.GetFullPath(Path.Combine(_codexHome, "skills"));
        string relative = Path.GetRelativePath(root, path);
        string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length != 2 || !parts[1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
            !PersonalSkillSlugPattern.IsMatch(parts[0]) || IsManagedKnowledgePath(path))
            return false;

        slug = parts[0];
        return true;
    }

    private bool IsSkillDocumentPath(string path)
    {
        string personalRoot = Path.GetFullPath(Path.Combine(_codexHome, "skills"));
        string sharedRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_codexHome) ?? _codexHome, ".agents", "skills"));
        return (IsPathUnder(path, personalRoot) || IsPathUnder(path, sharedRoot)) &&
            Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsManagedKnowledgePath(string path)
    {
        string systemSkills = Path.GetFullPath(Path.Combine(_codexHome, "skills", ".system"));
        string pluginCache = Path.GetFullPath(Path.Combine(_codexHome, "plugins", "cache"));
        return IsPathUnder(path, systemSkills) || IsPathUnder(path, pluginCache);
    }

    private static bool IsPathUnder(string path, string root)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsFileReadOnly(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private string GetRelativeKnowledgeName(KnowledgeDocumentEntry entry)
    {
        if (entry.Source.Equals("项目指令", StringComparison.OrdinalIgnoreCase))
        {
            var original = snapshotProjectName(entry.Path);
            if (!string.IsNullOrEmpty(original))
                return original;
        }

        string sharedRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_codexHome) ?? _codexHome, ".agents", "skills"));
        if (IsPathUnder(entry.Path, sharedRoot))
            return Path.Combine(".agents", "skills", Path.GetRelativePath(sharedRoot, entry.Path));
        if (IsPathUnder(entry.Path, _codexHome))
            return Path.GetRelativePath(_codexHome, entry.Path);
        return entry.Name;

        string snapshotProjectName(string documentPath)
        {
            var registered = registeredMarkdown.TryGetValue(documentPath, out var markdown) ? markdown : null;
            return registered?.Name ?? "";
        }
    }

    private static DocumentCategory CategoryForSource(string source) => source switch
    {
        "个人技能" or "共享技能" or "系统技能" or "插件文档" or ".codex/skills" or ".agents/skills" => DocumentCategory.Skills,
        "角色文件" => DocumentCategory.Roles,
        _ => DocumentCategory.Instructions
    };
}
