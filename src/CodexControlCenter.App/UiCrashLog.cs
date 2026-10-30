using CodexControlCenter.Core;
using System.Text.Json;

namespace CodexControlCenter.App;
internal static class UiCrashLog
{
    private static readonly object Sync=new();
    private static string StateRoot{
        get{var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--state-home");return index>=0&&index+1<args.Length?Path.GetFullPath(args[index+1]):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexControlCenter");}
    }
    public static void Write(string operation,string? detail=null)
    {
        try{lock(Sync){
            string folder=Path.Combine(StateRoot,"diagnostics");Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,$"ui-{Environment.ProcessId}.ndjson");
            if(File.Exists(file)&&new FileInfo(file).Length>512*1024)File.Move(file,file+".previous",true);
            string safe=UserFeedback.Redact(detail??"");if(safe.Length>400)safe=safe[..400];
            File.AppendAllText(file,JsonSerializer.Serialize(new{at=DateTimeOffset.Now,pid=Environment.ProcessId,version=typeof(App).Assembly.GetName().Version?.ToString(),operation,detail=safe})+Environment.NewLine);
        }}catch{ /* Diagnostic I/O must never replace the original failure. */ }
    }
    public static void Exception(string operation,Exception error)
    {
        Write(operation,error.GetType().FullName+": "+error.Message);
        try{File.WriteAllText(Path.Combine(StateRoot,"last-error.txt"),DateTimeOffset.Now+Environment.NewLine+UserFeedback.Redact(error.ToString()));}catch{}
    }
}
