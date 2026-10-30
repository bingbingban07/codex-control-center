param([int]$ProcessId)
$ErrorActionPreference='Stop'
# Re-register an existing Codex tray icon. Never restarts the app or Explorer.
Add-Type -TypeDefinition @'
using System;using System.Collections.Generic;using System.Runtime.InteropServices;using System.Text;
public static class CodexTrayRegistration {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [StructLayout(LayoutKind.Sequential)] public struct IconId{public uint size;public IntPtr hwnd;public uint uid;public Guid guid;}
 [StructLayout(LayoutKind.Sequential)] public struct Rect{public int left,top,right,bottom;}
 [DllImport("shell32.dll")]static extern int Shell_NotifyIconGetRect(ref IconId id,out Rect rect);
 [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr argument);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr hwnd,StringBuilder name,int count);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterWindowMessage(string name);
 [DllImport("user32.dll",SetLastError=true)]static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint message,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
 public static bool Present(){var id=new IconId{size=(uint)Marshal.SizeOf<IconId>(),guid=new Guid("e5768d8b-6936-4f45-b1ad-4c5fb414cb35")};return Shell_NotifyIconGetRect(ref id,out var rect)==0;}
 public static bool Restore(uint processId){bool notified=false;uint message=RegisterWindowMessage("TaskbarCreated");EnumWindows((hwnd,arg)=>{GetWindowThreadProcessId(hwnd,out uint owner);if(owner!=processId)return true;var name=new StringBuilder(256);GetClassName(hwnd,name,256);if(name.ToString()=="OwlElectron_NotifyIconHostWindow")notified|=SendMessageTimeout(hwnd,message,IntPtr.Zero,IntPtr.Zero,2,1500,out var result)!=IntPtr.Zero;return true;},IntPtr.Zero);return notified;}
}
'@
$trayWasPresent=[CodexTrayRegistration]::Present()
$trayNotified=$false
if(-not $trayWasPresent){
    $trayCandidates=if($ProcessId){@(Get-Process -Id $ProcessId)}else{@(Get-Process ChatGPT -ErrorAction SilentlyContinue)}
    foreach($trayCandidate in $trayCandidates){
        try{
            $trayPath=$trayCandidate.Path
            if($trayPath -notmatch '\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$'){continue}
            $trayNotified=[CodexTrayRegistration]::Restore([uint32]$trayCandidate.Id) -or $trayNotified
            if([CodexTrayRegistration]::Present()){break}
        }catch{continue}
    }
}
$trayPresent=[CodexTrayRegistration]::Present()
[pscustomobject]@{success=$trayPresent;alreadyPresent=$trayWasPresent;reRegistrationRequested=$trayNotified;appRestarted=$false;explorerRestarted=$false}|ConvertTo-Json
if(-not $trayPresent){exit 1}
