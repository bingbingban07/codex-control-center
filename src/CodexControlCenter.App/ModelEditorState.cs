using CodexControlCenter.Core;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private sealed record ModelEditorState(string Id,string Name,string Provider,string Upstream,string Context,string Efforts,string Protocol,string Compaction,string Budget)
    {
        public static ModelEditorState From(ModelEntry model)=>new(model.Id,model.DisplayName,model.Provider,
            string.IsNullOrEmpty(model.UpstreamModel)?model.Id:model.UpstreamModel,model.ContextWindow.ToString(),model.Efforts,model.AgentProtocol,
            model.AutoCompactTokenLimit?.ToString()??"",model.EffectiveContextWindowPercent.ToString());
        public ModelEntry ToModel(bool updateCompaction)=>new(){Id=Id.Trim(),DisplayName=Name.Trim(),Provider=Provider,UpstreamModel=Upstream.Trim(),
            ContextWindow=long.TryParse(Context,out long context)?context:0,Efforts=Efforts,AgentProtocol=Protocol,
            AutoCompactTokenLimit=string.IsNullOrWhiteSpace(Compaction)?null:long.TryParse(Compaction,out long compact)?compact:0,
            EffectiveContextWindowPercent=int.TryParse(Budget,out int budget)?budget:0,UpdateCompactionSettings=updateCompaction,Mapped=true};
        public bool CompactionChanged(ModelEditorState original)=>Context!=original.Context||Compaction!=original.Compaction||Budget!=original.Budget;
    }
}
