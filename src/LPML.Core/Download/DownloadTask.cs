using LPML.Core.Utils;

namespace LPML.Core.Download;

/// <summary>任务阶段类型：下载任务有"下载"阶段，安装加载器等有"安装"阶段。</summary>
public enum TaskStageKind
{
    Download,
    Install,
    Extract,
    Verify
}

public enum TaskStepStatus
{
    Pending,
    Running,
    Done,
    Failed
}

/// <summary>安装阶段里的一步（如"安装 Fabric 加载器"）。</summary>
public sealed class TaskStep : ObservableObject
{
    private TaskStepStatus _status = TaskStepStatus.Pending;
    private string _detail = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public TaskStepStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) OnPropertyChanged(nameof(StatusText)); }
    }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string StatusText => Status switch
    {
        TaskStepStatus.Running => L.T("String.Core.Task.Running"),
        TaskStepStatus.Done => L.T("String.Core.Task.Done"),
        TaskStepStatus.Failed => L.T("String.Core.Task.Failed"),
        _ => L.T("String.Core.Task.Pending")
    };
}

/// <summary>
/// 通用任务组：一次"下载整合包"或一次"安装游戏"是一个任务，
/// 点进去可以看到它内部的下载条目和安装步骤。
/// </summary>
public sealed class DownloadTask : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Kind { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Items / Steps 的同步锁。
    /// 这两个集合由下载线程写入（Enqueue / AddStep），又被界面线程读取，
    /// 不加锁的话会出现"读到一半被改"，WPF 还会因为集合在后台被改而抛
    /// "ItemsControl 与它的项源不一致"。
    /// </summary>
    public object Sync { get; } = new();

    /// <summary>下载阶段的条目。不要直接改，用 AddItem / SnapshotItems。</summary>
    public List<DownloadItem> Items { get; } = new();

    /// <summary>安装阶段的步骤（预留：加载器安装、解压、校验等）。</summary>
    public List<TaskStep> Steps { get; } = new();

    public void AddItem(DownloadItem item) { lock (Sync) Items.Add(item); }

    public void AddStep(TaskStep step) { lock (Sync) Steps.Add(step); }

    /// <summary>取当前条目的快照，供界面线程安全遍历。</summary>
    public List<DownloadItem> SnapshotItems() { lock (Sync) return Items.ToList(); }

    /// <summary>取当前步骤的快照，供界面线程安全遍历。</summary>
    public List<TaskStep> SnapshotSteps() { lock (Sync) return Steps.ToList(); }

    private bool _finished;
    public bool Finished
    {
        get => _finished;
        set { if (Set(ref _finished, value)) OnPropertyChanged(nameof(Summary)); }
    }

    public double Progress
    {
        get
        {
            lock (Sync)
            {
                // 按文件数算，理由同 DownloadAggregate.Progress：
                // 用字节当分母时，大小未知的文件会把分母压得很小，进度会虚高到 80%~100%。
                if (Items.Count == 0) return 0;
                var done = Items.Count(i => i.Status is DownloadStatus.Completed or DownloadStatus.Skipped);
                var active = Items.Where(i => i.IsActive).Sum(i => i.Progress);
                return Math.Clamp((done + active) / (double)Items.Count, 0, 1);
            }
        }
    }

    /// <summary>还没拿到文件清单（正在列文件）。</summary>
    private bool _preparing;
    public bool Preparing { get => _preparing; set { if (Set(ref _preparing, value)) OnPropertyChanged(nameof(Summary)); } }

    public string Summary
    {
        get
        {
            lock (Sync)
            {
                if (Preparing && Items.Count == 0) return L.T("String.Core.Task.Creating");
                var done = Items.Count(i => i.Status is DownloadStatus.Completed or DownloadStatus.Skipped);
                var active = Items.Count(i => i.IsActive);
                if (active > 0) return L.F("String.Core.Task.Downloading", done, Items.Count);
                if (Finished) return L.T("String.Core.Task.Done");
                return L.F("String.Core.Task.Waiting", done, Items.Count);
            }
        }
    }

    /// <summary>
    /// Summary / Progress 是计算属性：下载线程改的是文件状态，不会自动通知到
    /// 绑在任务对象上的一级卡片。界面线程每次刷新时统一触发一次即可。
    /// </summary>
    public void NotifyComputed()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(Finished));
    }
}
