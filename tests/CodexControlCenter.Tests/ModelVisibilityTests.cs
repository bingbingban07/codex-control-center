using System.Text.Json.Nodes;
using CodexControlCenter.Core;

internal static class ModelVisibilityTests
{
    public static async Task RunAsync()
    {
        await using var fixture=await FakeWorkspace.CreateAsync();
        var catalog=JsonNode.Parse(await File.ReadAllTextAsync(fixture.CatalogPath))!;
        catalog["models"]![0]!["visibility"]="list";
        catalog["models"]![1]!["visibility"]="hide";
        await File.WriteAllTextAsync(fixture.CatalogPath,catalog.ToJsonString());
        byte[] original=await File.ReadAllBytesAsync(fixture.CatalogPath),router=await File.ReadAllBytesAsync(fixture.RouterPath),config=await File.ReadAllBytesAsync(fixture.ConfigPath);
        var snapshot=await fixture.Service.LoadAsync();
        Require(snapshot.Models.Single(m=>m.Id=="model-a").Visibility=="list","visible field not read");
        Require(snapshot.Models.Single(m=>m.Id=="model-b").Visibility=="hide","hidden model was dropped");
        Require(snapshot.Models.Single(m=>m.Id=="model-unmapped").Visibility=="","missing visibility was invented");
        var changes=new Dictionary<string,string>{{"model-a","hide"},{"model-b","list"},{"model-unmapped","hide"}};
        var plan=fixture.Service.PlanModelVisibility(snapshot,changes);
        Require(plan.Errors.Count==0&&plan.Files.Count==1&&plan.Files[0].Path==fixture.CatalogPath,"visibility touched routing/defaults or required mapping");
        var result=await fixture.Service.ApplyAsync(snapshot,plan);
        var reloaded=await fixture.Service.LoadAsync();
        Require(reloaded.MainModel=="model-a"&&reloaded.AgentModel=="model-b","hiding changed defaults");
        Require(reloaded.Models.Single(m=>m.Id=="model-a").Visibility=="hide"&&reloaded.Models.Single(m=>m.Id=="model-b").Visibility=="list","visibility roundtrip");
        byte[] currentRouter=await File.ReadAllBytesAsync(fixture.RouterPath),currentConfig=await File.ReadAllBytesAsync(fixture.ConfigPath);
        Require(router.SequenceEqual(currentRouter)&&config.SequenceEqual(currentConfig),"unrelated files changed");
        var saved=JsonNode.Parse(await File.ReadAllTextAsync(fixture.CatalogPath))!;
        Require(saved["catalog_unknown"]!["keep"]!.GetValue<bool>()&&saved["models"]![0]!["model_unknown"]!["keep"]!.GetValue<bool>(),"unknown catalog data lost");
        Require(saved["models"]!.AsArray().Count==3,"hiding deleted a model");
        await fixture.Service.RestoreAsync(result.BackupDirectory);
        Require((await File.ReadAllBytesAsync(fixture.CatalogPath)).SequenceEqual(original),"restore did not preserve original bytes");

        snapshot=await fixture.Service.LoadAsync();
        var edit=snapshot.Models.Single(m=>m.Id=="model-a");edit.DisplayName="Changed display name";
        var mapping=fixture.Service.PlanModel(snapshot,edit);
        Require(mapping.Errors.Count==0,"mapping plan failed");
        var combined=fixture.Service.PlanModelVisibility(snapshot,new Dictionary<string,string>{{"model-a","hide"}},mapping.Files.Single(f=>f.Path==fixture.CatalogPath).Content);
        mapping.Files.RemoveAll(f=>f.Path==fixture.CatalogPath);mapping.Files.AddRange(combined.Files);
        await fixture.Service.ApplyAsync(snapshot,mapping);reloaded=await fixture.Service.LoadAsync();
        Require(reloaded.Models.Single(m=>m.Id=="model-a") is {DisplayName:"Changed display name",Visibility:"hide"},"composed edit lost display name or visibility");
        Require(fixture.Service.PlanModelVisibility(reloaded,new Dictionary<string,string>{{"missing-model","hide"}}).Errors.Count>0,"unknown id accepted");
        Require(fixture.Service.PlanModelVisibility(reloaded,new Dictionary<string,string>{{"model-a","invalid"}}).Errors.Count>0,"invalid visibility accepted");
        var stale=fixture.Service.PlanModelVisibility(reloaded,new Dictionary<string,string>{{"model-a","list"}});
        await File.AppendAllTextAsync(fixture.CatalogPath,"\n ");byte[] external=await File.ReadAllBytesAsync(fixture.CatalogPath);
        bool rejected=false;try{await fixture.Service.ApplyAsync(reloaded,stale);}catch(InvalidOperationException){rejected=true;}
        Require(rejected&&(await File.ReadAllBytesAsync(fixture.CatalogPath)).SequenceEqual(external),"stale visibility overwrote external file");

        reloaded=await fixture.Service.LoadAsync();
        var newModel=new ModelEntry{Id="new-model",DisplayName="New Model",Provider="a",UpstreamModel="up-new",ContextWindow=100000,Efforts="high",Mapped=true};
        var create=fixture.Service.PlanModel(reloaded,newModel);await fixture.Service.ApplyAsync(reloaded,create);
        Require((await fixture.Service.LoadAsync()).Models.Single(m=>m.Id=="new-model").Visibility=="list","new model inherited hidden template");

        var duplicate=await fixture.Service.LoadAsync();duplicate.Models.Add(new ModelEntry{Id="model-a"});
        Require(fixture.Service.PlanModelVisibility(duplicate,new Dictionary<string,string>{{"model-a","list"}}).Errors.Count>0,"ambiguous model id accepted");
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
