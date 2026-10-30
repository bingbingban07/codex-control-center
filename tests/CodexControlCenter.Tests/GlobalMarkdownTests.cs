using CodexControlCenter.Core;
using System.Text;

internal static class GlobalMarkdownTests
{
    public static async Task RunAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();
        string agents=Path.Combine(f.CodexHome,"AGENTS.md"),overridePath=Path.Combine(f.CodexHome,"AGENTS.override.md"),notes=Path.Combine(f.CodexHome,"说明.md");
        await File.WriteAllTextAsync(agents,"# 全局指令\r\n\r\n- 原有内容。\r\n",new UTF8Encoding(true));byte[] original=await File.ReadAllBytesAsync(agents);
        await File.WriteAllTextAsync(notes,"# 补充说明\n");
        string instruction=Path.Combine(f.CodexHome,"instructions","nested","说明.md");Directory.CreateDirectory(Path.GetDirectoryName(instruction)!);await File.WriteAllTextAsync(instruction,"# 嵌套说明\n");
        string excluded=Path.Combine(f.CodexHome,"secrets","private.md");await File.WriteAllTextAsync(excluded,"not a global instruction");
        var s=await f.Service.LoadAsync();
        Require(s.GlobalMarkdownFiles.Count>=4&&s.GlobalMarkdownFiles.Any(e=>e.Path==overridePath)&&!s.GlobalMarkdownFiles.Any(e=>e.Path==excluded),"global discovery scope or missing common files");
        Require(f.Service.ReadGlobalMarkdown(s,overridePath)==""&&!File.Exists(overridePath),"opening missing file created it");
        Require(f.Service.ReadGlobalMarkdown(s,agents).Contains("原有内容"),"global document not readable");
        var combined=f.Service.PlanGlobalMarkdown(s,agents,"# 修改后的全局指令\n\n- 保留受管理段落由用户自行编辑。\n");
        var secondary=f.Service.PlanGlobalMarkdown(s,overridePath,"# 临时覆盖指令\n");combined.Files.AddRange(secondary.Files);
        Require(combined.Errors.Count==0&&combined.Files.Count==2,"global change plan failed");
        var result=await f.Service.ApplyAsync(s,combined);Require(File.Exists(overridePath),"global override not created");
        await f.Service.RestoreAsync(result.BackupDirectory);Require(!File.Exists(overridePath)&&(await File.ReadAllBytesAsync(agents)).SequenceEqual(original),"global restore did not restore bytes/missing file");
        s=await f.Service.LoadAsync();string target=f.Service.NewGlobalMarkdownPath("新说明.md","prompts");
        Require(f.Service.ReadGlobalMarkdown(s,target)=="","new prompt not blank");
        var create=f.Service.PlanGlobalMarkdown(s,target,"# 新提示词\n");Require(create.Errors.Count==0,"new global prompt rejected");
        var created=await f.Service.ApplyAsync(s,create);Require((await f.Service.LoadAsync()).GlobalMarkdownFiles.Any(e=>e.Path==target),"created Markdown not discovered");
        await f.Service.RestoreAsync(created.BackupDirectory);Require(!File.Exists(target),"new Markdown restore did not remove file");

        s=await f.Service.LoadAsync();f.Service.ReadGlobalMarkdown(s,notes);var stale=f.Service.PlanGlobalMarkdown(s,notes,"# draft\n");await File.WriteAllTextAsync(notes,"# external\n");
        bool rejected=false;try{await f.Service.ApplyAsync(s,stale);}catch(InvalidOperationException){rejected=true;}
        Require(rejected&&(await File.ReadAllTextAsync(notes))=="# external\n","external Markdown overwritten");
        Require(f.Service.PlanGlobalMarkdown(s,excluded,"changed").Errors.Count>0&&f.Service.PlanGlobalMarkdown(s,Path.Combine(f.Root,"outside.md"),"changed").Errors.Count>0,"Markdown scope escaped");
        foreach(string invalid in new[]{"../outside.md","sub/file.md","bad.txt","CON.md","LPT1.md",".md"," name.md","bad?.md"}){
            bool failed=false;try{f.Service.NewGlobalMarkdownPath(invalid);}catch(ArgumentException){failed=true;}Require(failed,"invalid Markdown filename accepted: "+invalid);
        }
        Require(f.Service.PlanGlobalMarkdown(s,agents,"contains\0nul").Errors.Count>0,"NUL content accepted");
        Require(f.Service.PlanGlobalMarkdown(s,agents,new string('a',2*1024*1024+1)).Errors.Count>0,"oversize Markdown accepted");
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
