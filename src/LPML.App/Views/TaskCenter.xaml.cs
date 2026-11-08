using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.App.Views;

/// <summary>
/// 按任务类型挑卡片：补全文件用自己的「补全文件任务卡」，
/// 安装游戏 / 整合包 / 模组等继续沿用原来那张卡，一个字节都不改。
/// </summary>
public sealed class TaskCardTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        var key = item is DownloadTask { Kind: "repair" } ? "RepairTaskCard" : "GameTaskCard";
        return (container as FrameworkElement)?.TryFindResource(key) as DataTemplate;
    }
}

/// <summary>
/// 任务中心（三级）：
/// 一级是任务组列表（一次下载 / 一次安装 = 一个任务）；
/// 二级「下载」按大类汇总（整合包内容 / 原版内容），每类一个独立进度条；
/// 三级点进分类看每个文件的实时下载进度；「安装」点进去是步骤流程图。
/// </summary>
public partial class TaskCenter : UserControl
{
    public LauncherContext Context => App.Context;

    private readonly ObservableCollection<DownloadTask> _tasks = new();
    // Core 里的 Items / Steps 是普通 List，由下载线程写入，直接绑给 ItemsControl
    // 会因为"集合被改但没通知"而抛 "ItemsControl 与它的项源不一致"。
    // 这里统一用 ObservableCollection 做镜像，在界面线程增量同步。
    private readonly ObservableCollection<DownloadItem> _items = new();
    private readonly ObservableCollection<DownloadGroupView> _groups = new();
    private readonly ObservableCollection<FlowStepView> _flow = new();

    private DownloadTask? _opened;
    private string? _openedGroupKey;
    private readonly DispatcherTimer _timer;
    private bool _dirty;
    private bool _ready;
    private bool _switching;

    public TaskCenter()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => { if (_dirty) { _dirty = false; ApplyRefresh(); } };
        _timer.Start();

