namespace CodexControlCenter.Core;

/// <summary>A failed or disabled optional wallpaper helper never swallows an Open action.</summary>
public static class CodexOpenCoordinator
{
    public static async Task<string> OpenAsync(bool enhanced,bool wallpaper,Func<bool> visible,Action startWallpaper,Action official,
        Func<CancellationToken,Task>? wait=null,int attempts=16,CancellationToken ct=default)
    {
        if(!enhanced||!wallpaper||visible()){official();return "official";}
        try{
            startWallpaper();
            for(int i=0;i<attempts;i++){
                ct.ThrowIfCancellationRequested();
                if(visible())return "wallpaper";
                await (wait?.Invoke(ct)??Task.Delay(250,ct));
            }
        }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception){/* Official activation is the independent fallback. */}
        ct.ThrowIfCancellationRequested();official();return "official-fallback";
    }
}
