using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodexControlCenter.Core;

public sealed record WallpaperRuntimeStatus(string State,string Message,int AppliedWindows=0,string? Revision=null,DateTimeOffset? UpdatedAt=null)
{
    public string Version {get;init;}="legacy";
    public int ProtocolVersion {get;init;}
    public int HostPid {get;init;}
    public string? PreviewLeaseId {get;init;}
    public IReadOnlyList<WallpaperWindowStatus> Windows {get;init;}=[];
}
public sealed record WallpaperWindowStatus(string Id,string State,string Message,long BytesSent=0,int RequestsSent=0);
public sealed record WallpaperRuntimeSession(int Port,int ProcessId,long StartTimeUtcTicks,string Executable);
public sealed record WallpaperLaunchPlan(string Executable,string? ApplicationId,string Arguments);

public static class CodexWallpaperRuntime
{
    public static string SessionPath(string root)=>Path.Combine(root,"connection.json");
    public static string StatusPath(string root)=>Path.Combine(root,"runtime.json");
    public static string MutexName(string root)=>"Local\\CodexControlCenterWallpaper-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant())))[..24];
    public static void WriteJson<T>(string path,T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temporary,JsonSerializer.SerializeToUtf8Bytes(value));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static WallpaperRuntimeStatus ReadStatus(string root)
    {
        try{
            var value=JsonSerializer.Deserialize<WallpaperRuntimeStatus>(File.ReadAllText(StatusPath(root)));
            if(value is not null&&value.UpdatedAt is not null&&DateTimeOffset.UtcNow-value.UpdatedAt<TimeSpan.FromSeconds(15))return value;
            if(value?.State=="restart_required"){
                var running=RunningCodex();bool stillRunning=running.Count>0;foreach(var process in running)process.Dispose();if(stillRunning)return value;
            }
        }catch(IOException){}catch(JsonException){}
        return new("disconnected","壁纸组件尚未连接。通过“启动带壁纸的 Codex”打开。",UpdatedAt:DateTimeOffset.UtcNow);
    }
    public static WallpaperRuntimeSession? ReadSession(string root)
    {
        try{return JsonSerializer.Deserialize<WallpaperRuntimeSession>(File.ReadAllText(SessionPath(root)));}
        catch(IOException){return null;}catch(JsonException){return null;}
    }
    public static bool IsCodexExecutable(string? path)
    {
        if(string.IsNullOrEmpty(path)||!Path.IsPathFullyQualified(path)||!File.Exists(path))return false;
        string filename=Path.GetFileName(path);
        if(!filename.Equals("ChatGPT.exe",StringComparison.OrdinalIgnoreCase)&&!filename.Equals("Codex.exe",StringComparison.OrdinalIgnoreCase))return false;
        string resource=Path.Combine(Path.GetDirectoryName(path)!,"resources","app.asar");
        return File.Exists(resource)&&(path.Contains("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)||path.Contains("\\Codex\\",StringComparison.OrdinalIgnoreCase));
    }
    public static IReadOnlyList<Process> RunningCodex()
    {
        var values=new List<Process>();using var current=Process.GetCurrentProcess();int currentSession=current.SessionId;
        foreach(string name in new[]{"ChatGPT","Codex"})foreach(var process in Process.GetProcessesByName(name)){
            try{if(IsCodexExecutable(process.MainModule?.FileName)&&process.SessionId==currentSession){values.Add(process);continue;}}catch{}
            process.Dispose();
        }
        return values;
    }
    public static void RememberExecutable(string root,string path)
    {
        if(!IsCodexExecutable(path))throw new InvalidOperationException("请选择 Codex 安装目录中的桌面程序。");
        WriteJson(Path.Combine(root,"codex-installation.json"),new Dictionary<string,string>{{"executable",Path.GetFullPath(path)}});
    }
    public static string? FindExecutable(string? root=null)
    {
        var running=RunningCodex();string? existing=null;foreach(var process in running)try{existing??=process.MainModule?.FileName;}finally{process.Dispose();}if(existing is not null)return existing;
        if(root is not null)try{
            using var cache=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"codex-installation.json")));string? path=cache.RootElement.GetProperty("executable").GetString();if(IsCodexExecutable(path))return path;
        }catch(Exception error)when(error is IOException or JsonException or KeyNotFoundException or InvalidOperationException){}
        try{
            string? command=Microsoft.Win32.Registry.GetValue(@"HKEY_CLASSES_ROOT\codex\shell\open\command",null,null) as string;
            if(command is not null){var match=System.Text.RegularExpressions.Regex.Match(command,"^\"([^\"]+\\.exe)\"",System.Text.RegularExpressions.RegexOptions.IgnoreCase);if(match.Success&&IsCodexExecutable(match.Groups[1].Value))return match.Groups[1].Value;}
        }catch(System.Security.SecurityException){}
        string packages=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps");
        try{
            foreach(string directory in Directory.EnumerateDirectories(packages,"OpenAI.Codex_*_x64__*",SearchOption.TopDirectoryOnly).OrderByDescending(Path.GetFileName)){
                string path=Path.Combine(directory,"app","ChatGPT.exe");if(IsCodexExecutable(path))return path;
            }
        }catch(UnauthorizedAccessException){}catch(IOException){}
        foreach(string path in new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Codex","Codex.exe"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Codex","ChatGPT.exe")})if(IsCodexExecutable(path))return path;
        return null;
    }
    public static bool IsSessionProcessAlive(WallpaperRuntimeSession session)
    {
        try{using var process=Process.GetProcessById(session.ProcessId);return !process.HasExited&&process.StartTime.ToUniversalTime().Ticks==session.StartTimeUtcTicks&&
            string.Equals(process.MainModule?.FileName,session.Executable,StringComparison.OrdinalIgnoreCase)&&IsCodexExecutable(session.Executable);}
        catch{return false;}
    }
    public static bool OwnsLoopbackEndpoint(WallpaperRuntimeSession session)
    {
        if(!IsSessionProcessAlive(session))return false;
        int size=0;GetExtendedTcpTable(IntPtr.Zero,ref size,true,2,3,0);if(size<=0||size>16*1024*1024)return false;
        IntPtr memory=Marshal.AllocHGlobal(size);
        try{
            if(GetExtendedTcpTable(memory,ref size,true,2,3,0)!=0)return false;
            int rows=Marshal.ReadInt32(memory);int rowSize=Marshal.SizeOf<TcpRow>();
            for(int i=0;i<rows;i++){
                var row=Marshal.PtrToStructure<TcpRow>(memory+4+i*rowSize);
                int port=(int)(((row.LocalPort&255)<<8)|((row.LocalPort>>8)&255));
                if(port==session.Port&&row.State==2&&row.Pid==(uint)session.ProcessId)
                    return row.LocalAddress==0x0100007F; // exactly 127.0.0.1, never wildcard or LAN
            }
            return false;
        }finally{Marshal.FreeHGlobal(memory);}
    }
    public static WallpaperLaunchPlan PlanLaunch(string executable,string? profile,int port)
    {
        if(!IsCodexExecutable(executable))throw new InvalidOperationException("未找到受支持的 Codex 桌面程序。");
        if(port is <1024 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        var args=new List<string>{"--remote-debugging-address=127.0.0.1","--remote-debugging-port="+port};
        string directory=Path.GetDirectoryName(executable)!;
        var package=Directory.GetParent(directory);
        var match=Regex.Match(package?.Name??"",@"\A(OpenAI\.Codex)_[^_]+_[^_]+_[^_]*_([A-Za-z0-9]+)\z");
        string? applicationId=null;
        if(match.Success){
            // Use Windows package activation, not the raw WindowsApps executable. Activation
            // supplies the package identity and runtime working directory required by Owl.
            var manifest=XDocument.Load(Path.Combine(package!.FullName,"AppxManifest.xml"));
            string? app=manifest.Descendants().FirstOrDefault(e=>e.Name.LocalName=="Application"&&
                ((string?)e.Attribute("Executable"))?.Replace('/','\\').EndsWith("app\\ChatGPT.exe",StringComparison.OrdinalIgnoreCase)==true)?.Attribute("Id")?.Value;
            if(string.IsNullOrWhiteSpace(app))throw new InvalidOperationException("Codex 安装包的启动入口无效，请从系统开始菜单打开 Codex。");
            applicationId=match.Groups[1].Value+"_"+match.Groups[2].Value+"!"+app;
        }
        if(profile is not null)args.Add("--user-data-dir="+Path.GetFullPath(profile));
        else args.Add("--user-data-dir="+Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Codex","web","Codex"));
        args.Add("codex:///");
        return new(executable,applicationId,string.Join(" ",args.Select(QuoteArgument)));
    }
    private static string QuoteArgument(string value)
    {
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){
            if(c=='\\'){slashes++;continue;}
            result.Append('\\',c=='"'?slashes*2+1:slashes);result.Append(c);slashes=0;
        }
        result.Append('\\',slashes*2);result.Append('"');return result.ToString();
    }
    public static WallpaperRuntimeSession Launch(string executable,string? profile=null,string? codexHome=null,int? port=null)
    {
        int selectedPort;
        if(port.HasValue){if(port is <1024 or >65535)throw new ArgumentOutOfRangeException(nameof(port));selectedPort=port.Value;}
        else{var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();selectedPort=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();}
        var plan=PlanLaunch(executable,profile,selectedPort);
        if(plan.ApplicationId is not null){
            if(profile is not null||codexHome is not null)throw new InvalidOperationException("正式安装包不使用隔离测试参数启动；请使用独立的测试程序。");
            var manager=(IApplicationActivationManager)new ApplicationActivationManager();
            try{
                int hr=manager.ActivateApplication(plan.ApplicationId,plan.Arguments,0,out uint pid);
                if(hr<0)Marshal.ThrowExceptionForHR(hr);
                using var activated=Process.GetProcessById(checked((int)pid));
                return new(selectedPort,activated.Id,activated.StartTime.ToUniversalTime().Ticks,executable);
            }finally{if(Marshal.IsComObject(manager))Marshal.ReleaseComObject(manager);}
        }
        // A separately installed, unpackaged client still starts as a normal visible app.
        var start=new ProcessStartInfo(executable,plan.Arguments){UseShellExecute=false,CreateNoWindow=false,WindowStyle=ProcessWindowStyle.Normal,WorkingDirectory=Path.GetDirectoryName(executable)!};
        if(profile is not null)start.Environment["CODEX_ELECTRON_USER_DATA_PATH"]=Path.GetFullPath(profile);
        if(codexHome is not null)start.Environment["CODEX_HOME"]=Path.GetFullPath(codexHome);
        using var process=Process.Start(start)??throw new InvalidOperationException("Codex 未能启动。");
        return new(selectedPort,process.Id,process.StartTime.ToUniversalTime().Ticks,executable);
    }
    public static void ActivateOfficial(string? root=null)
    {
        string executable=FindExecutable(root)??throw new InvalidOperationException("未找到 Codex，请先从开始菜单正常打开一次。");
        var plan=PlanLaunch(executable,null,19387);
        if(plan.ApplicationId is not null){
            var manager=(IApplicationActivationManager)new ApplicationActivationManager();
            try{int result=manager.ActivateApplication(plan.ApplicationId,"codex:///",0,out _);if(result<0)Marshal.ThrowExceptionForHR(result);}
            finally{if(Marshal.IsComObject(manager))Marshal.ReleaseComObject(manager);}
        }else{
            using var process=Process.Start(new ProcessStartInfo(executable,"codex:///"){UseShellExecute=false,CreateNoWindow=false,WindowStyle=ProcessWindowStyle.Normal,WorkingDirectory=Path.GetDirectoryName(executable)!})??throw new InvalidOperationException("Codex 未能启动。");
        }
    }
    [ComImport,Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]private class ApplicationActivationManager { }
    [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager {
        [PreserveSig]int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string appUserModelId,[MarshalAs(UnmanagedType.LPWStr)]string arguments,uint options,out uint processId);
        [PreserveSig]int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)]string appUserModelId,IntPtr itemArray,[MarshalAs(UnmanagedType.LPWStr)]string verb,out uint processId);
        [PreserveSig]int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)]string appUserModelId,IntPtr itemArray,out uint processId);
    }
    [StructLayout(LayoutKind.Sequential)]private struct TcpRow{public uint State,LocalAddress,LocalPort,RemoteAddress,RemotePort,Pid;}
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,uint family,uint tableClass,uint reserved);
}
