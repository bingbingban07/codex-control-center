using CodexControlCenter.Core;
using System.Text.Json;
using System.Diagnostics;

internal static class WallpaperPreviewLeaseTests
{
    public static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"ccc-wallpaper-lease-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var library=new WallpaperLibrary(root);var preview=new WallpaperPreviewService(library);
        void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        try{
            var initial=library.LoadSnapshot();var draft=initial.Settings with{Enabled=false,Zoom=1.75,PositionX=13};var lease=preview.Start(draft,initial.Revision);
            Require(preview.ReadActive(initial.Revision)?.Settings.Zoom==1.75,"Draft lease unavailable");Require(library.LoadSnapshot()==initial,"Preview changed committed settings");
            bool conflict=false;try{preview.Start(draft,initial.Revision);}catch(InvalidOperationException){conflict=true;}Require(conflict,"Another editor stole the active preview");
            Require(!preview.Stop(new string('f',32)),"A different editor stopped the lease");
            var renewed=preview.Renew(lease.Id,draft with{PositionX=76});Require(preview.ReadActive(initial.Revision)?.Settings.PositionX==76,"Renew did not update draft parameters");
            library.SaveSnapshot(initial.Settings with{Overlay=.61},initial.Revision);Require(preview.ReadActive(library.SettingsRevision()) is null,"External commit did not revoke stale preview");
            Require(preview.Stop(lease.Id),"Owner could not release preview");Require(preview.ReadActive(library.SettingsRevision()) is null,"Stopped lease still active");
            var next=preview.Start(draft,library.SettingsRevision());
            var expired=next with{ExpiresAt=DateTimeOffset.UtcNow-TimeSpan.FromSeconds(1)};File.WriteAllText(preview.Path,JsonSerializer.Serialize(expired,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
            Require(preview.ReadActive(library.SettingsRevision()) is null,"Expired preview applied");
            var dead=next with{OwnerPid=int.MaxValue,ExpiresAt=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(5)};File.WriteAllText(preview.Path,JsonSerializer.Serialize(dead));
            Require(preview.ReadActive(library.SettingsRevision()) is null,"Dead editor lease applied");
            File.WriteAllText(preview.Path,"{ damaged");Require(preview.ReadActive(library.SettingsRevision()) is null,"Damaged transient state blocked saved wallpaper");
            var recovered=preview.Start(draft,library.SettingsRevision());Require(preview.Stop(recovered.Id),"New preview did not recover after malformed transient state");

            var cover=WallpaperComposition.Calculate(2000,1000,1000,1000,new(){Zoom=1,PositionX=100,PositionY=0});
            Require(cover.Width==2000&&cover.Height==1000&&cover.Left==-1000&&cover.Top==0,"Cover focus geometry wrong");
            var contain=WallpaperComposition.Calculate(2000,1000,1000,1000,new(){Fit="contain",Zoom=1,PositionY=100});
            Require(contain.Width==1000&&contain.Height==500&&contain.Top==500,"Contain position geometry wrong");
            var zoom=WallpaperComposition.Calculate(2000,1000,1000,1000,new(){Zoom=2,PositionX=50,PositionY=50});
            Require(zoom.Width==4000&&zoom.Height==2000&&zoom.Left==-1500&&zoom.Top==-500,"Zoom geometry wrong");
            Require(WallpaperRenderer.BuildScript(new(){Zoom=2},null).Contains("\"zoom\":2"),"Renderer lacks composition zoom");
            var blurred=new WallpaperSettings{Blur=20,PositionX=23,Zoom=1.4};var small=WallpaperComposition.Calculate(1600,900,1200,675,blurred);var large=WallpaperComposition.Calculate(1600,900,1920,1080,blurred);
            Require(Math.Abs(small.Left/1200-large.Left/1920)<.000001&&Math.Abs(small.Width/1200-large.Width/1920)<.000001,"Blur changed relative crop at another viewport size");
            var concurrent=preview.Start(draft,library.SettingsRevision());var reading=Task.Run(()=>{for(int i=0;i<500;i++)_ = preview.ReadActive(library.SettingsRevision());});
            for(int i=0;i<50;i++)preview.Renew(concurrent.Id,draft with{PositionX=i});await reading;Require(preview.Stop(concurrent.Id),"Concurrent reader blocked preview stop");
            await Task.CompletedTask;
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
