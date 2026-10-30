using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CodexControlCenter.Core;

public sealed record WallpaperSettings
{
    public bool Enabled { get; init; } = false;
    public string? ImageId { get; init; }
    public string Fit { get; init; } = "cover";
    public double PositionX { get; init; } = 50;
    public double PositionY { get; init; } = 50;
    // Retain legacy opacity for older saved files. New controls save Darkness;
    // legacy 70% intensity is interpreted as 30% dimming without a destructive rewrite.
    public double Opacity { get; init; } = .8;
    public double? Darkness { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double Dimming => Darkness ?? 1-Opacity;
    public double Blur { get; init; } = 0;
    public double Overlay { get; init; } = .35;
    public double Zoom { get; init; } = 1;
}

public sealed record WallpaperAsset(string Id, string Name, string Path, int Width, int Height)
{
    /// <summary>受管理原图的字节数。</summary>
    public long ByteLength { get; init; }

    /// <summary>导入时间；旧库缺少该字段时回退为元数据文件的写入时间。</summary>
    public DateTimeOffset ImportedAt { get; init; }

    /// <summary>是否被收藏。</summary>
    public bool IsFavorite { get; init; }

    /// <summary>缩略图的绝对路径；尚未生成时为 null，调用方应回退到原图。</summary>
    public string? ThumbnailPath { get; init; }
}

public sealed class WallpaperLibrary
{
    private const long MaximumFileBytes = 24L * 1024 * 1024;
    private const long MaximumPixels = 64L * 1024 * 1024;
    private const int MaximumDimension = 16_384;
    private const int MaximumThumbnailDimension = 480;
    private const int MaximumDisplayNameLength = 100;
    private const string ThumbnailJpegName = "thumbnail.jpg";
    private const string ThumbnailPngName = "thumbnail.png";
    private const string MissingRevision = "missing";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> AllowedFitValues =
        new(StringComparer.OrdinalIgnoreCase) { "cover", "contain" };
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public WallpaperLibrary(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("壁纸状态目录不能为空。", nameof(root));
        }

        Root = Path.GetFullPath(root);
        SettingsPath = Path.Combine(Root, "wallpaper.json");
    }

    public string Root { get; }
    public string SettingsPath { get; }

    private string LibraryPath => Path.Combine(Root, "library");
    private string SettingsLockPath => Path.Combine(Root, ".wallpaper-settings.lock");
    private string LibraryLockPath => Path.Combine(Root, ".wallpaper-library.lock");

    public WallpaperSettings LoadSettings()
    {
        EnsureSafeRoot();
        using var settingsLock = AcquireLock(SettingsLockPath);
        return LoadSettingsCore();
    }

    public string SettingsRevision()
    {
        EnsureSafeRoot();
        using var settingsLock = AcquireLock(SettingsLockPath);
        return SettingsRevisionCore();
    }

    public (WallpaperSettings Settings, string Revision) LoadSnapshot()
    {
        EnsureSafeRoot();
        using var settingsLock = AcquireLock(SettingsLockPath);
        return LoadSnapshotCore();
    }

    public void SaveSettings(WallpaperSettings settings, string expectedRevision)
        => _ = SaveSnapshot(settings, expectedRevision);

