using System.ComponentModel;
using System.Threading;
using LPML.Core.Config;
using LPML.Core.Mirror;
using LPML.Core.Utils;

namespace LPML.Core.Download;

/// <summary>
/// 下载调度中心：
/// - 多任务并发（MaxConcurrentTasks）+ 单文件多线程分段（MaxChunksPerFile）
/// - 实时汇总进度（总进度、速度、剩余时间）与逐项进度（多进度条）
/// - 暂停 / 继续 / 取消 / 失败重试
/// </summary>
public sealed class DownloadManager
{
    private readonly HttpService _http;
    private readonly TempFileManager _temp;
    private readonly SpeedLimiter _limiter;
    private readonly ChunkedDownloader _downloader;
    private readonly DownloadSettings _settings;
    private readonly List<DownloadItem> _items = new();
    private readonly LocalLibraryIndex _index;
    private MirrorManager? _mirrors;
    private double _peakBytesPerSecond;
    private readonly SourceHealth _health = new();
    private long _lastThroughputBytes;
    private DateTime _lastThroughputSample = DateTime.MinValue;
    private int _lowThroughputStreak;
    private readonly object _sync = new();
    private CancellationTokenSource _cts = new();
    private bool _paused;
    private bool _running;
    private TaskCompletionSource? _runCompletion;

    /// <summary>本轮实际使用的并发数（会在运行期自适应调整）。</summary>
    /// <summary>基准并发：由文件大小自适应算出（小文件高、大文件低）。</summary>
    private int _baseConcurrency;

    /// <summary>全局连接预算（文件级并发 + 单文件分段共用一份额度）。</summary>
    private readonly StreamBudget _budget;

    /// <summary>失败率微调：在基准上加减（被限流时往下压，稳定后缓慢回升）。</summary>
    private int _tuneAdjust;

    /// <summary>最终目标并发 = 基准 + 微调，夹在 1~128。</summary>
    private int EffectiveTarget =>
        Math.Clamp(Volatile.Read(ref _baseConcurrency) + Volatile.Read(ref _tuneAdjust), 1, 128);
    private int _successCount;
    private int _failureStreak;
    private int _tuneCounter;

    /// <summary>同一域名连续"镜像没这个文件"的次数，达到阈值就整体熔断。</summary>
    private string _missHost = "";
    private int _missCount;

    /// <summary>同类告警的节流表，避免上千条重复日志把界面线程压垮。</summary>
    private readonly Dictionary<string, DateTime> _lastWarn = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>平滑后的速度（EMA）。瞬时速度抖动会让"剩余时间"反复跳秒。</summary>
    private double _smoothSpeed;
    private DateTime _lastSpeedSample = DateTime.MinValue;

    public bool IsRunning => _running;

    // ── 任务组：一次"下载整合包"/"安装游戏"是一个任务 ──
    private readonly List<DownloadTask> _tasks = new();
    private DownloadTask? _currentTask;

    /// <summary>所有任务组（界面任务中心按这个列表展示）。</summary>
    public IReadOnlyList<DownloadTask> Tasks { get { lock (_sync) return _tasks.ToList(); } }

    /// <summary>开始一个新任务组；之后的 Enqueue 都归到它名下。</summary>
    public DownloadTask BeginTask(string title, string kind = "")
    {
        var task = new DownloadTask { Title = title, Kind = kind, Preparing = true };
        lock (_sync)
        {
            _currentTask = task;
            _tasks.Add(task);
        }
        return task;
    }

    /// <summary>当前任务（兼容旧的无参调用；新代码请保存 BeginTask 返回的句柄）。</summary>
    public DownloadTask? CurrentTask { get { lock (_sync) return _currentTask; } }

    /// <summary>给当前任务加一个安装步骤（预留：加载器安装等）。</summary>
    public TaskStep? AddStep(string name)
    {
        DownloadTask? task;
        lock (_sync) { task = _currentTask; }
        return task is null ? null : AddStep(name, task);
    }

    /// <summary>把安装步骤登记到指定任务，不依赖"当前任务"这个全局状态。</summary>
    public TaskStep AddStep(string name, DownloadTask task)
    {
        var step = new TaskStep { Name = name };
        task.AddStep(step);
        return step;
    }

    /// <summary>通知界面：任务已建立（用于立刻刷新任务列表）。</summary>
    public void MarkTaskReady() => ItemChanged?.Invoke(this, new DownloadItem { Name = "" });

    public void MarkCurrentFinished()
    {
        lock (_sync) { if (_currentTask is not null) _currentTask.Finished = true; }
    }

    /// <summary>标记指定任务整体完成（安装流程结束时调用）。</summary>
    public void MarkFinished(DownloadTask task)
    {
        task.Finished = true;
        ItemChanged?.Invoke(this, new DownloadItem { Name = "" });
    }

    /// <summary>清掉已完成的任务组（界面"清除已完成"）。</summary>
    public void ClearFinishedTasks()
    {
        lock (_sync) _tasks.RemoveAll(t => t.Finished && t.SnapshotItems().All(i => !i.IsActive));
    }

    public void ClearTasks() { lock (_sync) _tasks.Clear(); }
    public bool IsPaused => _paused;
    public IReadOnlyList<DownloadItem> Items { get { lock (_sync) return _items.ToList(); } }

    public event EventHandler<DownloadAggregate>? ProgressChanged;
    public event EventHandler<DownloadItem>? ItemChanged;
    public event EventHandler<DownloadAggregate>? Finished;