        InitializeComponent();
        _ready = true;
        TaskList.ItemsSource = _tasks;
        GroupList.ItemsSource = _groups;
        ItemList.ItemsSource = _items;
        StepList.ItemsSource = _flow;
        Loaded += (_, _) =>
        {
            Context.Downloads.ItemChanged += OnDownloadEvent;
            Context.Downloads.ProgressChanged += OnDownloadEvent;
            Context.Downloads.Finished += OnDownloadEvent;
            ApplyRefresh();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            Context.Downloads.ItemChanged -= OnDownloadEvent;
            Context.Downloads.ProgressChanged -= OnDownloadEvent;
            Context.Downloads.Finished -= OnDownloadEvent;
        };
    }

    private void OnDownloadEvent(object? sender, object e) => Refresh();

    public void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Refresh); return; }
        _dirty = true;
    }

    private void ApplyRefresh()
    {
        var tasks = Context.Downloads.Tasks;
        for (var i = _tasks.Count - 1; i >= 0; i--)
            if (!tasks.Any(t => t.Id == _tasks[i].Id)) _tasks.RemoveAt(i);
        foreach (var task in tasks)
            if (!_tasks.Any(t => t.Id == task.Id)) _tasks.Add(task);

        if (_opened is null) return;
        if (DetailTitle is null || ItemList is null || StepList is null || GroupList is null) return;

        DetailTitle.Text = _opened.Title;

        var allItems = _opened.SnapshotItems();
        var allSteps = _opened.SnapshotSteps();

        // ── 下载：按大类汇总 ──
        SyncGroups(allItems);
        // 非空态走 DynamicResource，界面语言切换时提示会跟着变
        if (_groups.Count == 0)
            GroupPickHint.Text = Lang.T("String.Task.GroupEmpty");
        else
            GroupPickHint.SetResourceReference(TextBlock.TextProperty, "String.Task.GroupPickHint");

        // 打开中的分类：只把这一类的文件同步进镜像
        if (_openedGroupKey is not null)
        {
            var group = _groups.FirstOrDefault(g => g.Key == _openedGroupKey);
            if (group is not null)
            {
                SyncMirror(_items, group.Items);
                ItemViewTitle.Text = group.Title;
                ItemViewCount.Text = Lang.F("String.Task.FileCount", group.Items.Count);
                ItemViewBar.Value = group.Progress;
            }
            else
            {
                // 分类里已经没有文件了（任务被清空），退回分类首页
                BackToGroups_OnClick(this, new RoutedEventArgs());
            }
        }

        // ── 总进度 ──
        if (DetailBar is not null) DetailBar.Value = _opened.Progress;
        if (DetailPercent is not null) DetailPercent.Text = $"{_opened.Progress * 100:0}%";
        if (DetailSummary is not null) DetailSummary.Text = _opened.Summary;

        // ── 安装：流程图 ──
        SyncFlow(allSteps);
        var doneCount = allSteps.Count(s => s.Status == TaskStepStatus.Done);
        if (FlowEntryCount is not null)
            FlowEntryCount.Text = allSteps.Count == 0
                ? ""
                : Lang.F("String.Task.FlowProgress", doneCount, allSteps.Count);
        if (FlowEntryBar is not null)
            FlowEntryBar.Value = allSteps.Count == 0 ? 0 : doneCount / (double)allSteps.Count;
        if (FlowEntryStatus is not null)
            FlowEntryStatus.Text = allSteps.Count == 0
                ? Lang.T("String.Task.FlowEmpty")
                : Lang.F("String.Task.FlowProgress", doneCount, allSteps.Count);

        // 有失败/取消的文件才显示"继续下载未完成文件"
        if (ResumeIncompleteButton is not null)
            ResumeIncompleteButton.Visibility = Context.Downloads.HasIncomplete && !Context.Downloads.IsRunning
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 把任务里的文件按 Section 聚合成两三个大类。只显示实际有文件的分类，
    /// 顺序固定为 整合包内容 → 原版内容 → 其他文件。
    /// </summary>
    private void SyncGroups(List<DownloadItem> allItems)
    {
        var order = new[]
        {
            DownloadSections.PackContent,
            DownloadSections.VanillaContent,
            DownloadSections.General
        };

        var buckets = new List<DownloadGroupView>();
        foreach (var key in order)
        {
            var files = allItems.Where(i => SectionOf(i) == key).ToList();
            if (files.Count == 0) continue;
            buckets.Add(BuildGroup(key, files));
        }

        // 按 Key 复用视图对象，避免每 400ms 重建导致列表闪烁 / 选中态丢失
        for (var i = _groups.Count - 1; i >= 0; i--)
            if (!buckets.Any(b => b.Key == _groups[i].Key)) _groups.RemoveAt(i);

        foreach (var bucket in buckets)
        {
            var target = _groups.FirstOrDefault(g => g.Key == bucket.Key);
            if (target is null) _groups.Add(bucket);
            else target.CopyFrom(bucket);
        }

        // 保持固定顺序（新增时可能插在末尾）
        for (var i = 0; i < buckets.Count; i++)
        {
            var want = buckets[i].Key;
            var at = -1;
            for (var j = 0; j < _groups.Count; j++)
                if (_groups[j].Key == want) { at = j; break; }
            if (at >= 0 && at != i) _groups.Move(at, i);
        }
    }

    private static string SectionOf(DownloadItem item)
        => string.IsNullOrWhiteSpace(item.Section) ? DownloadSections.General : item.Section;

    private static DownloadGroupView BuildGroup(string key, List<DownloadItem> files)
    {
        var done = files.Count(i => i.Status is DownloadStatus.Completed or DownloadStatus.Skipped);
        var active = files.Count(i => i.IsActive);
        var activeProgress = files.Where(i => i.IsActive).Sum(i => i.Progress);
        var failed = files.Count(i => i.Status is DownloadStatus.Failed or DownloadStatus.Canceled);

        var (icon, title, hint) = key switch
        {
            DownloadSections.PackContent => ("📦",
                Lang.T("String.Task.GroupPack"), Lang.T("String.Task.GroupPackHint")),
            DownloadSections.VanillaContent => ("🎮",
                Lang.T("String.Task.GroupVanilla"), Lang.T("String.Task.GroupVanillaHint")),
            _ => ("📄",
                Lang.T("String.Task.GroupGeneral"), Lang.T("String.Task.GroupGeneralHint"))
        };

        var view = new DownloadGroupView { Key = key };
        view.CopyFrom(new DownloadGroupView
        {
            Key = key,
            Icon = icon,
            Title = title,
            Hint = hint,
            Items = files,
            Total = files.Count,
            Completed = done,
            Active = active,
            Failed = failed,
            Progress = files.Count == 0 ? 0 : Math.Clamp((done + activeProgress) / (double)files.Count, 0, 1),
            CountText = Lang.F("String.Task.GroupCount", done, files.Count),
            StatusText = Lang.F("String.Task.GroupStatus", done, files.Count, active)
        });
        return view;
    }

    /// <summary>把 TaskStep 包成流程图节点（带序号、连线、状态图标）。</summary>
    private void SyncFlow(List<TaskStep> steps)
    {
        while (_flow.Count > steps.Count) _flow.RemoveAt(_flow.Count - 1);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var index = i + 1;
            var state = step.Status.ToString();
            var glyph = step.Status switch
            {
                TaskStepStatus.Done => "✓",
                TaskStepStatus.Running => "●",
                TaskStepStatus.Failed => "!",
                _ => index.ToString()
            };

            var view = i < _flow.Count ? _flow[i] : null;
            if (view is null) { view = new FlowStepView(); _flow.Add(view); }

            view.Index = index;
            view.IsLast = i == steps.Count - 1;
            view.StepName = step.Name;
            view.Detail = step.Detail;
            view.StatusText = step.StatusText;
            view.StateKey = state;
            view.Glyph = glyph;
            view.IndexText = Lang.F("String.Task.StepIndex", index);
            view.NotifyAll();
        }
    }

    /// <summary>
    /// 把 source 增量同步进 target：删掉已不在的、补上新增的，尽量保持顺序。
    /// 全程在界面线程跑，ObservableCollection 才能正确发出 CollectionChanged。
    /// </summary>
    private static void SyncMirror<T>(ObservableCollection<T> target, List<T> source) where T : class
    {
        for (var i = target.Count - 1; i >= 0; i--)
            if (!source.Any(x => ReferenceEquals(x, target[i]))) target.RemoveAt(i);

        var index = 0;
        foreach (var item in source)
        {
            if (index < target.Count && ReferenceEquals(target[index], item)) { index++; continue; }
            if (index < target.Count) target[index] = item;
            else target.Add(item);
            index++;
        }

        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }

    // ── 视图切换 ───────────────────────────────────────────

    private void ResetDetailViews()
    {
        _openedGroupKey = null;
        _switching = true;
        if (GroupList is not null) GroupList.SelectedItem = null;
        _switching = false;
        _items.Clear();

        if (GroupView is not null) GroupView.Visibility = Visibility.Visible;
        if (ItemView is not null) ItemView.Visibility = Visibility.Collapsed;
        if (FlowEntryView is not null) FlowEntryView.Visibility = Visibility.Visible;
        if (FlowView is not null) FlowView.Visibility = Visibility.Collapsed;
    }

    private void TaskList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || TaskList is null || HeaderList is null || HeaderDetail is null || DetailTabs is null || DetailTitle is null) return;
        if (TaskList.SelectedItem is not DownloadTask task) return;
        _opened = task;
        ResetDetailViews();
        HeaderList.Visibility = Visibility.Collapsed;
        HeaderDetail.Visibility = Visibility.Visible;
        TaskList.Visibility = Visibility.Collapsed;
        DetailTabs.Visibility = Visibility.Visible;
        ApplyRefresh();
    }

    private void GroupList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switching || !_ready) return;
        if (GroupList.SelectedItem is not DownloadGroupView group) return;
        _openedGroupKey = group.Key;
        GroupView.Visibility = Visibility.Collapsed;
        ItemView.Visibility = Visibility.Visible;
        ApplyRefresh();
    }

    private void BackToGroups_OnClick(object sender, RoutedEventArgs e)
    {
        _openedGroupKey = null;
        _switching = true;
        GroupList.SelectedItem = null;
        _switching = false;
        _items.Clear();
        ItemView.Visibility = Visibility.Collapsed;
        GroupView.Visibility = Visibility.Visible;
    }

    private void FlowEntry_OnClick(object sender, MouseButtonEventArgs e)
    {
        FlowEntryView.Visibility = Visibility.Collapsed;
        FlowView.Visibility = Visibility.Visible;
    }

    private void BackToFlowEntry_OnClick(object sender, RoutedEventArgs e)
    {
        FlowView.Visibility = Visibility.Collapsed;
        FlowEntryView.Visibility = Visibility.Visible;
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
    {
        _opened = null;
        ResetDetailViews();
        TaskList.SelectedItem = null;
        HeaderList.Visibility = Visibility.Visible;
        HeaderDetail.Visibility = Visibility.Collapsed;
        TaskList.Visibility = Visibility.Visible;
        DetailTabs.Visibility = Visibility.Collapsed;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.HideTaskCenter();

    private void Pause_OnClick(object sender, RoutedEventArgs e) { Context.Downloads.Pause(); Refresh(); }

    private void Resume_OnClick(object sender, RoutedEventArgs e) => _ = ResumeAsync();

    private async Task ResumeAsync()
    {
        try
        {
            await Context.Downloads.ResumeIncompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Task", "Resume failed", ex);
        }
        finally
        {
            Refresh();
        }
    }

    private void ResumeIncomplete_OnClick(object sender, RoutedEventArgs e) => _ = ResumeAsync();

    private void Cancel_OnClick(object sender, RoutedEventArgs e) { Context.Downloads.CancelAll(); Refresh(); }

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        Context.Downloads.ClearFinishedTasks();
        // 还有文件在下载时不要清空队列，否则会把别的任务正在跑的项一起抹掉
        if (!Context.Downloads.Items.Any(i => i.IsActive))
            Context.Downloads.Clear();
        Back_OnClick(sender, e);
        Refresh();
    }

    // ── 右键菜单（任务组级）────────────────────────────────

    private CurrentTaskTarget? GetClickedTask(RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menu) return null;
        if (menu.Parent is not ContextMenu cm) return null;
        if (cm.PlacementTarget is not ListBox list) return null;
        return new CurrentTaskTarget(list, list.SelectedItem as DownloadTask);
    }

    private sealed record CurrentTaskTarget(ListBox List, DownloadTask? Task);

    private void TaskRetry_OnClick(object sender, RoutedEventArgs e)
    {
        var hit = GetClickedTask(e);
        if (hit?.Task is null) return;
        var failed = hit.Task.SnapshotItems().Where(IsRetryable).ToList();
        if (failed.Count == 0) return;
        _ = RunItemsAsync(failed, false);
    }

    private void TaskRedownload_OnClick(object sender, RoutedEventArgs e)
    {
        var hit = GetClickedTask(e);
        if (hit?.Task is null) return;
        var all = hit.Task.SnapshotItems();
        if (all.Count == 0) return;
        _ = RunItemsAsync(all, true);
    }

    private void TaskCancel_OnClick(object sender, RoutedEventArgs e)
    {
        var hit = GetClickedTask(e);
        if (hit?.Task is null) return;
        Context.Downloads.CancelTask(hit.Task);
        Refresh();
    }

    // ── 右键菜单（单个文件级）──────────────────────────────

    private void ItemList_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ItemList.SelectedItem is null) ItemList.SelectedIndex = 0;
    }

    private DownloadItem? GetClickedItem(RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menu) return null;
        if (menu.Parent is not ContextMenu cm) return null;
        if (cm.PlacementTarget is not ListBox list) return null;
        return list.SelectedItem as DownloadItem;
    }

    private void ItemRetry_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetClickedItem(e) is not { } item) return;
        if (!IsRetryable(item)) return;
        _ = RunItemsAsync(new[] { item }, false);
    }

    private void ItemRedownload_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetClickedItem(e) is not { } item) return;
        _ = RunItemsAsync(new[] { item }, true);
    }

    private void ItemCancel_OnClick(object sender, RoutedEventArgs e)
    {
        if (GetClickedItem(e) is not { } item) return;
        Context.Downloads.CancelItem(item);
        Refresh();
    }

    private static bool IsRetryable(DownloadItem item)
        => item.Status is DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Pending;

    private async Task RunItemsAsync(IEnumerable<DownloadItem> items, bool redownload)
    {
        try
        {
            if (redownload) await Context.Downloads.RedownloadItemsAsync(items);
            else await Context.Downloads.RunItemsAsync(items);
        }
        catch (Exception ex)
        {
            Log.Error("Task", Lang.T("String.Task.ActionFailed"), ex);
        }
        finally
        {
            Refresh();
        }
    }
}

