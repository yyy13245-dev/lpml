using LPML.Core.Utils;
using LPML.Core.Config;

namespace LPML.Core.Download;

public enum DownloadStatus
{
    Pending,
    Preparing,
    Downloading,
    Merging,
    Verifying,
    Completed,
    Skipped,
    Failed,
    Canceled,
    Paused
}

/// <summary>
/// 任务详情里「下载」页的两个大类。
/// 一次整合包安装会同时下整合包自己的文件（mrpack / 模组）和原版游戏文件
/// （版本 JSON / 客户端 jar / 库 / 资源），全平铺在一个列表里会有几千行，
/// 所以先按这个分区汇总，点进去再看单文件。
/// 与细粒度的 Group（游戏本体 / 依赖库 / 资源文件…）互不冲突。
/// </summary>
public static class DownloadSections
{
    /// <summary>整合包内容：mrpack 本体、模组、overrides。</summary>
    public const string PackContent = "pack";

    /// <summary>原版内容：版本 JSON、客户端 jar、依赖库、资源。</summary>
    public const string VanillaContent = "vanilla";

    /// <summary>不属于上面两类的零散下载（Java 运行时、单独下的模组等）。</summary>
    public const string General = "general";
}

/// <summary>
/// 单个下载项。界面上的L.T("String.Core.Status.MultiProgress")就是绑定一组 DownloadItem。
/// </summary>
public sealed class DownloadItem : ObservableObject
{
    private long _bytesReceived;
    private long _totalBytes;
    private DownloadStatus _status = DownloadStatus.Pending;
    private string _statusText = L.T("String.Core.Status.Waiting");
    private double _speed;
    private int _chunks;
    private string? _error;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>
    /// 镜像改写前的官方地址。镜像缺文件（403/404）时用它换源重试，
    /// 否则只能对着同一个镜像反复重试，永远失败。
    /// </summary>
    public string OriginalUrl { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? Sha1 { get; set; }
    public long? ExpectedSize { get; set; }
    public ResourceCategory Category { get; set; } = ResourceCategory.Libraries;
    public string Group { get; set; } = L.T("String.Core.Status.DefaultGroup");

    /// <summary>任务详情页的大分区，见 <see cref="DownloadSections"/>。</summary>
    public string Section { get; set; } = DownloadSections.General;

    public int RetryUsed { get; set; }

    /// <summary>
    /// 这个文件属于哪一次安装任务。
    /// 一次安装会分好几批下载（整合包本体、原版 JSON/库、模组……），
    /// 每批都显式绑定到自己的任务，避免并发安装时"后开的任务把前面批次抢走"。
    /// </summary>
    public DownloadTask? Owner { get; set; }

    public long BytesReceived
    {
        get => _bytesReceived;
        set { if (Set(ref _bytesReceived, value)) { OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); } }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set { if (Set(ref _totalBytes, value)) { OnPropertyChanged(nameof(Progress)); OnPropertyChanged(nameof(ProgressText)); } }
    }

    public DownloadStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) OnPropertyChanged(nameof(IsActive)); }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public double Speed
    {
        get => _speed;
        set { if (Set(ref _speed, value)) OnPropertyChanged(nameof(SpeedText)); }
    }

    public int Chunks
    {
        get => _chunks;
        set => Set(ref _chunks, value);
    }

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    public bool IsActive => Status is DownloadStatus.Downloading or DownloadStatus.Preparing or DownloadStatus.Merging or DownloadStatus.Verifying;

    public double Progress => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived / (double)TotalBytes, 0, 1);

    public string ProgressText => $"{Progress * 100:0.0}%  ({FileHelper.HumanSize(BytesReceived)} / {FileHelper.HumanSize(TotalBytes)})";

    public string SpeedText => FileHelper.HumanSpeed(Speed);

    public CancellationTokenSource Cancellation { get; } = new();

    public void Cancel() => Cancellation.Cancel();

    /// <summary>重置为可重新下载的状态：清掉失败/取消标记、进度与已收字节。</summary>
    public void Reset()
    {
        BytesReceived = 0;
        TotalBytes = 0;
        Speed = 0;
        Chunks = 0;
        RetryUsed = 0;
        Error = null;
        Status = DownloadStatus.Pending;
        StatusText = L.T("String.Core.Status.Queued");
    }
}

/// <summary>整体进度快照，用于界面顶部汇总条。</summary>
public sealed class DownloadAggregate
{
    public int Total { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Active { get; set; }
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public TimeSpan Elapsed { get; set; }

    /// <summary>正在下载的那几个文件自身进度的总和（0~活跃数）。</summary>
    public double ActiveProgress { get; set; }

    /// <summary>
    /// 整体进度按"文件数"算，而不是按字节。
    /// 库文件动辄几千个、大小又各不相同，很多文件在开始下载前并不知道体积，
    /// 用字节做分母会出现"7564 个文件只下了 200 个却显示 80%"这种虚高。
    /// 正在下载的文件按它自己的进度计入，所以下载单个大文件时进度条也会平滑推进。
    /// 字节数只用来算速度和剩余时间。
    /// </summary>
    public double Progress => Total == 0
        ? 0
        : Math.Clamp((Completed + ActiveProgress) / (double)Total, 0, 1);

    public TimeSpan Remaining
    {
        get
        {
            if (BytesPerSecond <= 0) return TimeSpan.Zero;
            var left = TotalBytes - ReceivedBytes;
            if (left <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(left / BytesPerSecond);
        }
    }

    /// <summary>速度还没测出来时（起步或全部暂停）不显示"剩余 00:00"，避免读数乱跳。</summary>
    public bool HasSpeed => BytesPerSecond > 0 && ReceivedBytes < TotalBytes;

    public string Summary => HasSpeed
        ? L.F("String.Core.Status.Aggregate", Completed, Total, FileHelper.HumanSize(ReceivedBytes), FileHelper.HumanSize(TotalBytes), FileHelper.HumanSpeed(BytesPerSecond), FileHelper.FormatTime(Remaining))
        : L.F("String.Core.Status.AggregateNoSpeed", Completed, Total, FileHelper.HumanSize(ReceivedBytes), FileHelper.HumanSize(TotalBytes));
}
