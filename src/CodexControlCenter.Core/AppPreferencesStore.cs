using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexControlCenter.Core;
public sealed class AppPreferencesStore(string path)
{
    public JsonObject Load()
    {
        if(!File.Exists(path))return new();
        try{return JsonNode.Parse(File.ReadAllText(path)) as JsonObject??throw new FormatException("管理台设置必须为 JSON 对象。");}
        catch(JsonException e){throw new FormatException("管理台设置文件格式有误，原文件已保留。",e);}
    }
    public void Save(JsonObject data)
    {
        string full=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string content=data.ToJsonString(new JsonSerializerOptions{WriteIndented=true});
        string? backup=null;
        if(File.Exists(full)){
            try{Load();backup=full+".bak";}
            catch(FormatException){backup=full+".damaged-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".bak";}
            File.Copy(full,backup,true);
        }
        string temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                byte[] bytes=Encoding.UTF8.GetBytes(content);stream.Write(bytes);stream.Flush(true);
            }
            File.Move(temporary,full,true);
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
