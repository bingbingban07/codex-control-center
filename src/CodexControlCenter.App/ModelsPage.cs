using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private Func<Task>? verifyModelDraft;
    private Func<Task>? verifyModelVisibilityBulk;
    private Func<Task>? verifyModelLabels;
    private void RenderModels()
    {
        var s=snapshot!;
        var owner=pageInstance;long editorRevision=0;
        bool mappingChanged=false,reverting=false,filtering=false,defaultsTouched=false;
        Func<ModelEntry>? mappingDraft=null;
        object? selectedItem=null;
        string? editingModelId=s.MainModel;
        string newModelKey="new-model-"+Guid.NewGuid().ToString("N");
        var modelEdits=new Dictionary<string,ModelEditorState>(StringComparer.Ordinal);
        var expandedModels=new HashSet<string>(StringComparer.Ordinal);
        var itemKeys=new Dictionary<ListViewItem,string>();
        var visibilityChanges=new Dictionary<string,string>(StringComparer.Ordinal);
        var editSummary=Note("");
        string EffectiveVisibility(ModelEntry entry)=>visibilityChanges.GetValueOrDefault(entry.Id)??entry.Visibility;
        var defaults=Section("默认主模型");
        var model=Choice("模型",s.Models.Where(m=>m.Mapped).Select(m=>m.Id),s.MainModel,selectFirst:false);
        var effort=Choice("推理强度",EffortsFor(s.MainModel),s.MainEffort,selectFirst:false);
        object? observedModel=model.SelectedItem,observedEffort=effort.SelectedItem;
        model.SelectionChanged+=(_,_)=>{
            if(Equals(observedModel,model.SelectedItem))return;observedModel=model.SelectedItem;defaultsTouched=true;
            string? previous=effort.SelectedItem?.ToString();ClearControlItems(effort);
            foreach(var e in EffortsFor(model.SelectedItem?.ToString()??""))effort.Items.Add(e);
            effort.SelectedItem=previous;if(effort.SelectedIndex<0&&effort.Items.Count>0)effort.SelectedIndex=0;
            RefreshModelDirty();
        };
        effort.SelectionChanged+=(_,_)=>{if(Equals(observedEffort,effort.SelectedItem))return;observedEffort=effort.SelectedItem;defaultsTouched=true;RefreshModelDirty();};
        void RefreshModelDirty(){if(busy)return;dirty=modelEdits.Count>0||visibilityChanges.Count>0||defaultsTouched&&(model.SelectedItem?.ToString()!=s.MainModel||effort.SelectedItem?.ToString()!=s.MainEffort);RefreshDraftChrome();}
        var fields=new Grid{ColumnSpacing=20};fields.ColumnDefinitions.Add(new(){Width=new GridLength(2,GridUnitType.Star)});fields.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        fields.Children.Add(model);Grid.SetColumn(effort,1);fields.Children.Add(effort);defaults.Children.Add(fields);
        defaults.Children.Add(Note("默认模型与所有模型编辑一并预览保存。已有聊天可能保留独立选择；模型目录修改需重启 Codex。"));var defaultsCard=Card(defaults);
        defaults.Children.Add(editSummary);
        if(model.SelectedItem is null)defaults.Children.Add(new InfoBar{IsOpen=true,IsClosable=false,Severity=InfoBarSeverity.Warning,Title="当前默认模型尚未连接",Message=$"当前值：{s.MainModel}。不会自动替换；需要切换时请明确选择一个已连接模型。"});
        var heading=Section("模型目录与显示管理");
        heading.Children.Add(Note("隐藏的模型仍保留在此管理台，可随时恢复显示。下面的筛选仅影响当前视图。"));
        var search=new TextBox{Header="搜索模型",PlaceholderText="搜索模型名称、编号、上游 ID 或来源",MinHeight=52,FontSize=bodySize};heading.Children.Add(search);
        string ProviderLabel(ProviderEntry p)=>ProviderName(p)+" ["+p.Id+"]";
        var sourceFilter=Choice("按接入来源筛选",new[]{"全部来源"}.Concat(s.Providers.Select(ProviderLabel)),"全部来源",false);
        var visibilityFilter=Choice("按显示状态筛选",["全部模型","Codex 中显示","Codex 中隐藏","显示规则未声明或其他","缺少路由映射","默认主模型或子代理"],"全部模型",false);
        var sort=Choice("排序方式",["目录原顺序","按模型名称","按接入来源","按上下文长度"],"目录原顺序",false);
        heading.Children.Add(TileGrid(220,sourceFilter,visibilityFilter,sort));
        var filteredCount=Note("");heading.Children.Add(filteredCount);
        var visibilitySummary=Note("");heading.Children.Add(visibilitySummary);
        var visibleModels=new List<ModelEntry>();
        var list=new ListView{Height=570,SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch};heading.Children.Add(list);
        var edit=Section("模型映射与能力");var layout=new Grid{ColumnSpacing=24,RowSpacing=24};layout.ColumnDefinitions.Add(new());layout.ColumnDefinitions.Add(new());layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.Children.Add(heading);var card=Card(edit);Grid.SetColumn(card,1);layout.Children.Add(card);
        layout.SizeChanged+=(_,_)=>{bool narrow=layout.ActualWidth<1050;Grid.SetColumn(card,narrow?0:1);Grid.SetRow(card,narrow?1:0);Grid.SetColumnSpan(heading,narrow?2:1);Grid.SetColumnSpan(card,narrow?2:1);};Add(layout);Add(defaultsCard);
        TextBox? displayEditor=null;
        TextBox? labelEditor=null;
        Action? refreshVisibilityEditor=null;
        void Edit(ModelEntry? entry,string? entryKey=null)
        {
            UiCrashLog.Write("model.editor.begin",entry?.Id??"new-model");
            long revision=++editorRevision;ClearPanelChildren(edit);editingModelId=entryKey??entry?.Id??newModelKey;mappingChanged=modelEdits.ContainsKey(editingModelId);refreshVisibilityEditor=null;
            bool isNew=editingModelId==newModelKey;
            var original=s.Models.FirstOrDefault(m=>m.Id==editingModelId)??new ModelEntry{AgentProtocol="v1"};
            var baseline=ModelEditorState.From(original);
            var raw=modelEdits.GetValueOrDefault(editingModelId)??baseline;
            var m=raw.ToModel(isNew||raw.CompactionChanged(baseline));
            m.Visibility=isNew?"list":original.Visibility;m.Mapped=!isNew&&original.Mapped;
            TextBlock? modelHeading=null;
            if(!isNew){
                modelHeading=Text(string.IsNullOrWhiteSpace(m.DisplayName)?m.Id:m.DisplayName,24);edit.Children.Add(modelHeading);
                edit.Children.Add(Note("模型编号："+m.Id));
                var options=new[]{"显示在 Codex 模型列表","从 Codex 模型列表隐藏"}.ToList();
                string current=EffectiveVisibility(m);
                if(current is not ("list" or "hide"))options.Add("保持原设置（未声明或其他规则）");
                var visibility=Choice("Codex 列表显示",options,current switch{"list"=>options[0],"hide"=>options[1],_=>options[^1]},false,selectFirst:false);
                var notice=Note("");
                bool refreshingVisibility=false;
                void ExplainVisibility(){notice.Text=EffectiveVisibility(m)=="hide"?(m.Id==s.MainModel||m.Id==s.AgentModel?"这是当前默认模型。隐藏仅影响选择列表，现有默认值和聊天保持原模型。":"保存并重启 Codex 后从选择列表隐藏；管理台中仍可找到它。"):"显示名称用于识别模型；模型编号与来源决定实际调用。目录变化需重启 Codex。";}
                refreshVisibilityEditor=()=>{refreshingVisibility=true;try{visibility.SelectedIndex=EffectiveVisibility(m) switch{"list"=>0,"hide"=>1,_=>2};ExplainVisibility();}finally{refreshingVisibility=false;}};
                visibility.SelectionChanged+=(_,_)=>{if(refreshingVisibility||revision!=editorRevision||!ReferenceEquals(owner,pageInstance))return;if(visibility.SelectedIndex is 0 or 1){StageVisibility(m,visibility.SelectedIndex==0?"list":"hide");}else if(visibilityChanges.Remove(m.Id)){MarkDirty();RequestFilter();}ExplainVisibility();};
                edit.Children.Add(visibility);ExplainVisibility();edit.Children.Add(notice);
            }else edit.Children.Add(Pill("新模型保存后默认显示在 Codex 列表"));
            var id=Input("Codex 模型编号",raw.Id,track:false);id.IsReadOnly=!isNew;
            var display=Input("显示名称",raw.Name,track:false);displayEditor=display;
            var label=Input("中括号标签（留空去掉）",ModelDisplayLabel.Split(raw.Name).Label,track:false);labelEditor=label;
            label.PlaceholderText="例如：我的订阅、备用、MiniMax";
            var namePreview=Note("列表预览："+raw.Name);
            string Label(ProviderEntry p)=>ProviderName(p)+" ["+p.Id+"]";
            var provider=Choice("接入来源 / Key",s.Providers.Select(Label),s.Providers.FirstOrDefault(p=>p.Id==m.Provider) is {} source?Label(source):"",false,selectFirst:false);
            var upstream=Input("服务商模型名称（上游 ID）",raw.Upstream,track:false);
            var context=Input("上下文长度（Token）",raw.Context,track:false);
            var compact=Input("自动压缩阈值（Token，留空使用模型默认）",raw.Compaction,track:false);
            var budget=Input("有效上下文预算（百分比）",raw.Budget,track:false);
            var efforts=Input("支持的推理档位（逗号分隔）",m.Efforts,track:false);var protocol=Choice("子代理协议",new[]{"","v1","v2"}.Append(m.AgentProtocol).Distinct(),m.AgentProtocol,false);
            foreach(var control in new UIElement[]{id,display,label,namePreview})edit.Children.Add(control);
            edit.Children.Add(Note("只修改名称末尾 [ ] 内的说明，不会切换实际来源或密钥。留空会移除末尾标签；底部预览保存后，重开 Codex 读取新名称。"));
            foreach(var control in new UIElement[]{provider,upstream})edit.Children.Add(control);
            var advanced=Section();foreach(var control in new UIElement[]{context,compact,budget,efforts,protocol})advanced.Children.Add(control);
            var capacity=Note("");advanced.Children.Add(capacity);
            void ExplainCapacity(){bool valid=long.TryParse(context.Text,out long window)&&window>0&&int.TryParse(budget.Text,out int percent)&&percent is >0 and <=100;
                if(!valid){capacity.Text="请填写有效的上下文长度和 1–100 的预算百分比。";capacity.Foreground=ToneBrush("warn");return;}
                long effective=(long)((decimal)window*int.Parse(budget.Text)/100);bool custom=long.TryParse(compact.Text,out long limit);
                capacity.Text=$"配置窗口 {window:N0} · 有效预算 {effective:N0} Token"+(custom?$" · 压缩阈值占窗口 {(decimal)limit/window:0.#%}":" · 使用模型默认压缩策略");
                capacity.Foreground=ToneBrush((compact.Text.Length>0&&(!custom||limit<=0||limit>effective))?"warn":"neutral");
            }
            ExplainCapacity();advanced.Children.Add(Note("按服务商能力填写；压缩阈值应小于等于有效预算。这些设置将在 Codex 重新读取模型目录后生效。"));
            string keyForExpansion=editingModelId!;
            var capability=new Expander{Header="模型能力与高级设置",Content=advanced,IsExpanded=isNew||expandedModels.Contains(keyForExpansion),HorizontalAlignment=HorizontalAlignment.Stretch};
            capability.Expanding+=(_,_)=>expandedModels.Add(keyForExpansion);capability.Collapsed+=(_,_)=>expandedModels.Remove(keyForExpansion);edit.Children.Add(capability);
            edit.Children.Add(Note("可继续选择其他模型；每个模型的原文草稿会保留，底部统一预览并保存。"));
            ModelEditorState Current()=>new(id.Text,display.Text,s.Providers.FirstOrDefault(p=>Label(p)==provider.SelectedItem?.ToString())?.Id??"",upstream.Text,context.Text,efforts.Text,protocol.SelectedItem?.ToString()??"",compact.Text,budget.Text);
            mappingDraft=()=>Current().ToModel(isNew||Current().CompactionChanged(baseline));
            var observed=Current();
            void Changed(){if(revision!=editorRevision||!ReferenceEquals(owner,pageInstance))return;var now=Current();if(now==observed)return;observed=now;
                if(now==baseline&&!isNew)modelEdits.Remove(editingModelId!);else modelEdits[editingModelId!]=now;
                mappingChanged=modelEdits.ContainsKey(editingModelId!);ExplainCapacity();RefreshModelDirty();
                if(modelHeading is not null)modelHeading.Text=string.IsNullOrWhiteSpace(now.Name)?now.Id:now.Name;
                editSummary.Text=$"{modelEdits.Count} 个模型编辑草稿 · {visibilityChanges.Count} 个显示草稿将一起预览保存";
                if(selectedItem is ListViewItem selected&&selected.Content is StackPanel row){
                    var title=row.Children.OfType<TextBlock>().FirstOrDefault();if(title is not null)title.Text=string.IsNullOrWhiteSpace(now.Name)?now.Id:now.Name;
                    var marker=row.Children.OfType<Border>().LastOrDefault(b=>b.Tag?.ToString()=="model-draft-marker");if(marker is not null)marker.Visibility=mappingChanged?Visibility.Visible:Visibility.Collapsed;
                }
                SetFooter($"{modelEdits.Count} 份模型草稿已保留 · 可继续切换，预览后一起保存");}
            // Capture raw input synchronously; deferred TextChanged must not race with navigation.
            foreach(var input in new[]{id,display,upstream,context,compact,budget,efforts})input.TextChanging+=(_,_)=>Changed();provider.SelectionChanged+=(_,_)=>Changed();protocol.SelectionChanged+=(_,_)=>Changed();
            bool syncingLabel=false;
            display.TextChanging+=(_,_)=>{if(syncingLabel||revision!=editorRevision||!ReferenceEquals(owner,pageInstance))return;syncingLabel=true;try{label.Text=ModelDisplayLabel.Split(display.Text).Label;namePreview.Text="列表预览："+display.Text;}finally{syncingLabel=false;}};
            label.TextChanging+=(_,_)=>{if(syncingLabel||revision!=editorRevision||!ReferenceEquals(owner,pageInstance))return;syncingLabel=true;try{display.Text=ModelDisplayLabel.WithLabel(display.Text,label.Text);namePreview.Text="列表预览："+display.Text;}finally{syncingLabel=false;}};
            if(!isNew&&original.Mapped)edit.Children.Add(Action("设为默认主模型",()=>{model.SelectedItem=original.Id;MarkDirty();}));
            edit.Children.Add(Action("撤销此模型的编辑",()=>QueueUiUpdate("models.editor",owner,()=>{modelEdits.Remove(editingModelId!);Edit(isNew?null:original,isNew?newModelKey:original.Id);RefreshModelDirty();RequestFilter();})));
            UiCrashLog.Write("model.editor.complete",entry?.Id??"new-model");
        }
        void StageVisibility(ModelEntry entry,string value){if(value==entry.Visibility)visibilityChanges.Remove(entry.Id);else visibilityChanges[entry.Id]=value;RefreshModelDirty();RequestFilter();SetFooter($"{visibilityChanges.Count} 个模型的显示设置待保存 · 预览后统一备份应用");}
        void RequestFilter()=>QueueUiUpdate("models.filter",owner,Filter);
        void Filter(){filtering=true;try{
            string providerId=s.Providers.FirstOrDefault(p=>ProviderLabel(p)==sourceFilter.SelectedItem?.ToString())?.Id??"";
            var rows=s.Models.Select(m=>{var staged=modelEdits.TryGetValue(m.Id,out var state)?state.ToModel(state.CompactionChanged(ModelEditorState.From(m))):m;staged.Visibility=m.Visibility;staged.Mapped=m.Mapped;return (Key:m.Id,Model:staged);}).ToList();
            if(modelEdits.TryGetValue(newModelKey,out var added)){var fresh=added.ToModel(true);fresh.Visibility="list";fresh.Mapped=false;rows.Add((newModelKey,fresh));}
            var matching=rows.Where(row=>{var m=row.Model;return (m.Id+" "+m.DisplayName+" "+m.UpstreamModel+" "+m.Provider+" "+SourceName(m.Provider)).Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase);})
                .Where(row=>{var m=row.Model;return (sourceFilter.SelectedIndex==0||m.Provider==providerId)&&(visibilityFilter.SelectedIndex switch{
                    1=>EffectiveVisibility(m)=="list",2=>EffectiveVisibility(m)=="hide",3=>EffectiveVisibility(m) is not ("list" or "hide"),4=>!m.Mapped,5=>m.Id==s.MainModel||m.Id==s.AgentModel,_=>true});});
            matching=sort.SelectedIndex switch{1=>matching.OrderBy(row=>row.Model.DisplayName,StringComparer.CurrentCultureIgnoreCase),2=>matching.OrderBy(row=>SourceName(row.Model.Provider),StringComparer.CurrentCultureIgnoreCase),3=>matching.OrderByDescending(row=>row.Model.ContextWindow),_=>matching};
            var filtered=matching.ToList();visibleModels=filtered.Where(row=>row.Key!=newModelKey).Select(row=>row.Model).ToList();
            /* Filtering only changes the list, preserving the current editor and all raw drafts. */
            ClearControlItems(list);selectedItem=null;modelLiveLabels.Clear();itemKeys.Clear();
            foreach(var rowEntry in filtered){var m=rowEntry.Model;
                var row=Section();row.Spacing=7;row.Children.Add(Text(string.IsNullOrWhiteSpace(m.DisplayName)?m.Id:m.DisplayName,20));row.Children.Add(Note(m.Id));
                var badges=new List<FrameworkElement>{Pill(EffectiveVisibility(m) switch{"list"=>"Codex 中显示","hide"=>"Codex 中隐藏",_=>"显示规则未声明或其他"},EffectiveVisibility(m)=="hide"?"warn":"neutral")};
                if(m.Id==s.MainModel)badges.Add(Pill("默认主模型"));if(m.Id==s.AgentModel)badges.Add(Pill("默认子代理"));
                row.Children.Add(EvenGrid(180,3,badges.ToArray()));row.Children.Add(Note(SourceName(m.Provider)+" · "+(m.ContextWindow>0?m.ContextWindow.ToString("N0")+" Token":"上下文待填写")));
                var status=Text(m.Status,15);status.Foreground=ToneBrush(m.Mapped?"neutral":"warn");row.Children.Add(status);modelLiveLabels[m.Id]=status;
                if(visibilityChanges.ContainsKey(m.Id))row.Children.Add(Pill("显示设置待保存","warn"));
                var marker=Pill(rowEntry.Key==newModelKey?"新增模型草稿":"模型编辑待保存","warn");marker.Tag="model-draft-marker";marker.Visibility=modelEdits.ContainsKey(rowEntry.Key)?Visibility.Visible:Visibility.Collapsed;row.Children.Add(marker);
                var item=new ListViewItem{Content=row,Tag=m,Padding=new Thickness(14,12,14,12)};itemKeys[item]=rowEntry.Key;list.Items.Add(item);if(rowEntry.Key==editingModelId){selectedItem=item;list.SelectedItem=item;}
            }
            filteredCount.Text=$"当前展示 {list.Items.Count} / {s.Models.Count} 个模型"+(mappingChanged?" · 当前模型编辑仍保留":"");
            visibilitySummary.Text=$"Codex 列表：{s.Models.Count(m=>EffectiveVisibility(m)=="list")} 个显示 · {s.Models.Count(m=>EffectiveVisibility(m)=="hide")} 个隐藏 · {visibilityChanges.Count} 项待保存";
            editSummary.Text=modelEdits.Count>0?$"{modelEdits.Count} 个模型编辑草稿 · {visibilityChanges.Count} 个显示草稿将一起预览保存":"切换模型可查看与编辑；修改后以草稿保留。";
            if(filtered.Count==0)filteredCount.Text="没有匹配模型。可清除筛选，或在下方新增模型；已有编辑草稿保持保留。";
        }finally{filtering=false;}}
        list.SelectionChanged+=(_,_)=>{
            if(reverting||filtering||!ReferenceEquals(owner,pageInstance)||ReferenceEquals(list.SelectedItem,selectedItem))return;
            var requested=list.SelectedItem;
            QueueUiUpdate("models.editor",owner,()=>{
                if(!ReferenceEquals(requested,list.SelectedItem))return;
                if(requested is ListViewItem{Tag:ModelEntry m} item){selectedItem=requested;Edit(m,itemKeys[item]);}
            });
        };
        search.TextChanged+=(_,_)=>RequestFilter();sourceFilter.SelectionChanged+=(_,_)=>RequestFilter();visibilityFilter.SelectionChanged+=(_,_)=>RequestFilter();sort.SelectionChanged+=(_,_)=>RequestFilter();Filter();Edit(s.Models.FirstOrDefault(m=>m.Id==s.MainModel));
        heading.Children.Insert(heading.Children.IndexOf(list),TileGrid(210,Action("显示筛选结果",()=>BulkVisibility("list")),Action("隐藏筛选结果",()=>BulkVisibility("hide")),Action("清除筛选",()=>{search.Text="";sourceFilter.SelectedIndex=0;visibilityFilter.SelectedIndex=0;})));
        void BulkVisibility(string value){
            foreach(var entry in visibleModels.ToArray()){if(value==entry.Visibility)visibilityChanges.Remove(entry.Id);else visibilityChanges[entry.Id]=value;}
            if(visibleModels.Count>0){RefreshModelDirty();RequestFilter();refreshVisibilityEditor?.Invoke();}
        }
        heading.Children.Add(Action("新增模型",()=>QueueUiUpdate("models.editor",owner,()=>{reverting=true;list.SelectedItem=null;selectedItem=null;reverting=false;Edit(null,newModelKey);})));
        planFactory=()=>{
            var plan=new ChangePlan();string nextModel=model.SelectedItem?.ToString()??"",nextEffort=effort.SelectedItem?.ToString()??"";
            if(modelEdits.TryGetValue(newModelKey,out var newModel)&&s.Models.Any(m=>string.Equals(m.Id,newModel.Id.Trim(),StringComparison.OrdinalIgnoreCase))){plan.Errors.Add("新增模型编号已存在，请填写唯一编号。");return plan;}
            if(defaultsTouched&&(nextModel!=s.MainModel||nextEffort!=s.MainEffort)){var def=configuration.PlanDefaults(s,nextModel,nextEffort,s.AgentModel,s.AgentEffort,s.AgentConcurrency);plan.Files.AddRange(def.Files);plan.Errors.AddRange(def.Errors);}
            if(modelEdits.Count>0){
                var nameOnly=new Dictionary<string,string>(StringComparer.Ordinal);
                var mappings=new List<ModelEntry>();
                foreach(var pair in modelEdits){
                    var baseline=s.Models.FirstOrDefault(m=>m.Id==pair.Key);
                    if(baseline is not null&&(pair.Value with{Name=baseline.DisplayName})==ModelEditorState.From(baseline))nameOnly[pair.Key]=pair.Value.Name;
                    else mappings.Add(pair.Value.ToModel(baseline is null||pair.Value.CompactionChanged(ModelEditorState.From(baseline))));
                }
                var mapped=configuration.PlanModels(s,mappings);plan.Files.AddRange(mapped.Files);plan.Errors.AddRange(mapped.Errors);
                var named=configuration.PlanModelDisplayNames(s,nameOnly,plan.Files.FirstOrDefault(f=>f.Path==s.CatalogPath)?.Content);
                if(named.Errors.Count==0&&named.Files.Count>0){plan.Files.RemoveAll(f=>f.Path==s.CatalogPath);plan.Files.AddRange(named.Files);}plan.Errors.AddRange(named.Errors);
            }
            if(visibilityChanges.Count>0){
                var visibility=configuration.PlanModelVisibility(s,visibilityChanges,plan.Files.FirstOrDefault(f=>f.Path==s.CatalogPath)?.Content);
                if(visibility.Errors.Count==0){plan.Files.RemoveAll(f=>f.Path==s.CatalogPath);plan.Files.AddRange(visibility.Files);}plan.Errors.AddRange(visibility.Errors);
            }
            return plan;
        };
        verifyModelDraft=async()=>{
            if(displayEditor is null||s.Models.Count<2)return;
            string old=displayEditor.Text;displayEditor.Text=old+" · 自检";await Task.Delay(100);
            var alternative=s.Models.FirstOrDefault(m=>m.Mapped&&m.Id!=s.MainModel);if(alternative is not null)model.SelectedItem=alternative.Id;await Task.Delay(100);
            var combined=planFactory();if(combined.Errors.Count>0)throw new InvalidOperationException(string.Join(";",combined.Errors));
            if(!combined.Files.Any(f=>string.Equals(f.Path,s.CatalogPath,StringComparison.OrdinalIgnoreCase))||alternative is not null&&!combined.Files.Any(f=>string.Equals(f.Path,s.ConfigPath,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("模型草稿合并失败："+string.Join(",",combined.Files.Select(f=>f.Path)));
            var other=list.Items.OfType<ListViewItem>().FirstOrDefault(i=>!ReferenceEquals(i,selectedItem));if(other is not null){
                string originalId=editingModelId!;list.SelectedItem=other;await FlushUiUpdatesAsync();
                if(!ReferenceEquals(list.SelectedItem,other)||!modelEdits.ContainsKey(originalId))throw new InvalidOperationException("模型切换未保留原草稿");
                var originalItem=list.Items.OfType<ListViewItem>().First(i=>itemKeys[i]==originalId);list.SelectedItem=originalItem;await FlushUiUpdatesAsync();
                if(displayEditor?.Text!=old+" · 自检")throw new InvalidOperationException("返回模型后未恢复原文草稿");
            }
            dirty=false;
        };
        verifyModelVisibilityBulk=async()=>{
            string? before=displayEditor?.Text;search.Text="hidden-fixture";await Task.Delay(50);BulkVisibility("hide");await Task.Delay(50);
            if(displayEditor?.Text!=before||visibilityChanges.ContainsKey("hidden-fixture"))throw new InvalidOperationException("按筛选批量隐藏丢失编辑内容或未撤销原草稿");
            search.Text="";await Task.Delay(50);BulkVisibility("list");await Task.Delay(50);
            var composed=planFactory!();if(composed.Errors.Count>0||visibilityChanges.Count!=s.Models.Count(m=>m.Visibility!="list"))throw new InvalidOperationException("批量显示与原状态差异不符");
            var visibility=Descendants<ComboBox>(edit).First(c=>c.Header?.ToString()=="Codex 列表显示");if(visibility.SelectedIndex!=0)throw new InvalidOperationException("批量显示后当前编辑器状态未同步");
        };
        verifyModelLabels=async()=>{
            if(labelEditor is null||displayEditor is null)throw new InvalidOperationException("标签编辑器未创建。");
            string original=displayEditor.Text;string originalId=editingModelId!;
            labelEditor.Text="我的订阅";
            if(displayEditor.Text!=ModelDisplayLabel.WithLabel(original,"我的订阅"))throw new InvalidOperationException("标签与显示名称没有同步。");
            var planned=planFactory!();
            if(planned.Errors.Count>0||planned.Files.Count!=1||planned.Files[0].Path!=s.CatalogPath)throw new InvalidOperationException("仅标签修改触及了路由或其他文件："+string.Join("；",planned.Errors));
            var other=list.Items.OfType<ListViewItem>().FirstOrDefault(i=>itemKeys[i]!=originalId);
            if(other is not null){
                list.SelectedItem=other;await FlushUiUpdatesAsync();
                list.SelectedItem=list.Items.OfType<ListViewItem>().First(i=>itemKeys[i]==originalId);await FlushUiUpdatesAsync();
                if(labelEditor?.Text!="我的订阅")throw new InvalidOperationException("标签切换草稿没有恢复。");
            }
            Navigate("overview");await FlushUiUpdatesAsync();Navigate("models");await FlushUiUpdatesAsync();
            if(labelEditor?.Text!="我的订阅")throw new InvalidOperationException("标签跨页草稿没有恢复。");
            labelEditor!.Text="";
            if(displayEditor!.Text!=ModelDisplayLabel.Split(original).Name)throw new InvalidOperationException("清空标签未移除中括号。");
            displayEditor.Text="自定义名称 [同步检查]";
            if(labelEditor.Text!="同步检查")throw new InvalidOperationException("完整名称修改未同步标签。");
            displayEditor.Text=original;
            if(modelEdits.ContainsKey(originalId)||dirty)throw new InvalidOperationException("恢复原标签仍有假草稿。");
        };
    }
}
