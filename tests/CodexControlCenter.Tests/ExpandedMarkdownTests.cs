using CodexControlCenter.Core;

internal static class ExpandedMarkdownTests
{
    public static async Task RunAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();
        string personal=Path.Combine(f.CodexHome,"skills","skill-a","notes.md"),role=Path.Combine(f.CodexHome,"agents","reviewer.md"),system=Path.Combine(f.CodexHome,"skills",".system","hidden","SKILL.md"),plugin=Path.Combine(f.CodexHome,"plugins","cache","example","skills","sample","SKILL.md");
        await File.WriteAllTextAsync(personal,"# personal\n");await File.WriteAllTextAsync(role,"# role\n");Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);await File.WriteAllTextAsync(plugin,"# plugin\n");
        var snapshot=await f.Service.LoadAsync();
        Require(snapshot.GlobalMarkdownFiles.Single(e=>e.Path==personal) is {Source:"个人技能",Editable:true},"personal Markdown not discovered");
        Require(snapshot.GlobalMarkdownFiles.Any(e=>e.Source=="共享技能"&&e.Editable),"shared skill Markdown missing");
        Require(snapshot.GlobalMarkdownFiles.Single(e=>e.Path==role) is {Source:"角色文件",Editable:true},"role Markdown missing");
        foreach(string path in new[]{system,plugin}){
            Require(!snapshot.GlobalMarkdownFiles.Single(e=>e.Path==path).Editable,"managed Markdown was editable");
            Require(f.Service.ReadGlobalMarkdown(snapshot,path).Length>0,"managed Markdown not readable");
            Require(f.Service.PlanGlobalMarkdown(snapshot,path,"changed").Errors.Count>0&&f.Service.PlanText(snapshot,path,"changed","attempt").Errors.Count>0,"managed Markdown write was allowed");
        }
        var edit=f.Service.PlanGlobalMarkdown(snapshot,personal,"# changed personal\n");var result=await f.Service.ApplyAsync(snapshot,edit);await f.Service.RestoreAsync(result.BackupDirectory);Require(await File.ReadAllTextAsync(personal)=="# personal\n","personal file restore failed");
        string project=Path.Combine(f.Root,"project"),one=Path.Combine(project,"AGENTS.md"),two=Path.Combine(project,"src","AGENTS.md"),skipped=Path.Combine(project,"node_modules","vendor","AGENTS.md");
        foreach(string path in new[]{one,two,skipped}){Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,"# project instructions\n");}
        snapshot=await f.Service.LoadAsync();var found=f.Service.RegisterMarkdownFolder(snapshot,project);
        Require(found.Count==2&&found.Select(e=>e.Name).Distinct().Count()==2&&found.All(e=>e.Source=="项目指令"),"nested project instructions missing or generated directory scanned");
        Require(!snapshot.GlobalMarkdownFiles.Any(e=>e.Path==skipped),"generated project document included");
        var plan=f.Service.PlanGlobalMarkdown(snapshot,two,"# nested edit\n");Require(plan.Errors.Count==0,"registered project file not editable");var saved=await f.Service.ApplyAsync(snapshot,plan);await f.Service.RestoreAsync(saved.BackupDirectory);
        snapshot=await f.Service.LoadAsync();Require(snapshot.GlobalMarkdownFiles.Count(e=>e.Source=="项目指令")==2,"registered project discovery lost on refresh");
        f.Service.ReadGlobalMarkdown(snapshot,one);var stale=f.Service.PlanGlobalMarkdown(snapshot,one,"draft");await File.WriteAllTextAsync(one,"external");f.Service.RegisterMarkdownFolder(snapshot,project);
        bool rejected=false;try{await f.Service.ApplyAsync(snapshot,stale);}catch(InvalidOperationException){rejected=true;}Require(rejected&&await File.ReadAllTextAsync(one)=="external","project registration rebased a dirty file or overwrote external text");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
