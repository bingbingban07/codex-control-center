using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Text.Json.Nodes;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private sealed class DocumentBuffer(KnowledgeDocumentEntry entry,string original)
    {
        public KnowledgeDocumentEntry Entry { get; }=entry;
        public string Original { get; }=original;
        public string Text { get; set; }=original;
        public bool Created { get; set; }
        public bool Changed=>Created||Text!=Original;
    }
    private Action<DocumentCategory>? selectDocumentCategory;
    private Action<string>? selectDocumentPath;
    private DocumentCategory documentsCategory=DocumentCategory.Instructions;
    private string? documentsSelectedPath,documentsHome;
    private Action<string,string>? verifyCreateMarkdown;
    private Action<string>? verifyCreateSkill;
    private Action? verifyFindMarkdown;
    private Func<string,Task>? verifyAddMarkdownFolder;
    private static string NormalizeMarkdownLines(string text)=>text.Replace("\r\n","\n").Replace('\r','\n');
    private static int MarkdownLineCount(string text)=>NormalizeMarkdownLines(text).Count(c=>c=='\n')+1;
    private static string PreserveMarkdownLineEndings(string text,string original)
    {
        string normalized=NormalizeMarkdownLines(text);if(normalized==NormalizeMarkdownLines(original))return original;
        string newline=original.Contains("\r\n")?"\r\n":original.Contains('\r')&&!original.Contains('\n')?"\r":"\n";
        return normalized.Replace("\n",newline);
    }
    private static string DocumentCategoryName(DocumentCategory category)=>category switch{
        DocumentCategory.Instructions=>"全局与项目指令",DocumentCategory.Skills=>"技能管理",_=>"角色配置"};

    private void RenderDocuments()
    {
        var s=snapshot!;var owner=pageInstance;
        if(!string.Equals(documentsHome,s.CodexHome,StringComparison.OrdinalIgnoreCase)){documentsHome=s.CodexHome;documentsCategory=DocumentCategory.Instructions;documentsSelectedPath=null;}
        var entries=configuration.GetKnowledgeDocuments(s).ToList();
        var buffers=new Dictionary<string,DocumentBuffer>(StringComparer.OrdinalIgnoreCase);
        var statuses=new Dictionary<string,TextBlock>(StringComparer.OrdinalIgnoreCase);
        var selectedByCategory=new Dictionary<DocumentCategory,string>();
        Action<DocumentCategory> changeCategory=null!;Action<string,string> createMarkdownDraft=null!;
        Action<string> createSkillDraft=null!;Func<string,Task> addProjectFolder=null!;
        string? selectedPath=null;bool selectingFile=false,loadingText=false,loadingFilters=false;string observedEditorText="";
        var intro=Section("指令、技能与角色，统一管理");
        intro.Children.Add(Note("切换分类或文件会保留草稿。底部按钮一次预览并保存三个分类的全部修改，共用一份可恢复备份。系统与插件文档只读。"));
        var tabs=new Grid{ColumnSpacing=8};var tabButtons=new Dictionary<DocumentCategory,ToggleButton>();
        foreach(var category in Enum.GetValues<DocumentCategory>()){
            int column=tabs.ColumnDefinitions.Count;tabs.ColumnDefinitions.Add(new());
            var button=new ToggleButton{Content=Text(DocumentCategoryName(category),bodySize),MinHeight=58,HorizontalAlignment=HorizontalAlignment.Stretch,Padding=new Thickness(12,8,12,8)};
            Grid.SetColumn(button,column);tabs.Children.Add(button);tabButtons.Add(category,button);button.Click+=(_,_)=>changeCategory(category);
        }
        intro.Children.Add(tabs);var scopeHint=Note("");intro.Children.Add(scopeHint);
        var precedence=Note("");intro.Children.Add(precedence);Add(Card(intro));
        string overridePath=Path.Combine(s.CodexHome,"AGENTS.override.md"),baselineOverride="";bool overrideKnown=true;
        try{baselineOverride=configuration.ReadGlobalMarkdown(s,overridePath);}catch{overrideKnown=false;}
        var browse=Section("文件列表");
        var search=new TextBox{Header="搜索文件",PlaceholderText="名称、目录、来源或用途",MinHeight=52,FontSize=bodySize};browse.Children.Add(search);
        var source=Choice("文件来源",[],"",false);browse.Children.Add(source);
        var agentsOnly=new CheckBox{Content="只看 AGENTS / AGENT 指令文件",FontSize=bodySize};browse.Children.Add(agentsOnly);
        var list=new ListView{Height=360,SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};browse.Children.Add(list);
        var empty=Note("");browse.Children.Add(empty);var summary=Note("");browse.Children.Add(summary);
        var panel=Section();var editor=new TextBox{Header="原文",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=540,FontSize=bodySize,FontFamily=new FontFamily("Cascadia Code,Consolas"),MinHeight=320,IsReadOnly=true};
        var title=Text("请选择文件",24);var location=Note("");var effect=Note("");var stats=Note("");var origin=Note("");
        panel.Children.Add(title);panel.Children.Add(origin);panel.Children.Add(location);panel.Children.Add(effect);
        var find=new TextBox{Header="在原文中查找",PlaceholderText="输入关键词后查找下一处",MinHeight=52,FontSize=bodySize};var found=Note("");
        panel.Children.Add(find);panel.Children.Add(Action("查找下一处",FindNext));panel.Children.Add(found);panel.Children.Add(editor);panel.Children.Add(stats);
        var wrap=new CheckBox{Content="自动换行",IsChecked=true,FontSize=bodySize};wrap.Checked+=(_,_)=>editor.TextWrapping=TextWrapping.Wrap;wrap.Unchecked+=(_,_)=>editor.TextWrapping=TextWrapping.NoWrap;panel.Children.Add(wrap);
        var createMarkdown=AsyncAction("新建 Markdown 文件",async()=>{
            var fields=Section();var name=new TextBox{Header="文件名",PlaceholderText="例如：补充说明.md",FontSize=bodySize,MinHeight=52};
            var folder=Choice("保存位置",["配置目录根部","prompts","instructions"],"配置目录根部",false);fields.Children.Add(name);fields.Children.Add(folder);fields.Children.Add(Note("普通 Markdown 文件不会自动成为全局指令，需要显式引用。"));
            var dialog=new ContentDialog{Title="新建指令文档草稿",Content=fields,PrimaryButtonText="创建草稿",CloseButtonText="取消",XamlRoot=Root.XamlRoot};
            if(await ShowDialogAsync(dialog)==ContentDialogResult.Primary)createMarkdownDraft(name.Text,folder.SelectedIndex==0?"":folder.SelectedItem?.ToString()??"");
        });browse.Children.Add(createMarkdown);
        var addFolder=AsyncAction("添加项目指令目录",async()=>{
            var path=new TextBox{Header="项目目录",PlaceholderText="例如：D:\\我的项目",FontSize=bodySize,MinHeight=52};
            var dialog=new ContentDialog{Title="添加项目中的 AGENTS 指令文件",Content=path,PrimaryButtonText="扫描并添加",CloseButtonText="取消",XamlRoot=Root.XamlRoot};
            if(await ShowDialogAsync(dialog)==ContentDialogResult.Primary)await addProjectFolder(path.Text.Trim());
        });browse.Children.Add(addFolder);
        var createSkill=AsyncAction("新建个人技能",async()=>{
            var name=new TextBox{Header="技能目录名",PlaceholderText="2–61 位小写字母、数字与连字符",FontSize=bodySize,MinHeight=52};
            var dialog=new ContentDialog{Title="新建个人技能草稿",Content=name,PrimaryButtonText="创建草稿",CloseButtonText="取消",XamlRoot=Root.XamlRoot};
            if(await ShowDialogAsync(dialog)==ContentDialogResult.Primary)createSkillDraft(name.Text.Trim());
        });browse.Children.Add(createSkill);
        browse.Children.Add(AsyncAction("刷新文件目录",async()=>{if(dirty){ShowNotice("请先处理全部文档草稿","保存或放弃这三个分类的草稿后，再重新读取目录。",InfoBarSeverity.Warning);return;}await ReloadAsync();}));
        Add(TileGrid(650,Card(browse),Card(panel)));

        void RefreshState(){
            dirty=buffers.Values.Any(b=>b.Changed);RefreshDraftChrome();
            summary.Text=$"本分类 {entries.Count(e=>e.Category==documentsCategory)} 份 · 当前显示 {list.Items.Count} 份 · 全部分类 {buffers.Values.Count(b=>b.Changed)} 份草稿待保存";
            foreach(var pair in tabButtons){int count=buffers.Values.Count(b=>b.Changed&&b.Entry.Category==pair.Key);((TextBlock)pair.Value.Content).Text=DocumentCategoryName(pair.Key)+(count>0?$" · {count}":"");pair.Value.IsChecked=pair.Key==documentsCategory;}
            foreach(var item in statuses){var entry=entries.First(e=>e.Path.Equals(item.Key,StringComparison.OrdinalIgnoreCase));bool pending=buffers.TryGetValue(item.Key,out var data)&&data.Changed;item.Value.Text=pending?"草稿":!entry.Editable?"只读":File.Exists(item.Key)?"已存在":"可创建";item.Value.Foreground=ToneBrush(pending?"warn":"neutral");}
            string value=buffers.TryGetValue(overridePath,out var staged)?staged.Text:baselineOverride;
            precedence.Text=!overrideKnown&&!buffers.ContainsKey(overridePath)?"全局覆盖状态暂不可读，请修正文件后刷新。":!string.IsNullOrWhiteSpace(value)?"指令优先级：AGENTS.override.md 非空，新会话优先读取它；AGENTS.md 保留为默认文件。":"指令优先级：覆盖文件为空或不存在，新会话读取非空的 AGENTS.md。";
            if(buffers.TryGetValue(overridePath,out staged)&&staged.Changed)precedence.Text+=" 以上为草稿效果，保存后才生效。";
            precedence.Visibility=documentsCategory==DocumentCategory.Instructions?Visibility.Visible:Visibility.Collapsed;
            if(selectedPath is not null&&buffers.TryGetValue(selectedPath,out var current))stats.Text=$"{MarkdownLineCount(editor.Text)} 行 · {editor.Text.Length} 字符 · "+(current.Changed?"草稿未保存":File.Exists(selectedPath)?"已读取原文件":"尚未创建，保存前不会写入磁盘");
        }
        void ClearEditor(){
            selectedPath=null;documentsSelectedPath=null;loadingText=true;try{editor.Text="";observedEditorText="";}finally{loadingText=false;}
            editor.IsReadOnly=true;title.Text="请选择文件";origin.Text="";location.Text="";effect.Text="";stats.Text="";found.Text="";
        }
        void Populate(){
            CancelUiUpdate("documents.editor");
            selectingFile=true;KnowledgeDocumentEntry? open=null;
            try{
                ClearControlItems(list);statuses.Clear();
                foreach(var entry in entries.Where(e=>e.Category==documentsCategory)
                    .Where(e=>(e.Name+" "+e.Source+" "+e.Path+" "+e.Effect).Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase))
                    .Where(e=>source.SelectedIndex==0?e.Editable:source.SelectedIndex==1||e.Source==source.SelectedItem?.ToString())
                    .Where(e=>documentsCategory!=DocumentCategory.Instructions||agentsOnly.IsChecked!=true||new[]{"AGENTS.md","AGENT.md","AGENTS.override.md"}.Contains(Path.GetFileName(e.Path),StringComparer.OrdinalIgnoreCase))){
                    bool changed=buffers.TryGetValue(entry.Path,out var buffer)&&buffer.Changed;
                    var item=new ListViewItem{Tag=entry,Content=Row(entry.Name,entry.Source+" · "+entry.Format,changed?"草稿":!entry.Editable?"只读":File.Exists(entry.Path)?"已存在":"可创建",changed?"warn":"neutral",label=>statuses[entry.Path]=label)};
                    list.Items.Add(item);if(entry.Path.Equals(selectedPath,StringComparison.OrdinalIgnoreCase))list.SelectedItem=item;
                }
                if(list.SelectedItem is null&&list.Items.Count>0){
                    var wanted=selectedByCategory.GetValueOrDefault(documentsCategory);
                    var item=list.Items.OfType<ListViewItem>().FirstOrDefault(i=>((KnowledgeDocumentEntry)i.Tag).Path.Equals(wanted,StringComparison.OrdinalIgnoreCase))??(ListViewItem)list.Items[0];
                    list.SelectedItem=item;open=(KnowledgeDocumentEntry)item.Tag;
                }
                empty.Text=list.Items.Count>0?"":entries.Any(e=>e.Category==documentsCategory)?"没有匹配文件；可清空搜索或切换来源。":documentsCategory switch{DocumentCategory.Skills=>"暂无技能，可新建个人技能。",DocumentCategory.Roles=>"暂无角色配置；此处展示 agents 目录中的现有 Markdown 和 TOML 文件。",_=>"暂无指令文档，可新建 Markdown 或添加项目目录。"};
                empty.Visibility=list.Items.Count==0?Visibility.Visible:Visibility.Collapsed;
            }finally{selectingFile=false;}
            if(open is not null)TryOpen(open);else if(list.Items.Count==0)ClearEditor();RefreshState();
        }
        void RequestPopulate(){if(!loadingFilters)QueueUiUpdate("documents.files",owner,Populate);}
        void SwitchCategory(DocumentCategory category){
            if(!ReferenceEquals(owner,pageInstance))return;
            CancelUiUpdate("documents.editor");CancelUiUpdate("documents.files");
            if(selectedPath is not null)selectedByCategory[documentsCategory]=selectedPath;
            documentsCategory=category;selectedPath=selectedByCategory.GetValueOrDefault(category);loadingFilters=true;
            try{
                search.Text="";agentsOnly.IsChecked=false;ClearControlItems(source);
                foreach(string item in new[]{"可编辑文件","全部来源"}.Concat(entries.Where(e=>e.Category==category).Select(e=>e.Source).Distinct()))source.Items.Add(item);
                source.SelectedIndex=0;if(selectedPath is not null&&entries.FirstOrDefault(e=>e.Path.Equals(selectedPath,StringComparison.OrdinalIgnoreCase)) is {Editable:false})source.SelectedIndex=1;
            }finally{loadingFilters=false;}
            agentsOnly.Visibility=category==DocumentCategory.Instructions?Visibility.Visible:Visibility.Collapsed;
            createMarkdown.Visibility=addFolder.Visibility=category==DocumentCategory.Instructions?Visibility.Visible:Visibility.Collapsed;
            createSkill.Visibility=category==DocumentCategory.Skills?Visibility.Visible:Visibility.Collapsed;
            scopeHint.Text=category switch{DocumentCategory.Instructions=>"全局、项目和补充指令。普通 Markdown 需显式引用；项目指令只影响对应目录。",DocumentCategory.Skills=>"查看个人与共享技能；系统和插件资料只读。读取原文不会执行其中的命令。",_=>"编辑已有角色的 Markdown 与 TOML 配置；TOML 保存前校验格式。"};
            if(selectedPath is not null&&entries.FirstOrDefault(e=>e.Path.Equals(selectedPath,StringComparison.OrdinalIgnoreCase)) is {} remembered)TryOpen(remembered);
            Populate();
        }
        void Open(KnowledgeDocumentEntry entry){
            if(!buffers.TryGetValue(entry.Path,out var buffer)){buffer=new(entry,configuration.ReadKnowledgeDocument(s,entry.Path));buffers.Add(entry.Path,buffer);}
            if(!entries.Any(e=>e.Path.Equals(entry.Path,StringComparison.OrdinalIgnoreCase)))entries.Add(entry);
            selectedPath=entry.Path;documentsSelectedPath=entry.Path;selectedByCategory[entry.Category]=entry.Path;
            loadingText=true;try{editor.Text=NormalizeMarkdownLines(buffer.Text).Replace('\n','\r');observedEditorText=editor.Text;}finally{loadingText=false;}
            editor.IsReadOnly=!entry.Editable;title.Text=entry.Name;origin.Text=$"{entry.Source} · {entry.Format} · "+(entry.Editable?"可编辑":"只读");location.Text=entry.Path;
            effect.Text=entry.Effect+(Path.GetFileName(entry.Path).Equals("AGENTS.md",StringComparison.OrdinalIgnoreCase)?" 其他配置页可能更新受管理段落；外部冲突时会阻止保存。":"");found.Text="";
            selectingFile=true;try{list.SelectedItem=list.Items.OfType<ListViewItem>().FirstOrDefault(i=>((KnowledgeDocumentEntry)i.Tag).Path.Equals(entry.Path,StringComparison.OrdinalIgnoreCase));}finally{selectingFile=false;}
            RefreshState();
        }
        void TryOpen(KnowledgeDocumentEntry entry){try{Open(entry);}catch(Exception error){ClearEditor();ShowError("读取文件失败",error);}}
        void SelectPath(string path){
            var entry=entries.FirstOrDefault(e=>e.Path.Equals(Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))??throw new FileNotFoundException("文件未列入当前文档目录。",path);
            selectedByCategory[entry.Category]=entry.Path;SwitchCategory(entry.Category);Open(entry);
        }
        void AddDraft(string path,string text){
            var entry=entries.FirstOrDefault(e=>e.Path.Equals(path,StringComparison.OrdinalIgnoreCase))??configuration.DescribeKnowledgeDocument(s,path);
            if(File.Exists(path)||buffers.ContainsKey(path)){if(!entries.Any(e=>e.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))entries.Add(entry);SelectPath(path);return;}
            var original=configuration.ReadKnowledgeDocument(s,path);buffers[path]=new(entry,original){Created=true,Text=text};if(!entries.Any(e=>e.Path.Equals(path,StringComparison.OrdinalIgnoreCase)))entries.Add(entry);
            selectedByCategory[entry.Category]=path;SwitchCategory(entry.Category);Open(entry);SetFooter("新文件为内存草稿 · 与全部分类的修改一起预览、备份并保存");
        }
        void CreateMarkdown(string fileName,string folder)=>AddDraft(configuration.NewGlobalMarkdownPath(fileName.Trim(),folder),"# "+Path.GetFileNameWithoutExtension(fileName)+"\n\n");
        void CreateSkill(string slug)=>AddDraft(configuration.NewPersonalSkillPath(slug),$"---\nname: {slug}\ndescription: 请填写技能适用条件\n---\n\n# {slug}\n\n请填写执行步骤。\n");
        async Task AddFolder(string path){
            var service=configuration;var added=await Task.Run(()=>service.RegisterMarkdownFolder(s,path));
            var prefs=ReadPreferences();var folders=(prefs["markdownFolders"] as JsonArray)?.Select(n=>n?.GetValue<string>()??"").Where(p=>p.Length>0).ToList()??[];
            string full=Path.GetFullPath(path);if(!folders.Contains(full,StringComparer.OrdinalIgnoreCase))folders.Add(full);
            prefs["markdownFolders"]=new JsonArray(folders.TakeLast(16).Select(p=>(JsonNode?)JsonValue.Create(p)).ToArray());WritePreferences(prefs);
            if(!ReferenceEquals(owner,pageInstance))return;
            watcher?.Dispose();watcher=new WorkspaceChangeMonitor((snapshot??s).FileHashes.Keys.Concat(s.FileHashes.Keys));
            foreach(var entry in service.GetKnowledgeDocuments(s))if(!entries.Any(e=>e.Path.Equals(entry.Path,StringComparison.OrdinalIgnoreCase)))entries.Add(entry);
            SwitchCategory(DocumentCategory.Instructions);source.SelectedItem="项目指令";Populate();SetFooter($"已找到 {added.Count} 份项目指令 · 按完整路径区分同名文件");
        }
        void FindNext(){
            string query=find.Text;if(query.Length==0){found.Text="请先输入关键词。";return;}
            int start=Math.Min(editor.Text.Length,editor.SelectionStart+editor.SelectionLength),index=editor.Text.IndexOf(query,start,StringComparison.OrdinalIgnoreCase);
            if(index<0)index=editor.Text.IndexOf(query,StringComparison.OrdinalIgnoreCase);
            if(index<0){found.Text="没有找到匹配内容。";return;}editor.Select(index,query.Length);editor.Focus(FocusState.Programmatic);found.Text=$"已定位到第 {MarkdownLineCount(editor.Text[..index])} 行。";
        }
        editor.TextChanging+=(_,_)=>{
            if(loadingText||!ReferenceEquals(owner,pageInstance)||selectedPath is null||editor.Text==observedEditorText||!buffers[selectedPath].Entry.Editable)return;
            observedEditorText=editor.Text;var buffer=buffers[selectedPath];buffer.Text=PreserveMarkdownLineEndings(editor.Text,buffer.Original);RefreshState();SetFooter("草稿已保留 · 可切换分类或文件，最后统一预览并保存全部修改");
        };
        list.SelectionChanged+=(_,_)=>{
            if(selectingFile||!ReferenceEquals(owner,pageInstance)||list.SelectedItem is not ListViewItem{Tag:KnowledgeDocumentEntry entry}||entry.Path.Equals(selectedPath,StringComparison.OrdinalIgnoreCase))return;
            QueueUiUpdate("documents.editor",owner,()=>{if(entry.Category==documentsCategory)TryOpen(entry);});
        };
        search.TextChanged+=(_,_)=>RequestPopulate();source.SelectionChanged+=(_,_)=>RequestPopulate();agentsOnly.Checked+=(_,_)=>RequestPopulate();agentsOnly.Unchecked+=(_,_)=>RequestPopulate();
        planFactory=()=>configuration.PlanKnowledgeDocuments(s,buffers.Values.Where(b=>b.Changed).ToDictionary(b=>b.Entry.Path,b=>b.Text,StringComparer.OrdinalIgnoreCase));
        changeCategory=SwitchCategory;createMarkdownDraft=CreateMarkdown;createSkillDraft=CreateSkill;addProjectFolder=AddFolder;
        selectDocumentCategory=SwitchCategory;selectDocumentPath=SelectPath;verifyCreateMarkdown=CreateMarkdown;verifyCreateSkill=CreateSkill;verifyFindMarkdown=FindNext;verifyAddMarkdownFolder=AddFolder;
        if(documentsSelectedPath is not null&&entries.FirstOrDefault(e=>e.Path.Equals(documentsSelectedPath,StringComparison.OrdinalIgnoreCase)) is {} previous)selectedByCategory[previous.Category]=previous.Path;
        if(!selectedByCategory.ContainsKey(DocumentCategory.Instructions))selectedByCategory[DocumentCategory.Instructions]=s.AgentsPath;
        SwitchCategory(documentsCategory);
    }
}
