namespace CodexControlCenter.Core;

/// <summary>A display-only suffix. Never a provider, route or authentication ID.</summary>
public static class ModelDisplayLabel
{
    public static (string Name,string Label) Split(string? displayName)
    {
        string text=(displayName??"").TrimEnd();
        if(text.Length==0)return (text,"");
        char close=text[^1],open=close switch{']'=>'[','】'=>'【',_=>'\0'};
        int index=open=='\0'?-1:text.LastIndexOf(open);
        if(index<=0)return (text,"");
        return (text[..index].TrimEnd(),text[(index+1)..^1]);
    }

    public static string WithLabel(string displayName,string label)
    {
        string name=Split(displayName).Name;
        label=label.Trim();
        if(label.Length>1&&(label[0]=='['&&label[^1]==']'||label[0]=='【'&&label[^1]=='】'))label=label[1..^1].Trim();
        return label.Length==0?name:name+" ["+label+"]";
    }
}
