using Tomlyn.Model;

namespace CodexControlCenter.Core;

public static class EnhancementMode
{
    public const string PluginId="codex-enhancements@codex-control-center";
    public static bool IsEnabled(string? codexHome=null)
    {
        var home=codexHome??Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        try{
            string config=Path.Combine(home,"config.toml");
            if(!File.Exists(config))return !File.Exists(Path.Combine(home,"control-center","enhancement-mode","state.json"));
            var table=TomlEditor.ReadTable(File.ReadAllText(config),config);
            if(table.TryGetValue("plugins",out var plugins)&&plugins is TomlTable entries&&entries.TryGetValue(PluginId,out var plugin)&&plugin is TomlTable settings)
                return settings.TryGetValue("enabled",out var enabled)&&enabled is true;
            return !File.Exists(Path.Combine(home,"control-center","enhancement-mode","state.json"));
        }catch{return false;}
    }
}
