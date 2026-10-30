using System.Text.Json.Nodes;
using CodexControlCenter.Core;

internal static class ModelEditingTests
{
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    public static async Task RunAsync()
    {
        await using var f=await FakeWorkspace.CreateAsync();
        var catalog=JsonNode.Parse(await File.ReadAllTextAsync(f.CatalogPath))!;
        foreach(var node in catalog["models"]!.AsArray()){
            long context=node!["context_window"]?.GetValue<long>()??100000;node["context_window"]=context;node["max_context_window"]=context;node["auto_compact_token_limit"]=context*90/100;node["effective_context_window_percent"]=95;
        }
        await File.WriteAllTextAsync(f.CatalogPath,catalog.ToJsonString());
        var snapshot=await f.Service.LoadAsync();
        Check(ModelDisplayLabel.Split("GPT [内部] 名称 [账号订阅]")==("GPT [内部] 名称","账号订阅"),"Only the final label should split");
        Check(ModelDisplayLabel.WithLabel("GPT [账号订阅]","我的订阅")=="GPT [我的订阅]","Label replacement failed");
        Check(ModelDisplayLabel.WithLabel("GPT [账号订阅]","")=="GPT","Empty label did not remove brackets");
        Check(ModelDisplayLabel.WithLabel("GPT","[备用]")=="GPT [备用]","Pasted label brackets were duplicated");
        Check(ModelDisplayLabel.WithLabel("GPT 【原渠道】","新渠道")=="GPT [新渠道]","Full-width label not supported");
        var namedModel=snapshot.Models.First(m=>m.Mapped);
        string routerBefore=await File.ReadAllTextAsync(snapshot.RouterPath),catalogBefore=await File.ReadAllTextAsync(snapshot.CatalogPath);
        var naming=f.Service.PlanModelDisplayNames(snapshot,new Dictionary<string,string>{{namedModel.Id,namedModel.DisplayName+" [我的订阅]"}});
        Check(naming.Errors.Count==0&&naming.Files.Count==1&&naming.Files[0].Path==snapshot.CatalogPath,"Name-only plan rewrites routing");
        var namedJson=JsonNode.Parse(naming.Files[0].Content)!;
        var originalJson=JsonNode.Parse(catalogBefore)!;
        var changed=namedJson["models"]!.AsArray().Single(n=>n!["slug"]!.ToString()==namedModel.Id)!;
        changed["display_name"]=originalJson["models"]!.AsArray().Single(n=>n!["slug"]!.ToString()==namedModel.Id)!["display_name"]!.DeepClone();
        Check(JsonNode.DeepEquals(namedJson,originalJson),"Renaming changed model capabilities or routing metadata");
        var nameSaved=await f.Service.ApplyAsync(snapshot,naming);
        Check(await File.ReadAllTextAsync(snapshot.RouterPath)==routerBefore,"Name transaction touched router bytes");
        await f.Service.RestoreAsync(nameSaved.BackupDirectory);
        Check(await File.ReadAllTextAsync(snapshot.CatalogPath)==catalogBefore,"Name backup restore changed original bytes");
        Check(f.Service.PlanModelDisplayNames(snapshot,new Dictionary<string,string>{{namedModel.Id,"bad\nlabel"}}).Files.Count==0,"Multiline display accepted");
        Check(f.Service.PlanModelDisplayNames(snapshot,new Dictionary<string,string>{{"missing","Name"}}).Files.Count==0,"Missing model accepted");
        var model=snapshot.Models.First(m=>m.Mapped);
        long oldContext=model.ContextWindow;model.ContextWindow=1_000_000;
        var scaled=f.Service.PlanModel(snapshot,model);
        Check(scaled.Errors.Count==0,"Resize plan invalid");
        var after=JsonNode.Parse(scaled.Files.Single(p=>p.Path==snapshot.CatalogPath).Content)!["models"]!.AsArray().Single(n=>n!["slug"]!.ToString()==model.Id)!;
        Check(after["max_context_window"]!.GetValue<long>()==1_000_000,"Maximum context remains stale");
        Check(after["auto_compact_token_limit"]!.GetValue<long>()==900_000,"Compaction ratio was not preserved");
        model.UpdateCompactionSettings=true;model.AutoCompactTokenLimit=960_000;
        Check(f.Service.PlanModel(snapshot,model).Errors.Count>0,"Compaction exceeds effective budget");
        model.AutoCompactTokenLimit=900_000;model.EffectiveContextWindowPercent=101;
        Check(f.Service.PlanModel(snapshot,model).Errors.Count>0,"Invalid percentage accepted");
        model.EffectiveContextWindowPercent=95;
        var other=snapshot.Models.First(m=>m.Mapped&&m.Id!=model.Id);other.DisplayName="Second staged model";
        var batch=f.Service.PlanModels(snapshot,[model,other]);
        Check(batch.Errors.Count==0&&batch.Files.Count==2,"Batch is not a two-file transaction");
        after=JsonNode.Parse(batch.Files.Single(p=>p.Path==snapshot.CatalogPath).Content)!;
        Check(after["models"]!.AsArray().Single(n=>n!["slug"]!.ToString()==other.Id)!["display_name"]!.ToString()=="Second staged model","Second model overwrote first or disappeared");
        Check(after["models"]!.AsArray().Single(n=>n!["slug"]!.ToString()==model.Id)!["context_window"]!.GetValue<long>()==1_000_000,"First model disappeared");
        var saved=await f.Service.ApplyAsync(snapshot,batch);
        var loaded=await f.Service.LoadAsync();
        Check(loaded.Models.Single(m=>m.Id==model.Id).AutoCompactTokenLimit==900_000,"Compaction did not roundtrip");
        await f.Service.RestoreAsync(saved.BackupDirectory);
        Check((await f.Service.LoadAsync()).Models.Single(m=>m.Id==model.Id).ContextWindow==oldContext,"Batch restore failed");
        other.ContextWindow=0;
        Check(f.Service.PlanModels(snapshot,[model,other]).Files.Count==0,"Invalid second draft partially planned first");
        Check(f.Service.PlanModels(snapshot,[model,model]).Errors.Count>0,"Duplicate batch alias accepted");
        var created=f.Service.PlanModel(snapshot,new(){Id="fresh-model",Provider="b",DisplayName="Fresh",UpstreamModel="up-fresh",ContextWindow=250000,Efforts="high"});
        Check(created.Errors.Count==0,"New model template invalid");
        var fresh=JsonNode.Parse(created.Files.Single(f=>f.Path==snapshot.CatalogPath).Content)!["models"]!.AsArray().Single(n=>n!["slug"]!.ToString()=="fresh-model")!;
        Check(fresh["provider"]!.ToString()=="b"&&fresh["upstream_model"]!.ToString()=="up-fresh","Catalog mapping conflicts with router");
        Check(fresh["model_unknown"] is null&&fresh["description"]!.ToString()!="Model A description","New model inherited another model identity");
        // Export only this synthetic model for a separate installed-Codex schema acceptance check.
        string schemaFixture=Path.GetFullPath("verification/v0.5.0-schema");Directory.CreateDirectory(schemaFixture);
        await File.WriteAllTextAsync(Path.Combine(schemaFixture,"new-model-catalog.json"),new JsonObject{["models"]=new JsonArray(fresh.DeepClone())}.ToJsonString());
        var first=QuickNavigation.Search(snapshot,"模型列表").First();Check(first.Page=="models"&&first.ItemId is null,"Page exact match not ranked first");
        Check(QuickNavigation.Search(snapshot,model.Id).Any(t=>t.Page=="models"&&t.ItemId==model.Id),"Model lookup missing");
        Check(QuickNavigation.Search(snapshot,"no-matching-query").Count==0,"Empty query state broken");
        Check(QuickNavigation.Search(snapshot,"模型",2).Count==2,"Search result bound broken");
    }
}
