using CodexControlCenter.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private void InitializeQuickNavigation()
    {
        var shortcut=new Microsoft.UI.Xaml.Input.KeyboardAccelerator{Key=VirtualKey.K,Modifiers=VirtualKeyModifiers.Control};
        shortcut.Invoked+=async(_,args)=>{args.Handled=true;await ShowQuickNavigationAsync();};Root.KeyboardAccelerators.Add(shortcut);
        AutomationProperties.SetName(QuickSearchButton,"快速查找页面、模型与接入来源，Ctrl+K");
    }
    private async void OnQuickSearch(object sender,RoutedEventArgs e)=>await ShowQuickNavigationAsync();
    private async Task ShowQuickNavigationAsync()
    {
        if(busy||dialogActive||snapshot is null)return;
        try{
            var panel=Section();panel.Width=Math.Clamp(Root.ActualWidth-160,280,600);
            var query=new TextBox{PlaceholderText="输入页面、模型或接入名称",FontSize=bodySize,MinHeight=52};
            AutomationProperties.SetName(query,"查找页面、模型或接入来源");panel.Children.Add(query);
            var count=Note("");panel.Children.Add(count);
            var list=new ListView{Height=370,SelectionMode=ListViewSelectionMode.Single,HorizontalContentAlignment=HorizontalAlignment.Stretch,
                ItemContainerTransitions=new Microsoft.UI.Xaml.Media.Animation.TransitionCollection()};
            list.ItemsPanel=(ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel/></ItemsPanelTemplate>");
            AutomationProperties.SetName(list,"快速查找结果");panel.Children.Add(list);
            var dialog=new ContentDialog{Title="快速查找",Content=panel,PrimaryButtonText="打开所选项",CloseButtonText="取消",DefaultButton=ContentDialogButton.None,XamlRoot=Root.XamlRoot};
            dialog.Resources["ContentDialogMaxWidth"]=720.0;
            void Update(){
                ClearControlItems(list);
                var found=QuickNavigation.Search(snapshot,query.Text);
                foreach(var target in found){var text=Section();text.Spacing=5;text.Children.Add(Text(target.Title,20));text.Children.Add(Note(target.Description));
                    list.Items.Add(new ListViewItem{Tag=target,Content=text,Padding=new Thickness(12,12,12,12)});}
                list.SelectedIndex=list.Items.Count>0?0:-1;dialog.IsPrimaryButtonEnabled=list.SelectedIndex>=0;
                count.Text=found.Count==0?"没有匹配结果。试试模型编号、来源或页面名称。":$"{found.Count} 个结果 · Enter 打开 · Esc 关闭";
            }
            query.TextChanged+=(_,_)=>Update();Update();
            query.KeyDown+=(_,args)=>{
                if((args.Key is VirtualKey.Down or VirtualKey.Up)&&list.Items.Count>0){
                    list.SelectedIndex=Math.Clamp(list.SelectedIndex+(args.Key==VirtualKey.Down?1:-1),0,list.Items.Count-1);list.ScrollIntoView(list.SelectedItem);args.Handled=true;
                }
            };
            query.Loaded+=(_,_)=>query.Focus(FocusState.Programmatic);
            NavigationTarget? selected=null;
            dialog.PrimaryButtonClick+=(_,_)=>selected=(list.SelectedItem as ListViewItem)?.Tag as NavigationTarget;
            list.DoubleTapped+=(_,_)=>{selected=(list.SelectedItem as ListViewItem)?.Tag as NavigationTarget;if(selected is not null)dialog.Hide();};
            var enter=new Microsoft.UI.Xaml.Input.KeyboardAccelerator{Key=VirtualKey.Enter};
            enter.Invoked+=(_,args)=>{args.Handled=true;selected=(list.SelectedItem as ListViewItem)?.Tag as NavigationTarget;if(selected is not null)dialog.Hide();};
            panel.KeyboardAccelerators.Add(enter);
            await ShowDialogAsync(dialog);
            if(selected is not null)await OpenNavigationTargetAsync(selected);
        }catch(Exception ex){ShowError("快速查找暂不可用",ex);}
    }
    private async Task OpenNavigationTargetAsync(NavigationTarget target)
    {
        if(busy||dialogActive)return;
        Navigate(target.Page);await FlushUiUpdatesAsync();
        if(page!=target.Page||target.ItemId is null)return;
        if(page=="models"){
            var search=Descendants<TextBox>(PageBody).FirstOrDefault(t=>t.Header?.ToString()=="搜索模型");
            var filters=Descendants<ComboBox>(PageBody);
            foreach(var filter in filters.Where(c=>c.Header?.ToString() is "按显示状态筛选" or "按接入来源筛选"))filter.SelectedIndex=0;
            if(search is not null)search.Text=target.ItemId;await FlushUiUpdatesAsync();
            var list=Descendants<ListView>(PageBody).FirstOrDefault();
            if(list?.Items.OfType<ListViewItem>().FirstOrDefault(i=>(i.Tag as ModelEntry)?.Id==target.ItemId) is {} item){list.SelectedItem=item;list.ScrollIntoView(item);await FlushUiUpdatesAsync();}
        }else if(page=="providers"){
            var choice=Descendants<ComboBox>(PageBody).FirstOrDefault(c=>c.Header?.ToString()=="选择接入来源");
            var value=choice?.Items.OfType<string>().FirstOrDefault(label=>label.EndsWith("["+target.ItemId+"]",StringComparison.Ordinal));
            if(value is not null)choice!.SelectedItem=value;
        }
    }
}