    public (WallpaperSettings Settings, string Revision) SaveSnapshot(WallpaperSettings settings, string expectedRevision)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings), "壁纸设置不能为空。");
        }

        if (expectedRevision is null)
        {
            throw new ArgumentNullException(nameof(expectedRevision), "壁纸设置版本不能为空。");
        }

        Validate(settings);
        EnsureSafeRoot();

        using var settingsLock = AcquireLock(SettingsLockPath);
        var currentSnapshot = LoadSnapshotCore();
        if (!string.Equals(currentSnapshot.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("壁纸设置已被其他操作更新，请重新载入后再试。");
        }

        WallpaperSettings normalized = settings with
        {
            Fit = settings.Fit.Trim().ToLowerInvariant()
        };

        if (normalized.ImageId is not null)
        {
            // Keep the global order settings -> library for save and delete operations.
            using var libraryLock = AcquireLock(LibraryLockPath);
            if (!TryLoadAsset(normalized.ImageId, out _, out _))
            {
                throw new InvalidOperationException("壁纸设置引用了不存在或已损坏的图片。");
            }
        }

        return (normalized, SaveSettingsCore(normalized));
    }

    public (WallpaperSettings Settings,string Revision) RecoverSettings(bool useBackup,string expectedRevision)
    {
        EnsureSafeRoot();using var settingsLock=AcquireLock(SettingsLockPath);
        byte[]? current=ReadSettingsBytes();string revision=current is null?MissingRevision:Hash(current);
        if(revision!=expectedRevision)throw new InvalidOperationException("壁纸设置已被其他操作更新，请重新载入后再试。");
        WallpaperSettings candidate=new();
        if(useBackup){
            string backup=SettingsPath+".bak";EnsureNoReparsePoint(backup);
            if(!File.Exists(backup))throw new FileNotFoundException("没有可恢复的上次壁纸设置。",backup);
            try{candidate=JsonSerializer.Deserialize<WallpaperSettings>(ReadAllBytesShared(backup,4L*1024*1024),SettingsJsonOptions)??throw new InvalidDataException("壁纸备份为空。");Validate(candidate);}
            catch(Exception error)when(error is JsonException or ArgumentException){throw new InvalidDataException("上次壁纸设置也无法读取，可选择重置；图片副本会保留。",error);}
        }
        using var libraryLock=AcquireLock(LibraryLockPath);
        if(candidate.ImageId is not null&&!TryLoadAsset(candidate.ImageId,out _,out _))throw new InvalidOperationException("备份引用的图片已丢失，请选择重置壁纸设置后重新选图。");
        // Always preserve the exact replaced bytes; leave the last valid .bak untouched during recovery.
        if(current is not null)WriteNewFileDurably(SettingsPath+".recovered-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".json",current);
        return (candidate,SaveSettingsCore(candidate,preserveBackup:true));
    }

    public IReadOnlyList<WallpaperAsset> List()
    {
        EnsureSafeRoot();
        using var libraryLock = AcquireLock(LibraryLockPath);
        EnsureSafeLibrary(create: true);
        CleanupDeletedEntries();
        var assets = new List<WallpaperAsset>();
        var liveIds = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(LibraryPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            throw new IOException("无法读取壁纸图片库。", ex);
        }

        foreach (string directory in directories)
        {
            string id = Path.GetFileName(directory);
            if (!IsAssetId(id))
            {
                continue;
            }

            try
            {
                if (TryLoadDescriptor(id, out WallpaperAsset? asset))
                {
                    assets.Add(asset!);
                    liveIds.Add(id);
                }
            }
            catch (Exception ex) when (IsExpectedFileError(ex))
            {
                // One damaged or replaced entry must not make the rest of the library unusable.
            }
        }

        // Entries that vanished from disk must never resurface from the descriptor cache.
        WallpaperAssetCache.Prune(Root, liveIds);

        return assets
            .OrderBy(asset => asset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> UnavailableImageIds()
    {
        var available=List().Select(a=>a.Id).ToHashSet();EnsureSafeRoot();using var held=AcquireLock(LibraryLockPath);EnsureSafeLibrary(create:true);
        return Directory.EnumerateDirectories(LibraryPath,"*",SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Where(id=>IsAssetId(id)&&!available.Contains(id!)).Cast<string>().Take(100).ToArray();
    }

    public Task<WallpaperAsset> ImportAsync(string file) => ImportAsync(file, CancellationToken.None);

    public async Task<WallpaperAsset> ImportAsync(string file, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            throw new ArgumentException("壁纸图片路径不能为空。", nameof(file));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = Path.GetFullPath(file);
        byte[] bytes = ReadBoundedSource(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        DecodedImage decoded = await DecodeAndValidateAsync(bytes, sourcePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string id = Hash(bytes);
        EnsureSafeRoot();

        using var libraryLock = AcquireLock(LibraryLockPath);
        EnsureSafeLibrary(create: true);
        if (TryLoadAsset(id, out WallpaperAsset? existing, out _))
        {
            return existing!;
        }

        string assetDirectory = GetAssetDirectory(id);
        if (Directory.Exists(assetDirectory) || File.Exists(assetDirectory))
        {
            throw new InvalidDataException("图片库中已有同内容标识的损坏条目，无法安全导入。");
        }

        string stagingDirectory = Path.Combine(LibraryPath, ".import-" + Guid.NewGuid().ToString("N"));
        string fileName = "image" + decoded.Format.Extension;
        string imagePath = Path.Combine(stagingDirectory, fileName);
        string metadataPath = Path.Combine(stagingDirectory, "asset.json");
        string originalName = Path.GetFileName(sourcePath);
        string? thumbnailFileName = null;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            WriteNewFileDurably(imagePath, bytes);

            // The thumbnail is best effort: a failure keeps the imported original and only
            // defers the thumbnail to EnsureThumbnailAsync, it never leaves a partial file.
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThumbnailImage thumbnail = await CreateThumbnailAsync(bytes, sourcePath, cancellationToken)
                    .ConfigureAwait(false);
                thumbnailFileName = thumbnail.Opaque ? ThumbnailJpegName : ThumbnailPngName;
                WriteNewFileDurably(Path.Combine(stagingDirectory, thumbnailFileName), thumbnail.Bytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsExpectedFileError(ex))
            {
                thumbnailFileName = null;
            }

            var metadata = new AssetMetadata(new JsonObject())
            {
                Id = id,
                Name = originalName,
                FileName = fileName,
                MediaType = decoded.Format.MediaType,
                Width = decoded.Width,
                Height = decoded.Height,
                OriginalName = originalName,
                ByteLength = bytes.LongLength,
                ImportedAt = DateTimeOffset.UtcNow,
                IsFavorite = false,
                ThumbnailFileName = thumbnailFileName
            };
            byte[] metadataBytes = SerializeMetadata(metadata);
            WriteNewFileDurably(metadataPath, metadataBytes);
            EnsureNoReparsePoint(stagingDirectory);
            EnsureNoReparsePoint(imagePath);
            EnsureNoReparsePoint(metadataPath);

            // The directory rename is the atomic publish step for both image and metadata.
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingDirectory, assetDirectory);
            string publishedImagePath = Path.Combine(assetDirectory, fileName);
            string? publishedThumbnailPath = thumbnailFileName is null
                ? null
                : Path.Combine(assetDirectory, thumbnailFileName);
            var published = new WallpaperAsset(id, metadata.Name, publishedImagePath, decoded.Width, decoded.Height)
            {
                ByteLength = bytes.LongLength,
                ImportedAt = metadata.ImportedAt!.Value,
                IsFavorite = false,
                ThumbnailPath = publishedThumbnailPath
            };
            WallpaperAssetCache.Invalidate(Root, id);
            return published;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try
                {
                    DeleteManagedEntry(stagingDirectory);
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

    public void Delete(string id)
    {
        ValidateAssetId(id);
        EnsureSafeRoot();
        using var settingsLock = AcquireLock(SettingsLockPath);
        _ = DeleteWithSettingsLock(id, expectedRevision: null);
    }

    /// <summary>
    /// 修改图片的显示名称。原始文件名保留在元数据中，不会被覆盖，也不会影响原图内容。
    /// </summary>
    public WallpaperAsset Rename(string id, string name)
    {
        ValidateAssetId(id);
        string displayName = NormalizeDisplayName(name);
        EnsureSafeRoot();

        // Metadata-only edits keep the global settings -> library lock order so that they can
        // never interleave with a save or a delete, and they never touch the settings revision.
        using var settingsLock = AcquireLock(SettingsLockPath);
        using var libraryLock = AcquireLock(LibraryLockPath);
        EnsureSafeLibrary(create: false);
        return UpdateMetadata(id, metadata => metadata.Name = displayName);
    }

    /// <summary>标记或取消收藏图片。</summary>
    public WallpaperAsset SetFavorite(string id, bool favorite)
    {
        ValidateAssetId(id);
        EnsureSafeRoot();

        using var settingsLock = AcquireLock(SettingsLockPath);
        using var libraryLock = AcquireLock(LibraryLockPath);
        EnsureSafeLibrary(create: false);
        return UpdateMetadata(id, metadata => metadata.IsFavorite = favorite);
    }

    /// <summary>
    /// 确保图片存在缩略图并返回其路径。旧版导入的图片没有缩略图时按需生成一次。
    /// 生成失败时抛出 <see cref="InvalidDataException"/>，原图与既有条目保持不变。
    /// </summary>
    public async Task<string> EnsureThumbnailAsync(string id, CancellationToken cancellationToken = default,bool rebuild=false)
    {
        ValidateAssetId(id);
        EnsureSafeRoot();

        // Only the library lock is taken here: a thumbnail never touches the settings, and the
        // renderer polls the settings every two seconds and must not wait for an image re-encode.
        // The library lock still serializes the work against a save or a delete of the same entry.
        using var libraryLock = AcquireLock(LibraryLockPath);
        EnsureSafeLibrary(create: false);

        string directory = GetAssetDirectory(id);
        if (!Directory.Exists(directory))
        {
            throw new FileNotFoundException("没有找到指定的壁纸图片。", directory);
        }

        EnsureNoReparsePoint(directory);
        string metadataPath = Path.Combine(directory, "asset.json");
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("没有找到指定的壁纸图片。", id);
        }

        EnsureNoReparsePoint(metadataPath);
        AssetMetadata metadata = ReadMetadata(ReadAllBytesShared(metadataPath, 64 * 1024)) ??
            throw new InvalidDataException("壁纸图片的元数据已损坏。");
        if (!IsMetadataValid(metadata, id))
        {
            throw new InvalidDataException("壁纸图片的元数据已损坏。");
        }

        string imagePath = Path.Combine(directory, metadata.FileName);
        EnsureContainedPath(directory, imagePath);
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("没有找到指定的壁纸图片。", id);
        }

        EnsureNoReparsePoint(imagePath);

        // A thumbnail that is already published is reused, the image itself is never re-encoded.
        if (!rebuild&&metadata.ThumbnailFileName is not null && IsThumbnailFileName(metadata.ThumbnailFileName))
        {
            string existing = Path.Combine(directory, metadata.ThumbnailFileName);
            EnsureContainedPath(directory, existing);
            if (File.Exists(existing))
            {
                EnsureNoReparsePoint(existing);
                if (new FileInfo(existing).Length > 0)
                {
                    WallpaperAssetCache.Invalidate(Root, id);
                    return existing;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] imageBytes = ReadAllBytesShared(imagePath, MaximumFileBytes + 1);
        ThumbnailImage thumbnail = await CreateThumbnailAsync(imageBytes, imagePath, cancellationToken)
            .ConfigureAwait(false);
        string thumbnailFileName = thumbnail.Opaque ? ThumbnailJpegName : ThumbnailPngName;
        string thumbnailPath = Path.Combine(directory, thumbnailFileName);
        EnsureContainedPath(directory, thumbnailPath);

        // Publish the thumbnail atomically so that a failure never leaves a partial file behind.
        string temporaryPath = thumbnailPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNewFileDurably(temporaryPath, thumbnail.Bytes);
            if (File.Exists(thumbnailPath))
            {
                EnsureNoReparsePoint(thumbnailPath);
                File.Move(temporaryPath,thumbnailPath,true);
            }
            else File.Move(temporaryPath, thumbnailPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        metadata.ThumbnailFileName = thumbnailFileName;
        metadata.ByteLength ??= imageBytes.LongLength;
        WriteMetadataAtomically(metadataPath, SerializeMetadata(metadata));
        WallpaperAssetCache.Invalidate(Root, id);
        return thumbnailPath;
    }

    public (WallpaperSettings Settings, string Revision) Delete(string id, string expectedRevision)
    {
        ValidateAssetId(id);
        if (expectedRevision is null)
        {
            throw new ArgumentNullException(nameof(expectedRevision), "壁纸设置版本不能为空。");
        }

        EnsureSafeRoot();
        using var settingsLock = AcquireLock(SettingsLockPath);
        return DeleteWithSettingsLock(id, expectedRevision);
    }

    private (WallpaperSettings Settings, string Revision) DeleteWithSettingsLock(
        string id,
        string? expectedRevision)
    {
        (WallpaperSettings settings, string revision) = LoadSnapshotCore();
        if (expectedRevision is not null &&
            !string.Equals(revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("壁纸设置已被其他操作更新，请重新载入后再试。");
        }

        if (settings.Enabled && string.Equals(settings.ImageId, id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("当前启用的壁纸不能删除，请先关闭壁纸或选择其他图片。");
        }

        using var libraryLock = AcquireLock(LibraryLockPath);
        EnsureSafeLibrary(create: false);
        string assetDirectory = GetAssetDirectory(id);
        if (!Directory.Exists(assetDirectory))
        {
            throw new FileNotFoundException("没有找到指定的壁纸图片。", assetDirectory);
        }

        EnsureNoReparsePoint(assetDirectory);
        ValidateManagedEntry(assetDirectory);
        string staged=Path.Combine(LibraryPath,".deleted-"+id+"-"+Guid.NewGuid().ToString("N"));
        Directory.Move(assetDirectory,staged);
        try{
            if(string.Equals(settings.ImageId,id,StringComparison.Ordinal)){
                settings=settings with{ImageId=null};revision=SaveSettingsCore(settings);
            }
        }catch{Directory.Move(staged,assetDirectory);throw;}
        // Renaming publishes deletion. A thumbnail may still hold a file briefly; reclaim it on the next list.
        try{DeleteManagedEntry(staged);}catch(IOException){}catch(UnauthorizedAccessException){}
        return (settings,revision);
    }

    public string GetDataUri(string id)
    {
        ValidateAssetId(id);

        EnsureSafeRoot();
        using var libraryLock = AcquireLock(LibraryLockPath);
        EnsureSafeLibrary(create: false);
        if (!TryLoadAsset(id, out _, out LoadedAsset? loaded))
        {
            throw new FileNotFoundException("没有找到指定的壁纸图片，或图片已损坏。", id);
        }

        return $"data:{loaded!.Format.MediaType};base64,{Convert.ToBase64String(loaded.Bytes)}";
    }

    public static void Validate(WallpaperSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings), "壁纸设置不能为空。");
        }

        if (settings.ImageId is not null && !IsAssetId(settings.ImageId))
        {
            throw new ArgumentException("壁纸图片标识必须是 64 位小写 SHA-256 字符串。", nameof(settings));
        }

        if (settings.Enabled && settings.ImageId is null)
        {
            throw new ArgumentException("启用壁纸时必须选择一张图片。", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Fit) || !AllowedFitValues.Contains(settings.Fit.Trim()))
        {
            throw new ArgumentOutOfRangeException(nameof(settings.Fit), "壁纸缩放方式只支持 cover 或 contain。");
        }

        ValidateRange(settings.PositionX, 0, 100, "水平位置", nameof(settings.PositionX));
        ValidateRange(settings.PositionY, 0, 100, "垂直位置", nameof(settings.PositionY));
        ValidateRange(settings.Opacity, 0, 1, "图片不透明度", nameof(settings.Opacity));
        if(settings.Darkness is double darkness)ValidateRange(darkness,0,1,"压暗程度",nameof(settings.Darkness));
        ValidateRange(settings.Blur, 0, 64, "模糊半径", nameof(settings.Blur));
        ValidateRange(settings.Overlay, 0, 1, "遮罩强度", nameof(settings.Overlay));
        ValidateRange(settings.Zoom, 1, 3, "图片缩放比例", nameof(settings.Zoom));
    }

    private WallpaperSettings LoadSettingsCore()
    {
        return LoadSnapshotCore().Settings;
    }

    private (WallpaperSettings Settings, string Revision) LoadSnapshotCore()
    {
        byte[]? bytes = ReadSettingsBytes();
        if (bytes is null)
        {
            return (new WallpaperSettings(), MissingRevision);
        }

        try
        {
            WallpaperSettings? settings =
                JsonSerializer.Deserialize<WallpaperSettings>(bytes, SettingsJsonOptions);
            if (settings is null)
            {
                throw new InvalidDataException("壁纸设置文件必须是 JSON 对象。");
            }

            Validate(settings);
            return (settings with{Fit=settings.Fit.Trim().ToLowerInvariant()}, Hash(bytes));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("壁纸设置文件已损坏，服务已保留原文件。", ex);
        }
    }

    private string SettingsRevisionCore()
    {
        byte[]? bytes = ReadSettingsBytes();
        return bytes is null ? MissingRevision : Hash(bytes);
    }

    private byte[]? ReadSettingsBytes()
    {
        if (!File.Exists(SettingsPath))
        {
            if (Directory.Exists(SettingsPath))
            {
                throw new InvalidDataException("壁纸设置路径指向了目录。");
            }

            return null;
        }

        EnsureNoReparsePoint(SettingsPath);
        try
        {
            return ReadAllBytesShared(SettingsPath, 4L * 1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("无法读取壁纸设置文件。", ex);
        }
    }

    private string SaveSettingsCore(WallpaperSettings settings,bool preserveBackup=false)
    {
        Directory.CreateDirectory(Root);
        EnsureNoReparsePoint(Root);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonOptions);
        string temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string backupPath = SettingsPath + ".bak";

        try
        {
            WriteNewFileDurably(temporaryPath, content);
            if (File.Exists(SettingsPath))
            {
                EnsureNoReparsePoint(SettingsPath);
                if (File.Exists(backupPath))
                {
                    EnsureNoReparsePoint(backupPath);
                }

                File.Replace(temporaryPath, SettingsPath, preserveBackup?null:backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                if (Directory.Exists(SettingsPath))
                {
                    throw new InvalidOperationException("壁纸设置路径指向了目录。");
                }

                File.Move(temporaryPath, SettingsPath, overwrite: false);
            }
            return Hash(content);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private bool TryLoadAsset(string id, out WallpaperAsset? asset, out LoadedAsset? loaded)
    {
        asset = null;
        loaded = null;
        if (!IsAssetId(id))
        {
            return false;
        }

        string directory = GetAssetDirectory(id);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            EnsureNoReparsePoint(directory);
            string metadataPath = Path.Combine(directory, "asset.json");
            if (!File.Exists(metadataPath))
            {
                return false;
            }

            EnsureNoReparsePoint(metadataPath);
            byte[] metadataBytes = ReadAllBytesShared(metadataPath, 64 * 1024);
            AssetMetadata? metadata = ReadMetadata(metadataBytes);
            if (metadata is null || !IsMetadataValid(metadata, id))
            {
                return false;
            }

            if (!TryGetFormat(metadata.FileName, out ActualFormat? expectedFormat) || expectedFormat is null)
            {
                return false;
            }

            string imagePath = Path.Combine(directory, metadata.FileName);
            EnsureContainedPath(directory, imagePath);
            if (!File.Exists(imagePath))
            {
                return false;
            }

            EnsureNoReparsePoint(imagePath);
            byte[] bytes = ReadAllBytesShared(imagePath, MaximumFileBytes + 1);
            if (bytes.LongLength > MaximumFileBytes || !string.Equals(Hash(bytes), id, StringComparison.Ordinal))
            {
                return false;
            }

            DecodedImage decoded = DecodeAndValidateAsync(bytes, imagePath, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!string.Equals(decoded.Format.MediaType, metadata.MediaType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(decoded.Format.MediaType, expectedFormat.MediaType, StringComparison.OrdinalIgnoreCase) ||
                decoded.Width != metadata.Width ||
                decoded.Height != metadata.Height)
            {
                return false;
            }

            loaded = new LoadedAsset(bytes, decoded.Format);
            asset = BuildAsset(id, directory, metadata, metadataPath);
            return true;
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a lightweight descriptor for the library list. The original image is never hashed or
    /// decoded here, the descriptor is validated against the recorded size and the file stamps
    /// instead, and the result is cached until the metadata or the image changes.
    /// </summary>
    private bool TryLoadDescriptor(string id, out WallpaperAsset? asset)
    {
        asset = null;
        if (!IsAssetId(id))
        {
            return false;
        }

        string directory = GetAssetDirectory(id);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        EnsureNoReparsePoint(directory);
        string metadataPath = Path.Combine(directory, "asset.json");
        if (!File.Exists(metadataPath))
        {
            return false;
        }

        EnsureNoReparsePoint(metadataPath);
        FileInfo metadataInfo = new(metadataPath);

        // Reuse the cached descriptor while the metadata and the image keep the same length and
        // the same modification time. The original is never hashed here, GetDataUri and the save
        // path still verify the content hash before the image is used.
        if (WallpaperAssetCache.TryGet(Root, id, out WallpaperAssetCache.Entry cached) &&
            cached.MetadataLength == metadataInfo.Length &&
            cached.MetadataModifiedTicks == metadataInfo.LastWriteTimeUtc.Ticks &&
            (cached.Asset.ThumbnailPath is null || File.Exists(cached.Asset.ThumbnailPath)) &&
            TryGetFileStamp(cached.Asset.Path, out long cachedLength, out long cachedTicks) &&
            cachedLength == cached.ImageLength &&
            cachedTicks == cached.ImageModifiedTicks)
        {
            if(cached.Asset.ThumbnailPath is not null)EnsureNoReparsePoint(cached.Asset.ThumbnailPath);
            asset = cached.Asset;
            return true;
        }

        byte[] metadataBytes = ReadAllBytesShared(metadataPath, 64 * 1024);
        AssetMetadata? metadata = ReadMetadata(metadataBytes);
        if (metadata is null || !IsMetadataValid(metadata, id))
        {
            return false;
        }

        string imagePath = Path.Combine(directory, metadata.FileName);
        EnsureContainedPath(directory, imagePath);
        if (!File.Exists(imagePath))
        {
            return false;
        }

        EnsureNoReparsePoint(imagePath);
        FileInfo imageInfo = new(imagePath);
        if (imageInfo.Length <= 0 || imageInfo.Length > MaximumFileBytes)
        {
            return false;
        }

        // The recorded length detects a replaced or truncated original without hashing the image.
        if (metadata.ByteLength is long recorded && recorded != imageInfo.Length)
        {
            return false;
        }

        WallpaperAsset descriptor = BuildAsset(id, directory, metadata, metadataPath);
        WallpaperAssetCache.Set(Root, id, new WallpaperAssetCache.Entry(
            descriptor,
            metadataInfo.Length,
            metadataInfo.LastWriteTimeUtc.Ticks,
            imageInfo.Length,
            imageInfo.LastWriteTimeUtc.Ticks));
        asset = descriptor;
        return true;
    }

    private WallpaperAsset BuildAsset(string id, string directory, AssetMetadata metadata, string metadataPath)
    {
        string imagePath = Path.Combine(directory, metadata.FileName);
        long byteLength = 0;
        if (TryGetFileStamp(imagePath, out long measured, out _))
        {
            byteLength = measured;
        }

        string? thumbnailPath = null;
        if (metadata.ThumbnailFileName is not null && IsThumbnailFileName(metadata.ThumbnailFileName))
        {
            string candidate = Path.Combine(directory, metadata.ThumbnailFileName);
            try
            {
                EnsureContainedPath(directory, candidate);
                if (File.Exists(candidate))
                {
                    EnsureNoReparsePoint(candidate);
                    thumbnailPath = candidate;
                }
            }
            catch (IOException)
            {
            }
        }

        return new WallpaperAsset(id, metadata.Name, imagePath, metadata.Width, metadata.Height)
        {
            ByteLength = byteLength,
            ImportedAt = metadata.ImportedAt ?? GetFileTimestampUtc(metadataPath),
            IsFavorite = metadata.IsFavorite,
            ThumbnailPath = thumbnailPath
        };
    }

    private static async Task<DecodedImage> DecodeAndValidateAsync(
        byte[] bytes,
        string originalPath,
        CancellationToken cancellationToken)
    {
        if (bytes.LongLength == 0 || bytes.LongLength > MaximumFileBytes)
        {
            throw new InvalidDataException("壁纸图片大小必须在 1 字节到 24 MiB 之间。");
        }

        if (!TryGetFormat(originalPath, out ActualFormat? extensionFormat) || extensionFormat is null)
        {
            throw new InvalidDataException("仅支持 PNG、JPEG、WEBP 和 BMP 壁纸图片。");
        }

        if (!TrySniff(bytes, out ActualFormat? contentFormat) || contentFormat is null ||
            !string.Equals(extensionFormat.MediaType, contentFormat.MediaType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("壁纸图片扩展名与实际内容不一致。");
        }

        RejectAnimatedContainer(bytes, contentFormat);
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await writer.FlushAsync().AsTask().ConfigureAwait(false);
                writer.DetachStream();
            }

            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (decoder.FrameCount != 1)
            {
                throw new InvalidDataException("不支持动画或多帧壁纸图片。");
            }

            if (decoder.PixelWidth == 0 ||
                decoder.PixelHeight == 0 ||
                decoder.PixelWidth > MaximumDimension ||
                decoder.PixelHeight > MaximumDimension ||
                (long)decoder.PixelWidth * decoder.PixelHeight > MaximumPixels)
            {
                throw new InvalidDataException("壁纸图片尺寸超过限制：单边最多 16384 像素，总像素最多 64 Mi。");
            }

            string decodedMime = GetDecoderMimeTypes(decoder, contentFormat.MediaType);
            if (!string.Equals(NormalizeMimeType(decodedMime), contentFormat.MediaType, StringComparison.Ordinal))
            {
                throw new InvalidDataException("壁纸图片实际格式不受支持。");
            }

            BitmapFrame frame = await decoder.GetFrameAsync(0).AsTask().ConfigureAwait(false);
            var transform = new BitmapTransform
            {
                ScaledWidth = 1,
                ScaledHeight = 1
            };
            PixelDataProvider pixels = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage)
                .AsTask()
                .ConfigureAwait(false);
            byte[] onePixel = pixels.DetachPixelData();
            if (onePixel.Length < 4)
            {
                throw new InvalidDataException("壁纸图片无法解码。");
            }

            return new DecodedImage((int)decoder.PixelWidth, (int)decoder.PixelHeight, contentFormat);
        }
        catch (Exception ex) when (ex is COMException or NotSupportedException or ArgumentException or IOException)
        {
            throw new InvalidDataException("壁纸图片无法通过 Windows 图像组件解码。", ex);
        }
    }

    private static string GetDecoderMimeTypes(BitmapDecoder decoder, string fallback)
    {
        try
        {
            IReadOnlyList<string>? mimeTypes = decoder.DecoderInformation.MimeTypes;
            string? match = mimeTypes?.FirstOrDefault(mime =>
                string.Equals(NormalizeMimeType(mime), fallback, StringComparison.Ordinal));
            return match ?? mimeTypes?.FirstOrDefault() ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private WallpaperAsset UpdateMetadata(string id, Action<AssetMetadata> mutate)
    {
        string directory = GetAssetDirectory(id);
        if (!Directory.Exists(directory))
        {
            throw new FileNotFoundException("没有找到指定的壁纸图片。", directory);
        }

        EnsureNoReparsePoint(directory);
        string metadataPath = Path.Combine(directory, "asset.json");
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("没有找到指定的壁纸图片。", id);
        }

        EnsureNoReparsePoint(metadataPath);
        byte[] metadataBytes = ReadAllBytesShared(metadataPath, 64 * 1024);
        AssetMetadata metadata = ReadMetadata(metadataBytes) ??
            throw new InvalidDataException("壁纸图片的元数据已损坏。");
        if (!IsMetadataValid(metadata, id))
        {
            throw new InvalidDataException("壁纸图片的元数据已损坏。");
        }

        // The returned paths are always inside the managed asset directory.
        EnsureContainedPath(directory, Path.Combine(directory, metadata.FileName));

        string previousName = metadata.Name;
        mutate(metadata);
        if (metadata.OriginalName is null && previousName.Length > 0)
        {
            // A library created before the original name was recorded keeps it from the first edit on.
            metadata.OriginalName = previousName;
        }

        if (metadata.ByteLength is null &&
            TryGetFileStamp(Path.Combine(directory, metadata.FileName), out long measured, out _))
        {
            // Older libraries did not record the size, it is captured on the first metadata edit.
            metadata.ByteLength = measured;
        }

        WriteMetadataAtomically(metadataPath, SerializeMetadata(metadata));
        WallpaperAssetCache.Invalidate(Root, id);
        return BuildAsset(id, directory, metadata, metadataPath);
    }

    private static string NormalizeDisplayName(string? name)
    {
        if (name is null)
        {
            throw new ArgumentException("壁纸名称不能为空。", nameof(name));
        }

        // Control characters carry no meaning in a display name, they are dropped instead of stored.
        var builder = new StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        string displayName = builder.ToString().Trim();
        if (displayName.Length == 0)
        {
            throw new ArgumentException("壁纸名称不能为空。", nameof(name));
        }

        if (displayName.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException($"壁纸名称不能超过 {MaximumDisplayNameLength} 个字符。", nameof(name));
        }

        if (displayName.Contains('/') || displayName.Contains('\\'))
        {
            throw new ArgumentException("壁纸名称不能包含路径分隔符。", nameof(name));
        }

        if (displayName is "." or "..")
        {
            throw new ArgumentException("壁纸名称不能是目录名。", nameof(name));
        }

        return displayName;
    }

    private static AssetMetadata? ReadMetadata(byte[] bytes)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (node is not JsonObject root)
        {
            return null;
        }

        return new AssetMetadata(root)
        {
            Id = ReadString(root, "id") ?? string.Empty,
            Name = ReadString(root, "name") ?? string.Empty,
            FileName = ReadString(root, "fileName") ?? string.Empty,
            MediaType = ReadString(root, "mediaType") ?? string.Empty,
            Width = ReadInt32(root, "width") ?? 0,
            Height = ReadInt32(root, "height") ?? 0,
            OriginalName = ReadString(root, "originalName"),
            ByteLength = ReadInt64(root, "byteLength"),
            ImportedAt = ReadDateTimeOffset(root, "importedAt"),
            IsFavorite = ReadBoolean(root, "isFavorite") ?? false,
            ThumbnailFileName = ReadString(root, "thumbnailFileName")
        };
    }

    private static byte[] SerializeMetadata(AssetMetadata metadata)
    {
        // The parsed document is reused so that fields written by a newer version survive a rename.
        JsonObject root = metadata.Root;
        root["id"] = metadata.Id;
        root["name"] = metadata.Name;
        root["fileName"] = metadata.FileName;
        root["mediaType"] = metadata.MediaType;
        root["width"] = metadata.Width;
        root["height"] = metadata.Height;
        if (metadata.OriginalName is null)
        {
            root.Remove("originalName");
        }
        else
        {
            root["originalName"] = metadata.OriginalName;
        }

        if (metadata.ByteLength is null)
        {
            root.Remove("byteLength");
        }
        else
        {
            root["byteLength"] = metadata.ByteLength.Value;
        }

        if (metadata.ImportedAt is null)
        {
            root.Remove("importedAt");
        }
        else
        {
            root["importedAt"] = metadata.ImportedAt.Value.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
        }

        root["isFavorite"] = metadata.IsFavorite;
        if (metadata.ThumbnailFileName is null)
        {
            root.Remove("thumbnailFileName");
        }
        else
        {
            root["thumbnailFileName"] = metadata.ThumbnailFileName;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }

        return stream.ToArray();
    }

    private static void WriteMetadataAtomically(string path, byte[] content)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNewFileDurably(temporaryPath, content);
            if (File.Exists(path))
            {
                EnsureNoReparsePoint(path);
                File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
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

    private static bool IsMetadataValid(AssetMetadata metadata, string id)
    {
        if (!string.Equals(metadata.Id, id, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(metadata.Name) || metadata.Name.Length > 260)
        {
            return false;
        }

        if (metadata.Width is <= 0 or > MaximumDimension ||
            metadata.Height is <= 0 or > MaximumDimension ||
            (long)metadata.Width * metadata.Height > MaximumPixels)
        {
            return false;
        }

        if (!TryGetFormat(metadata.FileName, out ActualFormat? format) || format is null)
        {
            return false;
        }

        if (!string.Equals(metadata.FileName, Path.GetFileName(metadata.FileName), StringComparison.Ordinal))
        {
            return false;
        }

        if (metadata.OriginalName is not null && metadata.OriginalName.Length > 260)
        {
            return false;
        }

        if (metadata.ByteLength is < 0 or > MaximumFileBytes)
        {
            return false;
        }

        if (metadata.ThumbnailFileName is not null && !IsThumbnailFileName(metadata.ThumbnailFileName))
        {
            return false;
        }

        return true;
    }

    private static bool IsThumbnailFileName(string name) =>
        string.Equals(name, ThumbnailJpegName, StringComparison.Ordinal) ||
        string.Equals(name, ThumbnailPngName, StringComparison.Ordinal);

    private static string? ReadString(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
    }

    private static int? ReadInt32(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<int>(out int number) ? number : null;
    }

    private static long? ReadInt64(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<long>(out long number) ? number : null;
    }

    private static bool? ReadBoolean(JsonObject root, string name)
    {
        if (!root.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out bool flag) ? flag : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonObject root, string name)
    {
        string? text = ReadString(root, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out DateTimeOffset value)
            ? value
            : null;
    }

    private static bool TryGetFileStamp(string path, out long length, out long modifiedTicks)
    {
        length = 0;
        modifiedTicks = 0;
        try
        {
            EnsureNoReparsePoint(path);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            length = info.Length;
            modifiedTicks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return false;
        }
    }

    private static DateTimeOffset GetFileTimestampUtc(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static async Task<ThumbnailImage> CreateThumbnailAsync(
        byte[] imageBytes,
        string originalPath,
        CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync().AsTask().ConfigureAwait(false);
            await writer.FlushAsync().AsTask().ConfigureAwait(false);
            writer.DetachStream();
        }

        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        if (decoder.FrameCount < 1)
        {
            throw new InvalidDataException("壁纸图片无法解码。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        BitmapFrame frame = await decoder.GetFrameAsync(0).AsTask().ConfigureAwait(false);
        int rawWidth = checked((int)decoder.PixelWidth);
        int rawHeight = checked((int)decoder.PixelHeight);
        if (rawWidth <= 0 || rawHeight <= 0)
        {
            throw new InvalidDataException("壁纸图片尺寸无效。");
        }

        int orientation = ReadExifOrientation(imageBytes, originalPath);
        bool swaps = orientation is 5 or 6 or 7 or 8;
        int orientedWidth = swaps ? rawHeight : rawWidth;
        int orientedHeight = swaps ? rawWidth : rawHeight;
        (int targetWidth, int targetHeight) = FitThumbnail(orientedWidth, orientedHeight);
        int scaledWidth = swaps ? targetHeight : targetWidth;
        int scaledHeight = swaps ? targetWidth : targetHeight;

        // The scale is applied to the stored pixels, the orientation is applied afterwards so that
        // the EXIF flag can never be honoured twice or lost.
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)scaledWidth,
            ScaledHeight = (uint)scaledHeight
        };
        PixelDataProvider provider = await frame.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask()
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] scaled = provider.DetachPixelData();
        if (scaled.LongLength != (long)scaledWidth * scaledHeight * 4)
        {
            throw new InvalidDataException("壁纸缩略图的像素数据不完整。");
        }

        byte[] oriented = ApplyExifOrientation(scaled, scaledWidth, scaledHeight, orientation);
        bool opaque = IsOpaqueAlpha(oriented);
        byte[] encoded = await EncodeThumbnailAsync(oriented, targetWidth, targetHeight, opaque)
            .ConfigureAwait(false);
        return new ThumbnailImage(encoded, opaque, targetWidth, targetHeight);
    }

    private static (int Width, int Height) FitThumbnail(int width, int height)
    {
        long longest = Math.Max(width, height);
        if (longest <= 0)
        {
            throw new InvalidDataException("壁纸图片尺寸无效。");
        }

        double scale = Math.Min(1.0, MaximumThumbnailDimension / (double)longest);
        int scaledWidth = Math.Clamp((int)Math.Round(width * scale), 1, MaximumThumbnailDimension);
        int scaledHeight = Math.Clamp((int)Math.Round(height * scale), 1, MaximumThumbnailDimension);
        return (scaledWidth, scaledHeight);
    }

    private static bool IsOpaqueAlpha(ReadOnlySpan<byte> pixels)
    {
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 255)
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] ApplyExifOrientation(byte[] source, int width, int height, int orientation)
    {
        if (orientation == 1)
        {
            return source;
        }

        bool swaps = orientation is 5 or 6 or 7 or 8;
        int targetWidth = swaps ? height : width;
        int targetHeight = swaps ? width : height;
        var result = new byte[targetWidth * targetHeight * 4];
        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX;
                int sourceY;
                switch (orientation)
                {
                    case 2:
                        sourceX = width - 1 - x;
                        sourceY = y;
                        break;
                    case 3:
                        sourceX = width - 1 - x;
                        sourceY = height - 1 - y;
                        break;
                    case 4:
                        sourceX = x;
                        sourceY = height - 1 - y;
                        break;
                    case 5:
                        sourceX = y;
                        sourceY = x;
                        break;
                    case 6:
                        sourceX = y;
                        sourceY = height - 1 - x;
                        break;
                    case 7:
                        sourceX = width - 1 - y;
                        sourceY = height - 1 - x;
                        break;
                    case 8:
                        sourceX = width - 1 - y;
                        sourceY = x;
                        break;
                    default:
                        sourceX = x;
                        sourceY = y;
                        break;
                }

                int sourceIndex = ((sourceY * width) + sourceX) * 4;
                int targetIndex = ((y * targetWidth) + x) * 4;
                source.AsSpan(sourceIndex, 4).CopyTo(result.AsSpan(targetIndex, 4));
            }
        }

        return result;
    }

    private static async Task<byte[]> EncodeThumbnailAsync(byte[] pixels, int width, int height, bool opaque)
    {
        Guid encoderId = opaque ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId;
        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, output).AsTask().ConfigureAwait(false);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            opaque ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied,
            checked((uint)width),
            checked((uint)height),
            96,
            96,
            pixels);
        await encoder.FlushAsync().AsTask().ConfigureAwait(false);

        output.Seek(0);
        uint size = checked((uint)output.Size);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync(size).AsTask().ConfigureAwait(false);
        byte[] bytes = new byte[checked((int)size)];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static int ReadExifOrientation(ReadOnlySpan<byte> bytes, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => ReadJpegExifOrientation(bytes),
            ".png" => ReadPngExifOrientation(bytes),
            ".webp" => ReadWebpExifOrientation(bytes),
            _ => 1
        };
    }

    private static int ReadJpegExifOrientation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return 1;
        }

        int offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = bytes[offset + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                offset += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                break;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 2, 2));
            if (segmentLength < 2)
            {
                break;
            }

            int payload = offset + 4;
            int payloadLength = segmentLength - 2;
            if (marker == 0xE1 && payload + 6 <= bytes.Length &&
                bytes.Slice(payload, 6).SequenceEqual("Exif\0\0"u8))
            {
                return ReadTiffOrientation(bytes.Slice(payload + 6, payloadLength - 6));
            }

            offset = payload + payloadLength;
        }

        return 1;
    }

    private static int ReadPngExifOrientation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return 1;
        }

        int offset = 8;
        while (offset + 12 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));
            if (length < 0 || offset + 12L + length > bytes.Length)
            {
                break;
            }

            ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("eXIf"u8))
            {
                return ReadTiffOrientation(bytes.Slice(offset + 8, length));
            }

            if (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        return 1;
    }

    private static int ReadWebpExifOrientation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return 1;
        }

        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            ReadOnlySpan<byte> type = bytes.Slice(offset, 4);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            if (offset + 8L + length > bytes.Length)
            {
                break;
            }

            if (type.SequenceEqual("EXIF"u8))
            {
                return ReadTiffOrientation(bytes.Slice(offset + 8, checked((int)length)));
            }

            offset += 8 + checked((int)length) + checked((int)(length & 1));
        }

        return 1;
    }

    private static int ReadTiffOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return 1;
        }

        bool littleEndian;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I')
        {
            littleEndian = true;
        }
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M')
        {
            littleEndian = false;
        }
        else
        {
            return 1;
        }

        if (tiff[2] != 42)
        {
            return 1;
        }

        int directoryOffset = littleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(tiff.Slice(4, 4))
            : BinaryPrimitives.ReadInt32BigEndian(tiff.Slice(4, 4));
        if (directoryOffset < 8 || directoryOffset + 2 > tiff.Length)
        {
            return 1;
        }

        int count = littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(directoryOffset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(directoryOffset, 2));
        int entry = directoryOffset + 2;
        for (int index = 0; index < count && entry + 12 <= tiff.Length; index++, entry += 12)
        {
            int tag = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entry, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(entry, 2));
            if (tag != 0x0112)
            {
                continue;
            }

            int type = littleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entry + 2, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(entry + 2, 2));
            int value;
            if (type == 3)
            {
                value = littleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(entry + 8, 2))
                    : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(entry + 8, 2));
            }
            else if (type == 4)
            {
                value = littleEndian
                    ? BinaryPrimitives.ReadInt32LittleEndian(tiff.Slice(entry + 8, 4))
                    : BinaryPrimitives.ReadInt32BigEndian(tiff.Slice(entry + 8, 4));
            }
            else
            {
                return 1;
            }

            return value is >= 1 and <= 8 ? value : 1;
        }

        return 1;
    }

    private static byte[] ReadBoundedSource(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("没有找到要导入的壁纸图片。", path);
        }

        EnsureNoReparsePoint(path);
        FileInfo info = new(path);
        if (info.Length <= 0 || info.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("壁纸图片大小必须在 1 字节到 24 MiB 之间。");
        }

        try
        {
            return ReadAllBytesShared(path, MaximumFileBytes + 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("无法读取要导入的壁纸图片。", ex);
        }
    }

    private static byte[] ReadAllBytesShared(string path, long maximumBytes)
    {
        EnsureNoReparsePoint(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            if (stream.Length > maximumBytes)
            {
                throw new InvalidDataException("壁纸文件超过支持的大小上限。");
            }

            return [];
        }

        if (stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("壁纸文件过大。");
        }

        byte[] bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("读取壁纸文件时文件被修改。");
            }

            offset += read;
        }

        if (stream.ReadByte() >= 0)
        {
            throw new IOException("读取壁纸文件时文件内容发生变化。");
        }

        return bytes;
    }

    private static void WriteNewFileDurably(string path, ReadOnlySpan<byte> content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private void EnsureSafeRoot()
    {
        if (File.Exists(Root))
        {
            throw new IOException("壁纸状态路径不是目录。");
        }

        if (Directory.Exists(Root))
        {
            EnsureNoReparsePoint(Root);
        }
    }

    private void EnsureSafeLibrary(bool create)
    {
        EnsureSafeRoot();
        if (File.Exists(LibraryPath))
        {
            throw new IOException("壁纸图片库路径不是目录。");
        }

        if (!Directory.Exists(LibraryPath))
        {
            if (!create)
            {
                throw new DirectoryNotFoundException("壁纸图片库不存在。");
            }

            Directory.CreateDirectory(LibraryPath);
        }

        EnsureNoReparsePoint(LibraryPath);
    }

    private static void EnsureNoReparsePoint(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("壁纸路径不能经过符号链接或其他重解析点。");
        }
    }

    private string GetAssetDirectory(string id)
    {
        ValidateAssetId(id);

        string path = Path.Combine(LibraryPath, id);
        EnsureContainedPath(LibraryPath, path);
        return path;
    }

    private static void EnsureContainedPath(string parent, string child)
    {
        string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string fullChild = Path.GetFullPath(child);
        if (!fullChild.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullChild, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("壁纸路径超出了受管理目录。");
        }
    }

    private static void DeleteManagedEntry(string directory)
    {
        ValidateManagedEntry(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            File.Delete(entry);
        Directory.Delete(directory, recursive: false);
    }

    private static void ValidateManagedEntry(string directory)
    {
        EnsureNoReparsePoint(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                throw new IOException("受管理的壁纸条目包含不安全的子项。");
            }

        }
    }

    private void CleanupDeletedEntries()
    {
        foreach(string directory in Directory.EnumerateDirectories(LibraryPath,".deleted-*",SearchOption.TopDirectoryOnly).Take(128))
            if(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory),@"\A\.deleted-[a-f0-9]{64}-[a-f0-9]{32}\z"))
                try{EnsureContainedPath(LibraryPath,directory);DeleteManagedEntry(directory);}catch(IOException){}catch(UnauthorizedAccessException){}
    }

    private static FileStream AcquireLock(string path)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
        {
            EnsureNoReparsePoint(fullPath);
        }

        Stopwatch timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.Elapsed < LockTimeout)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static bool IsExpectedFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException or COMException or NotSupportedException;

    private static bool IsAssetId(string? id) =>
        id is { Length: 64 } && id.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateRange(
        double value,
        double minimum,
        double maximum,
        string displayName,
        string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName,
                $"{displayName}必须在 {minimum:0} 到 {maximum:0} 之间。");
        }
    }

    private static void ValidateAssetId(string id)
    {
        if (!IsAssetId(id))
        {
            throw new ArgumentException("壁纸图片标识无效。", nameof(id));
        }
    }

    private static bool TrySniff(ReadOnlySpan<byte> bytes, out ActualFormat? format)
    {
        if (bytes.Length >= 8 &&
            bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            format = Formats[".png"];
            return true;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            format = Formats[".jpg"];
            return true;
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            format = Formats[".webp"];
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
        {
            format = Formats[".bmp"];
            return true;
        }

        format = null;
        return false;
    }

    private static bool TryGetFormat(string path, out ActualFormat? format)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (string.Equals(extension, ".jpeg", StringComparison.Ordinal))
        {
            extension = ".jpg";
        }

        return Formats.TryGetValue(extension, out format);
    }

    private static void RejectAnimatedContainer(ReadOnlySpan<byte> bytes, ActualFormat format)
    {
        if (format.MediaType == "image/png")
        {
            int offset = 8;
            while (offset <= bytes.Length - 12)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
                if (length > int.MaxValue || offset + 12L + length > bytes.Length)
                {
                    return;
                }

                ReadOnlySpan<byte> type = bytes.Slice(offset + 4, 4);
                if (type.SequenceEqual("acTL"u8))
                {
                    throw new InvalidDataException("不支持动画壁纸图片。");
                }

                if (type.SequenceEqual("IEND"u8))
                {
                    return;
                }

                offset += checked((int)length + 12);
            }
        }
        else if (format.MediaType == "image/webp")
        {
            int offset = 12;
            while (offset <= bytes.Length - 8)
            {
                ReadOnlySpan<byte> type = bytes.Slice(offset, 4);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
                if (type.SequenceEqual("VP8X"u8) && offset + 9 < bytes.Length && (bytes[offset + 8] & 0x02) != 0)
                {
                    throw new InvalidDataException("不支持动画 WEBP 壁纸图片。");
                }

                if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
                {
                    throw new InvalidDataException("不支持动画 WEBP 壁纸图片。");
                }

                long next = offset + 8L + length + (length & 1);
                if (next > bytes.Length || next <= offset)
                {
                    return;
                }

                offset = (int)next;
            }
        }
    }

    private static string NormalizeMimeType(string? mimeType)
    {
        string normalized = mimeType?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? "";
        return normalized switch
        {
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/x-ms-bmp" => "image/bmp",
            _ => normalized
        };
    }

    private static readonly IReadOnlyDictionary<string, ActualFormat> Formats =
        new Dictionary<string, ActualFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = new(".png", "image/png"),
            [".jpg"] = new(".jpg", "image/jpeg"),
            [".webp"] = new(".webp", "image/webp"),
            [".bmp"] = new(".bmp", "image/bmp")
        };

    private sealed class AssetMetadata
    {
        public AssetMetadata(JsonObject root)
        {
            Root = root;
        }

        /// <summary>The parsed document, unknown fields are preserved across metadata writes.</summary>
        public JsonObject Root { get; }

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public string? OriginalName { get; set; }
        public long? ByteLength { get; set; }
        public DateTimeOffset? ImportedAt { get; set; }
        public bool IsFavorite { get; set; }
        public string? ThumbnailFileName { get; set; }
    }

    private sealed record ThumbnailImage(byte[] Bytes, bool Opaque, int Width, int Height);

    private sealed record ActualFormat(string Extension, string MediaType);
    private sealed record DecodedImage(int Width, int Height, ActualFormat Format);
    private sealed record LoadedAsset(byte[] Bytes, ActualFormat Format);
}
