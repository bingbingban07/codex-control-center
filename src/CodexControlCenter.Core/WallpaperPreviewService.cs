using System.Diagnostics;
using System.Text.Json;

namespace CodexControlCenter.Core;

public sealed record WallpaperPreviewLease(string Id,string BaseRevision,WallpaperSettings Settings,DateTimeOffset ExpiresAt,int OwnerPid,long OwnerStartTicks);

/// <summary>Short renewable lease: a dead editor can never leave an unsaved wallpaper behind.</summary>
public sealed class WallpaperPreviewService(WallpaperLibrary library)
{
    public static readonly TimeSpan LeaseDuration=TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    public string Path=>System.IO.Path.Combine(library.Root,"preview.json");
    private string LockPath=>System.IO.Path.Combine(library.Root,".preview.lock");

    public WallpaperPreviewLease Start(WallpaperSettings settings,string expectedRevision)
    {
        WallpaperLibrary.Validate(settings);var snapshot=library.LoadSnapshot();
        if(snapshot.Revision!=expectedRevision)throw new InvalidOperationException("已应用设置发生变化，请先重新读取后再试用。");
        if(settings.Enabled&&settings.ImageId is not null)_=library.GetDataUri(settings.ImageId);
        using var held=Lock();WallpaperPreviewLease? current;
        try{current=ReadCore();}catch(Exception error)when(error is JsonException or InvalidDataException or ArgumentException){
            EnsureSafeFile(Path);if(File.Exists(Path))File.Move(Path,Path+".damaged-"+Guid.NewGuid().ToString("N")+".json");current=null;
        }
        if(IsActive(current,expectedRevision))throw new InvalidOperationException("另一个管理台窗口正在试用壁纸，请先在那个窗口结束试用，或等待它自动恢复。");
        using var process=Process.GetCurrentProcess();
        var lease=new WallpaperPreviewLease(Guid.NewGuid().ToString("N"),expectedRevision,settings,DateTimeOffset.UtcNow+LeaseDuration,process.Id,process.StartTime.ToUniversalTime().Ticks);
        Write(lease);return lease;
    }

    public WallpaperPreviewLease Renew(string id,WallpaperSettings settings)
    {
        WallpaperLibrary.Validate(settings);string revision=library.LoadSnapshot().Revision;
        using var held=Lock();var lease=ReadCore();
        if(lease is null||lease.Id!=id||!IsActive(lease,revision))throw new InvalidOperationException("临时试用已结束或已应用设置发生变化。草稿仍保留，可以重新试用。");
        if(settings.Enabled&&settings.ImageId is not null&&settings.ImageId!=lease.Settings.ImageId)_=library.GetDataUri(settings.ImageId);
        var next=lease with{Settings=settings,ExpiresAt=DateTimeOffset.UtcNow+LeaseDuration};Write(next);return next;
    }

    public bool Stop(string id)
    {
        using var held=Lock();var lease=ReadCore();if(lease is null||lease.Id!=id)return false;
        EnsureSafeFile(Path);RetryFileOperation(()=>File.Delete(Path));return true;
    }

    public WallpaperPreviewLease? ReadActive(string committedRevision)
    {
        try{using var held=Lock();var lease=ReadCore();return IsActive(lease,committedRevision)?lease:null;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException){return null;}
    }

    private static bool IsActive(WallpaperPreviewLease? lease,string revision)
    {
        if(lease is null||lease.BaseRevision!=revision||lease.ExpiresAt<=DateTimeOffset.UtcNow||lease.ExpiresAt>DateTimeOffset.UtcNow+LeaseDuration+TimeSpan.FromSeconds(2)||lease.Id is not{Length:32})return false;
        try{using var process=Process.GetProcessById(lease.OwnerPid);return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==lease.OwnerStartTicks;}
        catch{return false;}
    }
    private WallpaperPreviewLease? ReadCore()
    {
        if(!File.Exists(Path))return null;EnsureSafeFile(Path);
        if(new FileInfo(Path).Length>64*1024)throw new InvalidDataException("临时壁纸设置过大。");
        using var stream=new FileStream(Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var lease=JsonSerializer.Deserialize<WallpaperPreviewLease>(stream,JsonOptions);
        if(lease is null)return null;WallpaperLibrary.Validate(lease.Settings);return lease;
    }
    private void Write(WallpaperPreviewLease lease)
    {
        EnsureSafeFile(Path);string temporary=Path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(JsonSerializer.SerializeToUtf8Bytes(lease,JsonOptions));stream.Flush(true);}RetryFileOperation(()=>File.Move(temporary,Path,true));}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private FileStream Lock()
    {
        Directory.CreateDirectory(library.Root);EnsureSafeFile(LockPath);
        var elapsed=Stopwatch.StartNew();while(true){try{return new FileStream(LockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException)when(elapsed.Elapsed<TimeSpan.FromSeconds(3)){Thread.Sleep(15);}}
    }
    private static void RetryFileOperation(Action operation)
    {
        var elapsed=Stopwatch.StartNew();while(true){try{operation();return;}catch(Exception error)when(error is IOException or UnauthorizedAccessException&&elapsed.Elapsed<TimeSpan.FromMilliseconds(500)){Thread.Sleep(15);}}
    }
    private void EnsureSafeFile(string path)
    {
        var current=new DirectoryInfo(library.Root);while(current is not null){if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("壁纸状态目录不支持链接路径。");current=current.Parent;}
        if((File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new IOException("临时壁纸状态路径无效。");
    }
}