    public DownloadManager(HttpService http, TempFileManager temp, SpeedLimiter limiter,
        DownloadSettings settings, LocalLibraryIndex? index = null, MirrorManager? mirrors = null)
    {
        _http = http;
        _temp = temp;
        _limiter = limiter;
        _settings = settings;
        // 全局连接预算：保证"文件数 × 每段连接"的总量不超过并发上限，
        // 同时让"只剩一两个大文件"时自动放宽段数、跑满带宽。
        _budget = new StreamBudget { Max = settings.MaxConcurrentTasks };
        _downloader = new ChunkedDownloader(http, temp, limiter, settings, _budget);
        _index = index ?? new LocalLibraryIndex(Path.Combine(Path.GetTempPath(), "lpml-noindex"));
        _mirrors = mirrors;
        // 几千个 I/O 任务排队时，默认线程池起步线程数偏少会造成"有并发额度却迟迟不开跑"。
        // 只抬高下限，不动上限；幂等，多处构造也安全。
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorker, 16), Math.Max(minIo, 16));
    }

    /// <summary>接上镜像管理器后，403/404 会自动换源重试。</summary>
    public void UseMirrors(MirrorManager mirrors) => _mirrors = mirrors;

    /// <summary>
    /// 后台把已有目录（游戏目录 / 实例文件夹 / 第三方启动器目录）扫进索引。
    /// 不阻塞下载：持久化索引立刻可用，扫描结果增量补进去。
    /// </summary>
    public Task WarmupIndexAsync(IEnumerable<string> roots)
    {
        return Task.Run(() =>
        {
            try
            {
                var added = _index.Scan(roots);
                if (added > 0)
                    Log.Info("Download", L.F("String.Core.Log.Index.Scanned", _index.ScannedCount, added, _index.Count));
            }
            catch (Exception ex)
            {
                Log.Warn("Download", L.T("String.Core.Log.Index.Failed") + " " + ex.Message);
            }
        });
    }

    public void Clear()
    {
        lock (_sync)
        {
            foreach (var item in _items) item.PropertyChanged -= ItemOnPropertyChanged;
            _items.Clear();
        }
    }

    /// <summary>
    /// 只移除已完成/已跳过的项，正在下载的和待下载的都保留。
    /// 这些文件仍留在各自任务的 Items 里，界面上照常能看到，只是不再参与汇总。
    /// </summary>
    private void PruneCompleted()
    {
        lock (_sync)
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.IsActive || item.Status is DownloadStatus.Pending or DownloadStatus.Paused
                    or DownloadStatus.Failed or DownloadStatus.Canceled) continue;
                item.PropertyChanged -= ItemOnPropertyChanged;
                _items.RemoveAt(i);
            }
        }
    }

    public void Enqueue(IEnumerable<DownloadItem> items)
    {
        DownloadTask? task;
        lock (_sync) { task = _currentTask; }
        if (task is null) EnqueueCore(items, null);
        else Enqueue(items, task);
    }

    /// <summary>
    /// 把一批文件明确归到指定任务名下。
    /// 并发装两个东西时，每批文件只会进它自己的任务，不会串台。
    /// </summary>
    public void Enqueue(IEnumerable<DownloadItem> items, DownloadTask task)
        => EnqueueCore(items, task);

    private void EnqueueCore(IEnumerable<DownloadItem> items, DownloadTask? task)
    {
        lock (_sync)
        {
            foreach (var item in items)
            {
                // 先退订再订阅，避免同一文件被重跑时重复触发事件
                item.PropertyChanged -= ItemOnPropertyChanged;
                item.PropertyChanged += ItemOnPropertyChanged;
                if (!_items.Contains(item)) _items.Add(item);

                if (task is not null)
                {
                    // 已经属于别的任务就不动：这个分支是并发安装串台的兜底
                    if (item.Owner is null) { item.Owner = task; task.AddItem(item); }
                    else if (ReferenceEquals(item.Owner, task) && !task.SnapshotItems().Contains(item))
                        task.AddItem(item);
                    task.Preparing = false;   // 有文件了就不算"准备中"
                }
            }
        }
    }

    public void Enqueue(DownloadItem item) => Enqueue([item]);

    /// <summary>把单个文件明确归到指定任务名下（批量重载的便捷版）。</summary>
    public void Enqueue(DownloadItem item, DownloadTask task) => Enqueue([item], task);

    private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DownloadItem item) return;
        if (e.PropertyName == nameof(DownloadItem.Status) || e.PropertyName == nameof(DownloadItem.StatusText))
            ItemChanged?.Invoke(this, item);

        // 项完成/失败 → 检查所属任务是否整体结束
        if (e.PropertyName == nameof(DownloadItem.Status)) SyncTaskFinished(item);
    }

    /// <summary>该任务的所有下载项都落地后，把它标成已完成（界面不再显示"进行中"）。</summary>
    private void SyncTaskFinished(DownloadItem item)
    {
        lock (_sync)
        {
            // 优先用文件上记录的归属；老数据没记录时才回退到遍历
            var task = item.Owner
                ?? _tasks.FirstOrDefault(t => t.SnapshotItems().Any(i => ReferenceEquals(i, item)));
            if (task is null || task.Finished) return;
            var items = task.SnapshotItems();
            if (items.Count == 0) return;
            if (items.Any(i => i.IsActive || i.Status == DownloadStatus.Pending)) return;
            task.Finished = true;
        }
    }

    public void Pause()
    {
        _paused = true;
        lock (_sync)
        {
            foreach (var item in _items.Where(i => i.IsActive))
            {
                item.Cancel();
                item.Status = DownloadStatus.Paused;
                item.StatusText = L.T("String.Core.Status.Paused");
            }
        }
        _cts.Cancel();
    }

    public void CancelAll()
    {
        _cts.Cancel();
        lock (_sync)
        {
            foreach (var item in _items.Where(i => i.Status is DownloadStatus.Pending or DownloadStatus.Downloading or DownloadStatus.Preparing))
            {
                item.Status = DownloadStatus.Canceled;
                item.StatusText = L.T("String.Core.Status.Cancelled");
            }
        }
    }

    /// <summary>取消某个任务（及其所有未完成的文件）。</summary>
    public void CancelTask(DownloadTask task)
    {
        lock (_sync)
        {
            foreach (var item in task.SnapshotItems().Where(i => i.Status is DownloadStatus.Pending
                                                         or DownloadStatus.Downloading
                                                         or DownloadStatus.Preparing
                                                         or DownloadStatus.Paused))
            {
                item.Status = DownloadStatus.Canceled;
                item.StatusText = L.T("String.Core.Status.Cancelled");
            }
            task.Finished = true;
        }
    }

    /// <summary>取消单个文件。</summary>
    public void CancelItem(DownloadItem item)
    {
        lock (_sync)
        {
            if (item.Status is DownloadStatus.Pending or DownloadStatus.Downloading
                or DownloadStatus.Preparing or DownloadStatus.Paused)
            {
                item.Status = DownloadStatus.Canceled;
                item.StatusText = L.T("String.Core.Status.Cancelled");
            }
        }
    }

    /// <summary>
    /// 把所有没下完的文件（失败 / 取消 / 暂停 / 排队）重新排队并开跑。
    /// 给界面上的"继续下载未完成文件"按钮用——不用一个个右键重新下载。
    /// </summary>
    public async Task ResumeIncompleteAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            foreach (var item in _items.Where(i =>
                         i.Status is DownloadStatus.Failed or DownloadStatus.Canceled
                                   or DownloadStatus.Paused or DownloadStatus.Pending))
            {
                item.Error = null;
                item.RetryUsed = 0;
                item.Status = DownloadStatus.Pending;
                item.StatusText = L.T("String.Core.Status.Queued");
            }
            if (_currentTask is not null) _currentTask.Finished = false;
        }
        await RunAsync(token).ConfigureAwait(false);
    }

    /// <summary>还有没下完的文件吗（给"继续下载"按钮判断要不要显示）。</summary>
    public bool HasIncomplete
    {
        get
        {
            lock (_sync)
                return _items.Any(i => i.Status is DownloadStatus.Failed or DownloadStatus.Canceled
                                             or DownloadStatus.Paused or DownloadStatus.Pending);
        }
    }

    /// <summary>把一批失败/取消的文件重新排队，并立刻跑（不重跑已完成的文件）。</summary>
    public async Task RunItemsAsync(IEnumerable<DownloadItem> items, CancellationToken token = default)
        => await RunItemsAsync(items, null, token).ConfigureAwait(false);

    /// <summary>
    /// 跑一批新文件，并明确归到指定任务（整合包的本体文件、模组等都走这里）。
    /// 不清空全局队列：并发安装时清空会把另一个任务正在下载的文件一并抹掉。
    /// RunAsync 只挑"未完成"状态的文件，所以已下载完的历史批次不会被重跑。
    /// </summary>
    public async Task RunItemsAsync(IEnumerable<DownloadItem> items, DownloadTask? task, CancellationToken token = default)
    {
        var targets = items.ToList();
        lock (_sync)
        {
            foreach (var item in targets)
            {
                // 新文件才重置；重试已存在的文件时也清掉失败/取消标记
                if (item.Status is DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Paused)
                    item.Reset();
                item.PropertyChanged -= ItemOnPropertyChanged;
                item.PropertyChanged += ItemOnPropertyChanged;
                if (!_items.Contains(item)) _items.Add(item);

                if (task is not null && item.Owner is null)
                {
                    item.Owner = task;
                    task.AddItem(item);
                    task.Preparing = false;
                }
            }
        }
        await RunAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// 重跑整批：连已完成的文件也一并重下（"重新下载"）。
    /// 会先删掉本地已落地的文件，避免校验命中直接跳过。
    /// </summary>
    public async Task RedownloadItemsAsync(IEnumerable<DownloadItem> items, CancellationToken token = default)
    {
        var targets = items.ToList();
        foreach (var item in targets)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(item.Destination) && File.Exists(item.Destination))
                    File.Delete(item.Destination);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        await RunItemsAsync(targets, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 跑传入的这批（整合包这种需要先下完再解压的场景）。
    /// 不再 Clear 全局队列：一次安装会分多批，清掉的话任务里就只剩最后一批文件，
    /// 看起来像"整合包只有一个下载项"。
    /// </summary>
    public async Task RunWithAsync(IEnumerable<DownloadItem> items, CancellationToken token = default)
        => await RunWithAsync(items, null, token).ConfigureAwait(false);

    public async Task RunWithAsync(IEnumerable<DownloadItem> items, DownloadTask? task, CancellationToken token = default)
    {
        if (task is null) Enqueue(items);
        else Enqueue(items, task);
        await RunAsync(token).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken token = default)
    {
        // 已经在跑就等它结束再开一轮。两轮并发会让同一批文件被写两次，
        // 表现就是日志里那种 ".part 文件被另一个进程占用"。
        // 必须循环等待：await 返回后可能又有人开了新一轮（B 等 A 的间隙里 C 进来了），
        // 只等一次的话 B 和 C 会同时往下跑，互相覆盖 _cts。
        var waited = 0;
        while (Volatile.Read(ref _running))
        {
            var current = _runCompletion;
            if (current is null) break;   // 状态不一致，别死等

            try { await current.Task.ConfigureAwait(false); }
            catch (Exception) { /* 前一轮的结果不影响这一轮 */ }

            if (++waited > 20)
            {
                Log.Warn("Download", L.T("String.Core.Log.Download.Busy"));
                break;
            }
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        _paused = false;
        _running = true;
        _budget.Reset();
        _runCompletion = new TaskCompletionSource();

        // 把上一批已下载完的文件从"活跃队列"里移走（任务自己仍保留完整记录）。
        // 不清空全局队列是为了不打断并发安装，但留着的话顶部汇总会把历史文件
        // 也算进分母，看起来像"刚开始就快满了"。
        PruneCompleted();

        var started = DateTime.UtcNow;

        try
        {
            var pending = new List<DownloadItem>();
            lock (_sync)
            {
                // Canceled 也算"没下完"：点了继续 / 重新开始就该接着下，
                // 以前它不在筛选里，于是取消过的文件再也不会被下载。
                pending.AddRange(_items.Where(i =>
                    i.Status is DownloadStatus.Pending or DownloadStatus.Paused
                              or DownloadStatus.Failed or DownloadStatus.Canceled));
            }
            // 缓存命中：有 SHA-1 且缓存里已有 → 直接复制，不进下载队列
            var cacheHits = 0;
            var stillPending = new List<DownloadItem>();
            foreach (var item in pending)
            {
                // 本地已有相同 sha1 的文件 → 硬链接过去，不用下载
                if (!string.IsNullOrWhiteSpace(item.Sha1)
                    && _index.TryGetPath(item.Sha1!, out var existing)
                    && !string.Equals(existing, item.Destination, StringComparison.OrdinalIgnoreCase)
                    && HardLink.TryCreate(existing, item.Destination))
                {
                    cacheHits++;
                    item.BytesReceived = item.ExpectedSize ?? new FileInfo(item.Destination).Length;
                    item.StatusText = L.T("String.Core.Status.Reused");
                    item.Status = DownloadStatus.Completed;
                    continue;
                }
                item.Status = DownloadStatus.Pending;
                item.StatusText = L.T("String.Core.Status.Queued");
                stillPending.Add(item);
            }

            if (cacheHits > 0)
                Log.Info("Download", L.F("String.Core.Log.Cache.Hit", cacheHits, pending.Count));
            pending = stillPending;

            // 开跑前先把要访问的域名解析好，避免每个小文件各解析一次
            var warmHosts = pending.Select(i => i.Url).ToList();
            _ = HttpService.WarmupDnsAsync(warmHosts);

            // 单域连接池上限跟随并发目标，并把本次真正请求的域名记一行日志
            _http.ApplyConnectionLimit(EffectiveTarget);
            var hostSummary = string.Join("、", warmHosts
                .Select(HostOf)
                .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(6)
                .Select(g => $"{g.Key}×{g.Count()}"));
            if (!string.IsNullOrEmpty(hostSummary))
                Log.Info("Download", L.F("String.Core.Log.Download.Hosts", hostSummary));

            var throughput = Task.Run(() => ThroughputLoopAsync(started, _cts.Token), _cts.Token);

            // 队列里有多少文件、准备放行多少 —— 出现"全是等待中、一个都没跑"时，
            // 靠这条日志就能分清是队列为空还是许可没放出去。
            Log.Info("Download", L.F("String.Core.Log.Download.StartQueue",
                pending.Count, EffectiveTarget));

            // 队列为空但界面上有条目：多半是本地已存在被跳过了，
            // 不把状态标出来就会一直显示"等待中"，看着像卡死。
            if (pending.Count == 0)
            {
                lock (_sync)
                {
                    foreach (var item in _items.Where(i => i.Status == DownloadStatus.Pending))
                    {
                        item.Status = DownloadStatus.Skipped;
                        item.StatusText = L.T("String.Core.Status.AlreadyHave");
                    }
                }
            }

            // 小文件优先、大文件靠后：
            // ① 数量占绝大多数的库文件先跑完，进度条快速推进；
            // ② 队列从"小→大"单调变化，自适应并发就能自然地先高后低。
            if (_settings.EnableSizeAwareConcurrency)
                pending = pending.OrderBy(i => i.ExpectedSize ?? 0).ToList();

            // 基准并发：开了自适应就按文件大小算，否则用固定设定值
            _baseConcurrency = _settings.EnableSizeAwareConcurrency
                ? ComputeSizeAwareConcurrency(pending)
                : Math.Clamp(_settings.MaxConcurrentTasks, 1, 128);
            _tuneAdjust = 0;
            _successCount = 0;
            _failureStreak = 0;
            // 初始 0 许可、最大 128：先放出 _effectiveConcurrency 个，
            // 之后可以靠 Release / 额外 Wait 动态增减，不用重建信号量。
            using var semaphore = new SemaphoreSlim(0, 128);
            _released = 0;
            // 渐进放行：先放一小批，之后每隔一段时间再放一个，平滑爬到目标并发。
            // 一次性 Release(N) 会让 N 个 TCP/TLS 握手同时爆发，老机器卡、镜像还容易限流。
            var rampUp = Task.Run(() => RampUpLoopAsync(semaphore, _cts.Token), _cts.Token);

            // 文件大小自适应规划器：随队列推进调整基准并发（关掉则不启动）
            var sizeAware = _settings.EnableSizeAwareConcurrency
                ? Task.Run(() => SizeAwareLoopAsync(pending, _cts.Token), _cts.Token)
                : Task.CompletedTask;
            var reporter = Task.Run(() => ReportLoopAsync(started, _cts.Token), _cts.Token);

            var tasks = pending.Select(async item =>
            {
                await semaphore.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    RerouteDegraded(item);
                    for (var attempt = 0; attempt <= _settings.RetryCount; attempt++)
                    {
                        try
                        {
                            await _downloader.DownloadAsync(item, _cts.Token).ConfigureAwait(false);
                            Interlocked.Increment(ref _successCount);

                            // 登记进索引，下次装别的版本可以直接复用
                            if (!string.IsNullOrWhiteSpace(item.Sha1))
                            {
                                try { _index.Register(item.Sha1!, item.Destination); }
                                catch (Exception) { /* 索引失败不影响使用 */ }
                            }

                            Interlocked.Exchange(ref _failureStreak, 0);
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (attempt < _settings.RetryCount)
                        {
                            item.RetryUsed++;
                            item.StatusText = L.F("String.Core.Status.Retrying", attempt + 1, _settings.RetryCount);

                            var badHost = HostOf(item.Url);
                            var hostFailure = IsHostFailure(ex);
                            var missing = IsMissingOnMirror(ex);
                            if (hostFailure) _health.MarkBroken(badHost);
                            if (missing) NoteMirrorMiss(badHost);

                            // 403/404 说明这个镜像根本没有该文件；DNS/SSL 失败说明这个域名根本连不上。
                            // 两者重试同一个地址都毫无意义，必须换源。
                            var switched = TrySwitchMirror(item, ex, attempt);
                            if (switched)
                            {
                                if (ThrottleWarn("switch:" + badHost))
                                    Log.Warn("Download", L.F("String.Core.Log.Download.SwitchMirror", item.Name, HostOf(item.Url)));
                            }
                            else if (ThrottleWarn("retry:" + badHost))
                                Log.Warn("Download", L.F("String.Core.Log.Download.Retrying", item.Name, ex.Message));

                            // 已经换到别的源就立刻重试，不必退避；429 是被限流，再拉长一档
                            var backoff = Math.Min(400 * Math.Pow(2, attempt), 8000);
                            var delay = switched && (hostFailure || missing)
                                ? 0
                                : IsRateLimited(ex) ? Math.Min(backoff * 3, 15000) : backoff;
                            if (delay > 0)
                                await Task.Delay(TimeSpan.FromMilliseconds(delay), _cts.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 暂停也是靠取消 _cts 实现的，但那些文件是"暂停"不是"取消"。
                    // 以前一律标成 Canceled，点继续后它们既不在排队里、界面又显示已取消。
                    if (_paused)
                    {
                        item.Status = DownloadStatus.Paused;
                        item.StatusText = L.T("String.Core.Status.Paused");
                    }
                    else if (item.Status != DownloadStatus.Canceled)
                    {
                        item.Status = DownloadStatus.Canceled;
                        item.StatusText = L.T("String.Core.Status.Cancelled");
                    }
                }
                catch (Exception ex)
                {
                    item.Status = DownloadStatus.Failed;
                    item.StatusText = L.T("String.Core.Status.Failed");
                    item.Error = ex.Message;
                    Interlocked.Increment(ref _failureStreak);
                    Log.Error("Download", L.F("String.Core.Log.Download.Failed", item.Name, ex.Message));
                }
                finally
                {
                    semaphore.Release();
                    TuneConcurrency();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            _cts.Cancel();
            try { await rampUp.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await sizeAware.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await reporter.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await throughput.ConfigureAwait(false); } catch (OperationCanceledException) { }
            Report(started);
            LogPhaseSummary(started, pending);
            lock (_sync) { if (_currentTask is not null) _currentTask.Finished = true; }
            Finished?.Invoke(this, BuildAggregate(started));
        }
        finally
        {
            _running = false;
            _runCompletion?.TrySetResult();
            _temp.TrimCacheIfNeeded();
        }
    }

    /// <summary>
    /// 自适应并发：小文件为主时并发越高越快，但并发过高会触发服务端限流。
    /// 策略：连续成功就往上加（每次 +2，到 128 封顶），连续失败就退回去。
    /// </summary>
    /// <summary>当前已放行的许可数。由 ramp-up 泵和目标并发共同决定。</summary>
    private int _released;

    /// <summary>
    /// 渐进放行泵：把 semaphore 的许可数从 0 平滑拉到目标并发。
    /// 目标并发被自适应调整时（TuneConcurrency 改 _effectiveConcurrency），
    /// 这里会跟着往上放或往下收，所以不需要别处再动信号量。
    /// </summary>
    private async Task RampUpLoopAsync(SemaphoreSlim semaphore, CancellationToken token)
    {
        try
        {
            var settings = _settings;
            var startCount = Math.Clamp(settings.RampUpStartCount, 1, 128);
            var interval = Math.Max(0, settings.RampUpIntervalMs);

            // 首批：一次性放行，避免开头完全串行等太久
            if (settings.EnableRampUp)
            {
                var first = Math.Min(startCount, EffectiveTarget);
                for (var i = 0; i < first; i++)
                {
                    semaphore.Release();
                    Interlocked.Increment(ref _released);
                }
            }
            else
            {
                var all = EffectiveTarget;
                for (var i = 0; i < all; i++)
                {
                    semaphore.Release();
                    Interlocked.Increment(ref _released);
                }
            }

            while (!token.IsCancellationRequested)
            {
                var target = EffectiveTarget;
                var current = Volatile.Read(ref _released);

                // 预算跟随并发变化：并发放宽时段数也能放宽，被限流收窄时一起收窄
                _budget.Max = target;
                _http.ApplyConnectionLimit(target);

                if (current < target)
                {
                    semaphore.Release();
                    Interlocked.Increment(ref _released);
                    if (interval > 0 && settings.EnableRampUp)
                        await Task.Delay(interval, token).ConfigureAwait(false);
                }
                else if (current > target)
                {
                    // 并发被下调：收回一个许可（有任务在跑时会等它归还）
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    Interlocked.Decrement(ref _released);
                }
                else
                {
                    // 已达标，短暂休眠避免空转
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void TuneConcurrency()
    {
        try
        {
            var max = Math.Clamp(_settings.MaxConcurrentTasks, 1, 128);
            // 微调的下限保底 1（不会压到 0），上限不超过用户设定值

            // 每 12 个文件做一次判定，用成功率而不是"连续失败"——
            // 单个文件失败（404、镜像缺文件）很常见，不该因此砍并发。
            var done = Interlocked.Increment(ref _tuneCounter);
            if (done < 12) return;
            Interlocked.Exchange(ref _tuneCounter, 0);

            var ok = Volatile.Read(ref _successCount);
            var bad = Volatile.Read(ref _failureStreak);
            var total = ok + bad;
            Interlocked.Exchange(ref _successCount, 0);
            Interlocked.Exchange(ref _failureStreak, 0);
            if (total == 0) return;

            var rate = ok / (double)total;

            // 只改微调量，实际放行/收回由 RampUpLoopAsync 平滑执行。
            // 基准值交给文件大小自适应去算，两者叠加，不互相覆盖。
            var adjust = Volatile.Read(ref _tuneAdjust);
            if (rate < 0.8) adjust -= 4;
            else if (rate > 0.95) adjust += 4;

            var baseC = Volatile.Read(ref _baseConcurrency);
            // 下限保底 1，上限不超过用户设的 MaxConcurrentTasks
            var minAdj = 1 - baseC;
            var maxAdj = Math.Max(0, max - baseC);
            Volatile.Write(ref _tuneAdjust, Math.Clamp(adjust, minAdj, maxAdj));
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 每 5 秒算一次有效吞吐（不是"已下载 / 总数"的百分比）。
    /// 连续两次明显偏低就记日志、把当前主源标成降级，后续换源会绕开它。
    /// </summary>
    private async Task ThroughputLoopAsync(DateTime started, CancellationToken token)
    {
        _lastThroughputSample = DateTime.UtcNow;
        _lastThroughputBytes = 0;
        _lowThroughputStreak = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                var seconds = (now - _lastThroughputSample).TotalSeconds;
                if (seconds <= 0) continue;

                var agg = BuildAggregate(started);
                var delta = agg.ReceivedBytes - _lastThroughputBytes;
                _lastThroughputBytes = agg.ReceivedBytes;
                _lastThroughputSample = now;
                var bps = Math.Max(0, delta / seconds);
                _health.Tick();

                // 没有活跃文件时不判定：暂停、队列已空都属于正常停顿
                if (agg.Active == 0) { _lowThroughputStreak = 0; continue; }

                // 阈值取曾经峰值的 20%，且不低于 50 KB/s，避免起步阶段误判
                var floor = Math.Max(50 * 1024, _peakBytesPerSecond * 0.2);
                if (bps >= floor) { _lowThroughputStreak = 0; continue; }

                _lowThroughputStreak++;
                if (_lowThroughputStreak < 2) continue;

                var mainHost = Items.Where(i => i.IsActive)
                    .Select(i => HostOf(i.Url))
                    .FirstOrDefault(h => !string.IsNullOrEmpty(h));
                if (string.IsNullOrEmpty(mainHost)) continue;

                _health.MarkDegraded(mainHost);
                Log.Warn("Download", L.F("String.Core.Log.Download.LowThroughput",
                    (long)(bps / 1024), _lowThroughputStreak, mainHost));

                // 只降一级：归零后要重新连续两次低吞吐才会再降，避免一路压到底
                _lowThroughputStreak = 0;

                // 收窄一点并发给源喘口气，实际放行由 RampUpLoopAsync 平滑执行
                var adjust = Volatile.Read(ref _tuneAdjust);
                var baseC = Volatile.Read(ref _baseConcurrency);
                var maxC = Math.Clamp(_settings.MaxConcurrentTasks, 1, 128);
                Volatile.Write(ref _tuneAdjust,
                    Math.Clamp(adjust - 4, 1 - baseC, Math.Max(0, maxC - baseC)));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReportLoopAsync(DateTime started, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Report(started);
                await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
    }

    private void Report(DateTime started)
    {
        var agg = BuildAggregate(started);
        if (agg.BytesPerSecond > _peakBytesPerSecond) _peakBytesPerSecond = agg.BytesPerSecond;
        ProgressChanged?.Invoke(this, agg);
    }

    /// <summary>
    /// 按文件大小算基准并发：小文件多开、大文件收窄。
    /// 取队列前一批文件的中位数判断"现在是哪个阶段"。
    /// </summary>
    private int ComputeSizeAwareConcurrency(IReadOnlyList<DownloadItem> remaining)
    {
        var sample = remaining.Take(24).ToList();
        if (sample.Count == 0) return Math.Clamp(_settings.MaxConcurrentTasks, 1, 128);

        var sizes = sample.Select(i => i.ExpectedSize ?? 0).OrderBy(x => x).ToList();
        var median = sizes[sizes.Count / 2];

        var threshold = (long)Math.Max(1, _settings.LargeFileThresholdMb) * 1024 * 1024;
        // 0 = 极小文件，1 = 已达大文件门槛
        var t = Math.Clamp(median / (double)threshold, 0.0, 1.0);

        var small = Math.Clamp(_settings.SmallFileConcurrency, 1, 128);
        var large = Math.Clamp(_settings.LargeFileConcurrency, 1, 128);
        if (large > small) (large, small) = (small, large);

        var target = (int)Math.Round(small - (small - large) * t);

        // 小文件都下完了、队列里只剩一堆大文件时，并发不该继续收窄：
        // 文件数虽少，但数量已经超过"大文件并发"，收窄只会让它们排队干等。
        // 这时放宽到"小文件并发的一半"，让更多大文件同时跑（每个内部还会分段）。
        if (t >= 0.9 && remaining.Count > large)
        {
            var boosted = Math.Max(large, Math.Min(small / 2, remaining.Count));
            target = Math.Clamp(boosted, target, small);
        }

        return Math.Clamp(target, large, small);
    }

    /// <summary>
    /// 文件大小自适应规划器：定期根据队列里剩下的文件重新定基准并发。
    /// 队列按体积从小到大排过序，所以会自然经历"小文件高并发 → 大文件低并发"。
    /// </summary>
    private async Task SizeAwareLoopAsync(List<DownloadItem> queue, CancellationToken token)
    {
        var lastLogged = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = queue.Where(i => i.Status == DownloadStatus.Pending).ToList();
                var target = ComputeSizeAwareConcurrency(remaining);

                var previous = Volatile.Read(ref _baseConcurrency);
                if (target != previous)
                {
                    Volatile.Write(ref _baseConcurrency, target);
                    // 变化明显时才记一条，避免刷屏
                    if (Math.Abs(target - lastLogged) >= 8)
                    {
                        lastLogged = target;
                        Log.Info("Download", L.F("String.Core.Log.Download.ConcurrencyTuned", target,
                            remaining.Count == 0 ? 0 : (remaining[0].ExpectedSize ?? 0) / 1024));
                    }
                }

                if (remaining.Count == 0) return;
                await Task.Delay(400, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>异常是不是"镜像没有这个文件"（403/404）。429 是限流，不算。</summary>
    private static bool IsMissingOnMirror(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("HTTP 403") || message.Contains("HTTP 404");
    }

    /// <summary>异常是不是"被限流"（429）。</summary>
    private static bool IsRateLimited(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("429");
    }

    /// <summary>
    /// 主机级故障：DNS 解析失败、SSL 握手失败、连接被拒。
    /// 这类错误换源才有意义，对同一个地址重试多少次都一样。
    /// 用消息 + 内层异常链判断，避免依赖具体 SocketException 类型名。
    /// </summary>
    private static bool IsHostFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message ?? "";
            if (m.Contains("不知道这样的主机", StringComparison.OrdinalIgnoreCase)
                || m.Contains("No such host", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
                || m.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase)
                || m.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase)
                || m.Contains("无法建立 SSL", StringComparison.OrdinalIgnoreCase)
                || m.Contains("A connection attempt failed", StringComparison.OrdinalIgnoreCase)
                || m.Contains("连接尝试失败", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>连续 12 次 403 说明这个域名对这一类文件整体不可用，别让几千个文件逐个再试。</summary>
    private void NoteMirrorMiss(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        lock (_sync)
        {
            if (!string.Equals(_missHost, host, StringComparison.OrdinalIgnoreCase))
            { _missHost = host; _missCount = 0; }
            _missCount++;
            if (_missCount >= 12) _health.MarkBroken(host, 120);
        }
    }

    /// <summary>相同来源的告警每 3 秒最多一条，避免死域名刷屏拖垮界面线程。</summary>
    private bool ThrottleWarn(string key)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (_lastWarn.TryGetValue(key, out var last) && (now - last).TotalSeconds < 3) return false;
            _lastWarn[key] = now;
            if (_lastWarn.Count > 64) _lastWarn.Clear();
            return true;
        }
    }

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    /// <summary>
    /// 换源重试：把 item.Url 从当前镜像切到下一个候选镜像。
    /// 顺序见 MirrorManager.FailoverOrder——其他镜像优先，官方源兜底。
    /// 返回是否成功换源（没有原始地址或已无候选时返回 false）。
    /// </summary>
    private bool TrySwitchMirror(DownloadItem item, Exception ex, int attempt)
    {
        if (_mirrors is null) return false;
        // 403/404（镜像没文件）和 DNS/SSL 失败（域名连不上）都要换源；
        // 429 是限流，换源没用，留在原源退避。
        if (!IsMissingOnMirror(ex) && !IsHostFailure(ex)) return false;

        var official = item.OriginalUrl;
        if (string.IsNullOrWhiteSpace(official)) return false;

        var currentHost = HostOf(item.Url);

        // 第二次还是 403：第三方镜像同步的是同一份库，一家没有基本家家都没有，
        // 再顺着镜像列表试下去纯属浪费重试次数（还容易撞上 429）。
        // 官方源一定有这个文件，直接上官方兜底。
        if (attempt >= 1)
        {
            var direct = _mirrors.RewriteWith(official, item.Category, MirrorKind.Official);
            if (!string.IsNullOrWhiteSpace(direct) && HostOf(direct) != currentHost)
            {
                item.Url = direct;
                return true;
            }
        }

        // 已经换过几次了？按顺序取下一个
        var order = _mirrors.FailoverOrder(item.Category);
        var nextKind = order.FirstOrDefault(kind =>
        {
            var candidate = _mirrors.RewriteWith(official, item.Category, kind);
            return !string.IsNullOrWhiteSpace(candidate)
                   && HostOf(candidate) != currentHost
                   && !_health.IsDegraded(HostOf(candidate));
        });

        // 所有镜像都试过还没找到 → 直接用官方地址
        var target = nextKind != default
            ? _mirrors.RewriteWith(official, item.Category, nextKind)
            : official;

        if (string.IsNullOrWhiteSpace(target) || HostOf(target) == currentHost) return false;

        item.Url = target;
        return true;
    }

    /// <summary>
    /// 还没开始下载的文件，若它的源刚被判为吞吐降级，就换到下一个健康源。
    /// 已经在飞的请求不动，只影响后续排队的条目。
    /// </summary>
    private void RerouteDegraded(DownloadItem item)
    {
        if (_mirrors is null) return;
        if (!_health.IsDegraded(HostOf(item.Url))) return;
        var official = item.OriginalUrl;
        if (string.IsNullOrWhiteSpace(official)) return;
        var currentHost = HostOf(item.Url);

        foreach (var kind in _mirrors.FailoverOrder(item.Category))
        {
            var candidate = _mirrors.RewriteWith(official, item.Category, kind);
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var host = HostOf(candidate);
            if (host == currentHost || _health.IsDegraded(host)) continue;
            item.Url = candidate;
            Log.Warn("Download", L.F("String.Core.Log.Download.SwitchMirror", item.Name, host));
            return;
        }
    }

    /// <summary>下载阶段结束时的统计日志：文件数、耗时、平均/峰值速度，用来判断是否跑满带宽。</summary>
    private void LogPhaseSummary(DateTime started, IReadOnlyList<DownloadItem> batch)
    {
        var elapsed = DateTime.UtcNow - started;
        // 只统计本轮文件；_items 里还有上一轮已完成的文件，混进来会把平均值算虚高。
        var total = batch.Sum(i => i.BytesReceived);
        var avg = elapsed.TotalSeconds > 0 ? total / elapsed.TotalSeconds : 0;
        // 峰值取自 250ms 采样，单个小文件可能在两次采样之间就下完了；
        // 保证它至少不低于本轮平均，避免出现"平均 134 KB/s、峰值 17 KB/s"。
        var peak = Math.Max(_peakBytesPerSecond, avg);
        Log.Info("Download", L.F("String.Core.Log.Download.Phase",
            batch.Count,
            (long)elapsed.TotalMilliseconds,
            (long)(avg / 1024),
            (long)(peak / 1024)));
    }

    /// <summary>
    /// 对速度做指数滑动平均（时间常数约 2 秒）。
    /// 瞬时速度在两个文件交替的间隙会掉到 0，直接用它算 ETA 会出现
    /// "剩余 3 秒 → 0 秒 → 5 秒" 这种反复跳动，平滑后读数稳定。
    /// </summary>
    private double SmoothSpeed(double instant)
    {
        var now = DateTime.UtcNow;
        var elapsed = _lastSpeedSample == DateTime.MinValue ? 1.0 : (now - _lastSpeedSample).TotalSeconds;
        _lastSpeedSample = now;

        if (instant <= 0)
        {
            // 完全停了才归零；偶尔一帧为 0 不算
            if (elapsed > 1.5) _smoothSpeed = 0;
            return _smoothSpeed;
        }

        if (_smoothSpeed <= 0) { _smoothSpeed = instant; return _smoothSpeed; }

        var alpha = Math.Clamp(elapsed / 2.0, 0.05, 0.5);
        _smoothSpeed += alpha * (instant - _smoothSpeed);
        return _smoothSpeed;
    }

    public DownloadAggregate BuildAggregate(DateTime? started = null)
    {
        var snapshot = Items;
        var agg = new DownloadAggregate
        {
            Total = snapshot.Count,
            Completed = snapshot.Count(i => i.Status is DownloadStatus.Completed or DownloadStatus.Skipped),
            Failed = snapshot.Count(i => i.Status == DownloadStatus.Failed),
            Active = snapshot.Count(i => i.IsActive),
            TotalBytes = snapshot.Sum(i => i.TotalBytes),
            ReceivedBytes = snapshot.Sum(i => i.BytesReceived),
            ActiveProgress = snapshot.Where(i => i.IsActive).Sum(i => i.Progress),
            BytesPerSecond = SmoothSpeed(snapshot.Where(i => i.IsActive).Sum(i => i.Speed)),
            Elapsed = DateTime.UtcNow - (started ?? DateTime.UtcNow)
        };
        return agg;
    }
}