/// <summary>下载页第一级的一张分类卡片（纯展示数据，界面线程持有）。</summary>
public sealed class DownloadGroupView : ObservableObject
{
    public string Key { get; set; } = "";
    public string Icon { get; set; } = "📄";
    public string Title { get; set; } = "";
    public string Hint { get; set; } = "";
    public string CountText { get; set; } = "";
    public string StatusText { get; set; } = "";
    public double Progress { get; set; }
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Active { get; set; }
    public int Failed { get; set; }
    public List<DownloadItem> Items { get; set; } = new();

    public void CopyFrom(DownloadGroupView other)
    {
        Icon = other.Icon;
        Title = other.Title;
        Hint = other.Hint;
        CountText = other.CountText;
        StatusText = other.StatusText;
        Progress = other.Progress;
        Total = other.Total;
        Completed = other.Completed;
        Active = other.Active;
        Failed = other.Failed;
        Items = other.Items;
        NotifyAll();
    }

    public void NotifyAll() => Raise(
        nameof(Icon), nameof(Title), nameof(Hint), nameof(CountText), nameof(StatusText),
        nameof(Progress), nameof(Total), nameof(Completed), nameof(Active), nameof(Failed),
        nameof(Items), nameof(Key));
}

/// <summary>安装流程图里的一个节点。</summary>
public sealed class FlowStepView : ObservableObject
{
    public int Index { get; set; }
    public bool IsLast { get; set; }
    public string StepName { get; set; } = "";
    public string Detail { get; set; } = "";
    public string StatusText { get; set; } = "";
    public string StateKey { get; set; } = "Pending";
    public string Glyph { get; set; } = "1";
    public string IndexText { get; set; } = "";

    // XAML 里绑定的是 Name，避开 UserControl 自身已有的 Name 依赖属性歧义
    public string Name => StepName;

    public Visibility LineVisibility => IsLast ? Visibility.Collapsed : Visibility.Visible;

    public void NotifyAll() => Raise(
        nameof(Index), nameof(IsLast), nameof(StepName), nameof(Name), nameof(Detail),
        nameof(StatusText), nameof(StateKey), nameof(Glyph), nameof(IndexText),
        nameof(LineVisibility));
}
