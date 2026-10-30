namespace CodexControlCenter.Core;

/// <summary>Exact configuration files only; watches replacements without following session/log trees.</summary>
public sealed class WorkspaceChangeMonitor : IDisposable
{
    private readonly HashSet<string> paths;
    private readonly Dictionary<string,FileSystemWatcher> watchers=new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync=new();
    private bool disposed;
    private DateTimeOffset? changedAt;
    public WorkspaceChangeMonitor(IEnumerable<string> files)
    {
        paths=files.Where(p=>!string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RegisterDirectories();
    }
    private void RegisterDirectories()
    {
        lock(sync){if(disposed)return;foreach(string directory in paths.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)){
            string root=directory;while(!Directory.Exists(root)&&Path.GetDirectoryName(root) is {} parent)root=parent;
            if(!Directory.Exists(root)||watchers.ContainsKey(root))continue;
            var watcher=new FileSystemWatcher(root){NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.FileName|NotifyFilters.DirectoryName,IncludeSubdirectories=false};
            watcher.Changed+=(_,e)=>Observe(e.FullPath);watcher.Created+=(_,e)=>Observe(e.FullPath);watcher.Deleted+=(_,e)=>Observe(e.FullPath);watcher.Renamed+=(_,e)=>{Observe(e.OldFullPath);Observe(e.FullPath);};watcher.Error+=(_,_)=>Signal();watchers[root]=watcher;watcher.EnableRaisingEvents=true;
        }}
    }
    private void Observe(string path){string full=Path.GetFullPath(path);if(paths.Contains(full)){Signal();return;}if(paths.Any(p=>p.StartsWith(full+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))){RegisterDirectories();Signal();}}
    private void Signal(){lock(sync)changedAt=DateTimeOffset.UtcNow;}
    public bool Consume(TimeSpan debounce)
    {
        lock(sync){if(changedAt is null||DateTimeOffset.UtcNow-changedAt<debounce)return false;changedAt=null;return true;}
    }
    public void Dispose(){lock(sync){disposed=true;foreach(var watcher in watchers.Values)watcher.Dispose();watchers.Clear();}}
}
