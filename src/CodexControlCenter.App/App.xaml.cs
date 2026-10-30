using Microsoft.UI.Xaml;
namespace CodexControlCenter.App;
public partial class App : Application
{
    private Window? window;
    public App() {
        InitializeComponent();UiCrashLog.Write("application.start");
        UnhandledException+=(_,e)=>UiCrashLog.Exception("xaml.unhandled",e.Exception);
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{if(e.ExceptionObject is Exception error)UiCrashLog.Exception("runtime.unhandled",error);};
        TaskScheduler.UnobservedTaskException+=(_,e)=>UiCrashLog.Exception("task.unobserved",e.Exception);
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args) { window = new MainWindow(); window.Activate(); }
}
