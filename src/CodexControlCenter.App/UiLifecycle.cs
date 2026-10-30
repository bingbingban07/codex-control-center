using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexControlCenter.App;
public sealed partial class MainWindow
{
    private object pageInstance=new();
    private long uiUpdateSequence;
    private readonly Dictionary<string,long> queuedUiUpdates=[];
    private readonly Queue<(long At,object[] Objects)> retiredVisuals=new();
    private DispatcherQueueTimer? retirementTimer;
    private StackPanel PageBody=new(){Spacing=22,Padding=new Thickness(0,0,6,16)};
    private readonly Queue<(long At,StackPanel Body)> retiredPageBodies=new();
    private void EnsurePageBodyAttached()
    {
        if(!PageBodies.Children.Contains(PageBody))PageBodies.Children.Add(PageBody);
    }
    private void RetirePageBody(StackPanel body)
    {
        body.Visibility=Visibility.Collapsed;
        if(!drafts.Values.Any(d=>ReferenceEquals(d.Body,body)))retiredPageBodies.Enqueue((Environment.TickCount64,body));
        RetireVisuals([body]);
    }
    private void BeginPageBody()
    {
        var previous=PageBody;PageBody=new(){Spacing=22,Padding=new Thickness(0,0,6,16)};EnsurePageBodyAttached();RetirePageBody(previous);
        foreach(var body in PageBodies.Children.OfType<StackPanel>().ToArray())
            if(!ReferenceEquals(body,PageBody)&&!drafts.Values.Any(d=>ReferenceEquals(d.Body,body))&&!retiredPageBodies.Any(p=>ReferenceEquals(p.Body,body)))RetirePageBody(body);
    }

    // Keep projections rooted through the native removal callback and later layout/unload events.
    // IVector.Clear caused native access violations in both model selection and page navigation.
    private void RetireVisuals(object[] objects)
    {
        if(objects.Length==0)return;
        retiredVisuals.Enqueue((Environment.TickCount64,objects));
        if(retirementTimer is null){
            retirementTimer=DispatcherQueue.CreateTimer();retirementTimer.Interval=TimeSpan.FromMilliseconds(100);
            retirementTimer.Tick+=(_,_)=>{
                while(retiredPageBodies.TryPeek(out var page)&&Environment.TickCount64-page.At>=200){
                    retiredPageBodies.Dequeue();
                    if(!ReferenceEquals(page.Body,PageBody)&&!drafts.Values.Any(d=>ReferenceEquals(d.Body,page.Body))){int at=PageBodies.Children.IndexOf(page.Body);if(at>=0)PageBodies.Children.RemoveAt(at);}
                }
                while(retiredVisuals.TryPeek(out var item)&&Environment.TickCount64-item.At>=400)retiredVisuals.Dequeue();
                if(retiredVisuals.Count==0&&retiredPageBodies.Count==0)retirementTimer.Stop();
            };
        }
        retirementTimer.Start();
    }
    private void ClearPanelChildren(Panel panel)
    {
        var held=panel.Children.ToArray();RetireVisuals(held);
        for(int i=panel.Children.Count-1;i>=0;i--)panel.Children.RemoveAt(i);
        GC.KeepAlive(held);
    }
    private void ClearControlItems(ItemsControl control)
    {
        var held=control.Items.ToArray();RetireVisuals(held);
        for(int i=control.Items.Count-1;i>=0;i--)control.Items.RemoveAt(i);
        GC.KeepAlive(held);
    }
    private void ClearGridDefinitions(Grid grid)
    {
        object[] held=grid.ColumnDefinitions.Cast<object>().Concat(grid.RowDefinitions.Cast<object>()).ToArray();RetireVisuals(held);
        for(int i=grid.ColumnDefinitions.Count-1;i>=0;i--)grid.ColumnDefinitions.RemoveAt(i);
        for(int i=grid.RowDefinitions.Count-1;i>=0;i--)grid.RowDefinitions.RemoveAt(i);
        GC.KeepAlive(held);
    }
    private void QueueUiUpdate(string key,object? owner,Action update)
    {
        long sequence=++uiUpdateSequence;queuedUiUpdates[key]=sequence;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal,()=>{
            if(!queuedUiUpdates.TryGetValue(key,out long latest)||latest!=sequence)return;
            queuedUiUpdates.Remove(key);
            if(lifetime.IsCancellationRequested||owner is not null&&!ReferenceEquals(owner,pageInstance))return;
            try{update();}catch(Exception error){UiCrashLog.Exception("queued-ui.error",error);ShowError("更新界面失败",error);}
        });
    }
    private void CancelUiUpdate(string key)=>queuedUiUpdates.Remove(key);
    private async Task FlushUiUpdatesAsync()
    {
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,()=>completed.TrySetResult()))throw new InvalidOperationException("界面调度队列已关闭。");
        await completed.Task;
    }
}
