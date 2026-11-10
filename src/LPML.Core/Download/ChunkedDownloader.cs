using LPML.Core.Config;
using LPML.Core.Utils;

namespace LPML.Core.Download;

/// <summary>
/// 单文件多线程分段下载器：
/// - 支持 HTTP Range 断点续传（中断后继续，不重下）
/// - 每个分片独立进度，合并后校验 SHA1 / 大小
/// - 服务端不支持 Range 时自动回退单线程
/// </summary>
public sealed class ChunkedDownloader
{
    private readonly HttpService _http;
    private readonly TempFileManager _temp;
    private readonly SpeedLimiter _limiter;
    private readonly DownloadSettings _settings;

    /// <summary>
    /// 全局连接预算（可选）。接入后，单文件分段数不再固定，
    /// 而是按"现在还剩多少空闲预算"动态决定：
    /// 队列里只剩这个大文件时它会多开几段，很多文件在跑时则自动收窄。
    /// </summary>
    private readonly StreamBudget? _budget;

    /// <summary>SHA-1 校验走独立线程池，且限住并发，不把文件下载的线程抢光。</summary>
    private static readonly SemaphoreSlim VerifySlots =
        new(Math.Max(2, Math.Min(4, Environment.ProcessorCount)));

    public ChunkedDownloader(HttpService http, TempFileManager temp, SpeedLimiter limiter,
        DownloadSettings settings, StreamBudget? budget = null)
    {
        _http = http;
        _temp = temp;
        _limiter = limiter;
        _settings = settings;
        _budget = budget;
    }

    public async Task DownloadAsync(DownloadItem item, CancellationToken token = default)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, item.Cancellation.Token);
        var ct = linked.Token;

        try
        {
            if (_settings.SkipExistingFiles && IsAlreadyValid(item))
            {
                item.Status = DownloadStatus.Skipped;
                item.StatusText = L.T("String.Core.Status.SkipExisting");
                item.TotalBytes = item.ExpectedSize ?? new FileInfo(item.Destination).Length;
                item.BytesReceived = item.TotalBytes;
                return;
            }

            // 体积已知且不到分段阈值的小文件：直接单线程下载，跳过 HEAD 探测。
            // 一次版本安装有几百上千个这类小文件，逐个探测等于请求数翻倍，是装得慢的主因之一。
            var thresholdBytes = _settings.ChunkThresholdMb * 1024L * 1024L;
            // 队列里只剩一两个文件时（文件级并发只有 1），把分段门槛临时降到 256KB：
            // 否则一个 1~2MB 的加载器 jar 会被判成「小文件」直接单线程，
            // 单连接又容易被源停滞反复拖住，界面上就一直停在「单线程下载」。
            if (item.IdleQueueBoost) thresholdBytes = Math.Min(thresholdBytes, 256L * 1024L);
            var knownSize = item.ExpectedSize ?? 0;
            if (knownSize > 0 && knownSize < thresholdBytes)
            {
                var stream = _budget?.Acquire(1) ?? 0;
                try
                {
                    item.TotalBytes = knownSize;
                    await DownloadSingleAsync(item, ct).ConfigureAwait(false);
                    await VerifyAsync(item, ct).ConfigureAwait(false);
                }
                finally
                {
                    _budget?.Release(stream);
                }
                item.Status = DownloadStatus.Completed;
                item.StatusText = L.T("String.Core.Status.Done");
                item.Speed = 0;
                return;
            }

            item.Status = DownloadStatus.Preparing;
            item.StatusText = L.T("String.Core.Status.Probing");

            var url = item.Url;
            var (length, acceptRanges) = await _http.ProbeAsync(url, ct).ConfigureAwait(false);
            var total = length ?? item.ExpectedSize ?? 0;
            item.TotalBytes = total > 0 ? total : item.ExpectedSize ?? 0;

            var canChunk = acceptRanges && total > 0 && total >= thresholdBytes && _settings.MaxChunksPerFile > 1;

            // BMCLAPI 的大文件会 302 到 OpenBMCLAPI 的签名节点（URL 带 s / e 参数），
            // 签名 URL 通常不接受 Range 分段，多段请求会被 WAF 判 403。
            // 这类域名一律单线程，跟随重定向拿完整文件即可。
            try
            {
                var h = new Uri(item.Url).Host;
                if (h.EndsWith("bangbang93.com", StringComparison.OrdinalIgnoreCase))
                    canChunk = false;
            }
            catch (UriFormatException) { /* 相对地址按原判定走 */ }

            // 单线程文件也占 1 条连接，这样预算才能真实反映当前压力
            var streams = _budget?.Acquire(1) ?? 0;
            try
            {
                if (!canChunk)
                {
                    // 只有队列空闲、且源明确不支持 Range 时才留一句说明，
                    // 免得用户看到「单线程」却不知道为什么没并发。
                    if (item.IdleQueueBoost && total >= thresholdBytes)
                        Log.Info("Download", L.T("String.Core.Status.SingleThread") + " (Range unsupported): " + item.Name);
                    await DownloadSingleAsync(item, ct).ConfigureAwait(false);
                }
                else
                {
                    // 分段下载：归还刚才的 1 条，按预算重新申请段数
                    _budget?.Release(streams);
                    streams = PlanChunks(total, item);
                    await DownloadChunksAsync(item, total, streams, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _budget?.Release(streams);
            }

            await VerifyAsync(item, ct).ConfigureAwait(false);
            item.Status = DownloadStatus.Completed;
            item.StatusText = L.T("String.Core.Status.Done");
            item.Speed = 0;
        }
        catch (OperationCanceledException)
        {
            // PauseTask / Pause 已经把状态设成 Paused，这里不能再覆盖成
            // 「已取消」——用户点的是暂停，不是取消。
            if (item.Status != DownloadStatus.Paused)
            {
                item.Status = DownloadStatus.Canceled;
                item.StatusText = L.T("String.Core.Status.Cancelled");
            }
        }
        catch (Exception ex)
        {
            // 429 说明这个源已经嫌请求多了：下次重试收回分段，别再用更多连接加压。
            if ((ex.Message ?? "").Contains("429")) item.IdleQueueBoost = false;
            item.Status = DownloadStatus.Failed;
            item.StatusText = L.T("String.Core.Status.Failed");
            item.Error = ex.Message;
            throw;
        }
        finally
        {
            linked.Dispose();
        }
    }

    private bool IsAlreadyValid(DownloadItem item)
    {
        // 强制重下：内容错误但格式合法的 jar 不能被「已存在」挡回去
        if (item.ForceRedownload) return false;
        try
        {
            if (!File.Exists(item.Destination)) return false;
            if (!FileHelper.IsUsableLibraryFile(item.Destination)) return false;
            var info = new FileInfo(item.Destination);
            if (item.ExpectedSize.HasValue && item.ExpectedSize.Value > 0 && info.Length != item.ExpectedSize.Value) return false;
            if (item.ExpectedSize.HasValue && item.ExpectedSize.Value == 0 && info.Length == 0) return false;
            if (_settings.VerifySha1 && !string.IsNullOrWhiteSpace(item.Sha1) && item.Sha1.Length == 40)
                return string.Equals(HashHelper.Sha1File(item.Destination), item.Sha1, StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (IOException) { return false; }
    }

    private async Task DownloadSingleAsync(DownloadItem item, CancellationToken ct)
    {
        item.Status = DownloadStatus.Downloading;
        item.Chunks = 1;
        item.StatusText = L.T("String.Core.Status.SingleThread");
        var started = DateTime.UtcNow;
        long lastReported = 0;
        var progress = new Progress<(long Received, long Total)>(p =>
        {
            item.BytesReceived = p.Received;
            if (p.Total > 0) item.TotalBytes = p.Total;
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            if (elapsed > 0.2)
            {
                item.Speed = (p.Received - lastReported) / elapsed;
                lastReported = p.Received;
                started = DateTime.UtcNow;
            }
        });
        await _http.DownloadToFileAsync(item.Url, item.Destination, progress, ct,
            (n, t) => _limiter.ConsumeAsync(n, t)).ConfigureAwait(false);
    }

    /// <summary>
    /// 决定这个文件开几段。
    /// 基准值按体积算（每 8MB 一段，不超过设置上限）；
    /// 如果当前空闲预算还很充足（说明队列里没别的东西在跑），就放宽段数把带宽吃满。
    /// </summary>
    private int PlanChunks(long total, DownloadItem item)
    {
        var baseChunks = Math.Min(_settings.MaxChunksPerFile, (int)Math.Ceiling(total / (8d * 1024 * 1024)));
        baseChunks = Math.Max(1, baseChunks);
        // 队列空闲时至少开 2 段，把空出来的连接预算真正用上；
        // 但这个源刚出过 403 / 429 / 停滞的话就收回到 2 段，别拿更多连接去撞限流。
        if (item.IdleQueueBoost) baseChunks = Math.Max(2, baseChunks);
        // 换过源说明这个源刚失败过：队列里还有很多文件在跑时收窄到 2 段，
        // 免得连累别人；但只剩这一个文件时总连接数完全可控，不必再压，
        // 否则最后一个加载器 jar 永远只能 2 线程。真被 429 时 DownloadAsync
        // 会把 IdleQueueBoost 置回 false，下一次重试仍会收窄。
        var restrict = item.TriedHosts.Count > 0 && !item.IdleQueueBoost;
        if (restrict) baseChunks = Math.Min(baseChunks, 2);

        if (_budget is null) return baseChunks;

        // 放宽上限：默认分段数的 2 倍、但每段至少 256KB。
        // 1~2MB 的加载器 jar 以前会被放到十几段，请求太碎反而更容易撞 429。
        var bySize = Math.Clamp((int)Math.Ceiling(total / (256d * 1024)), 1, _settings.MaxChunksPerFile);
        var boostCap = restrict
            ? baseChunks   // 这个源刚失败过，别再放宽段数
            : Math.Min(Math.Min(_settings.MaxChunksPerFile * 2, Math.Max(baseChunks, _budget.Max)),
                       Math.Max(baseChunks, bySize));
        var granted = _budget.Acquire(baseChunks, boostCap);

        if (granted > baseChunks)
            Log.Info("Download", L.F("String.Core.Log.ChunkBoosted", granted, baseChunks));

        return granted;
    }

    private async Task DownloadChunksAsync(DownloadItem item, long total, int chunks, CancellationToken ct)
    {
        chunks = Math.Max(1, chunks);
        var chunkSize = total / chunks;
        item.Chunks = chunks;
        item.Status = DownloadStatus.Downloading;
        item.StatusText = L.F("String.Core.Status.Chunked", chunks);

        var key = HashHelper.Sha1String(item.Url)[..16];
        var partFiles = new string[chunks];
        var ranges = new (long Start, long End)[chunks];
        for (var i = 0; i < chunks; i++)
        {
            var start = i * chunkSize;
            var end = i == chunks - 1 ? total - 1 : (start + chunkSize - 1);
            ranges[i] = (start, end);
            partFiles[i] = _temp.Allocate($"{key}_{i}.part", end - start + 1);
        }

        long received = 0;
        var started = DateTime.UtcNow;
        foreach (var file in partFiles)
        {
            var info = new FileInfo(file);
            if (info.Exists) received += info.Length;
        }
        item.BytesReceived = received;

        var tasks = new List<Task>();
        for (var i = 0; i < chunks; i++)
        {
            var index = i;
            tasks.Add(Task.Run(async () =>
            {
                var (start, end) = ranges[index];
                var part = partFiles[index];
                long offset = start;
                var partInfo = new FileInfo(part);
                if (partInfo.Exists && partInfo.Length > 0 && partInfo.Length <= end - start + 1)
                    offset = start + partInfo.Length;

                if (offset > end) return;

                var mode = offset == start ? FileMode.Create : FileMode.Open;
                await using var fs = new FileStream(part, mode, FileAccess.Write, FileShare.None, 1 << 20, true);
                if (mode == FileMode.Open) fs.Seek(0, SeekOrigin.End);

                await using var stream = await _http.OpenRangeAsync(item.Url, offset, end, ct).ConfigureAwait(false);
                var buffer = new byte[81920];
                int read;
                while ((read = await _http.ReadWithStallAsync(stream, buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await _limiter.ConsumeAsync(read, ct).ConfigureAwait(false);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    var now = Interlocked.Add(ref received, read);
                    item.BytesReceived = now;
                    _temp.NoteBytes(read);
                    var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                    if (elapsed > 0.2)
                        item.Speed = now / elapsed;
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        item.Status = DownloadStatus.Merging;
        item.StatusText = L.T("String.Core.Status.Merging");
        FileHelper.EnsureDirectory(Path.GetDirectoryName(item.Destination));
        await using (var output = new FileStream(item.Destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            var buffer = new byte[1 << 20];
            foreach (var part in partFiles)
            {
                if (!File.Exists(part)) throw new IOException(L.F("String.Core.Err.Download.ChunkMissing", part));
                await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        if (_settings.CleanChunkFiles)
            foreach (var part in partFiles) FileHelper.TryDelete(part);
    }

    private async Task VerifyAsync(DownloadItem item, CancellationToken ct)
    {
        item.Status = DownloadStatus.Verifying;
        item.StatusText = L.T("String.Core.Status.Verifying");
        await Task.Yield();
        if (!File.Exists(item.Destination)) throw new IOException(L.F("String.Core.Err.Download.NoResult", item.Destination));
        if (!FileHelper.IsUsableLibraryFile(item.Destination))
            throw new IOException(L.F("String.Core.Err.Download.InvalidJar", item.Destination));
        var info = new FileInfo(item.Destination);
        if (_settings.VerifyFileSize && item.ExpectedSize is > 0 && info.Length != item.ExpectedSize.Value)
            throw new IOException(L.F("String.Core.Err.Download.SizeMismatch", item.ExpectedSize, info.Length));
        if (_settings.VerifySha1 && !string.IsNullOrWhiteSpace(item.Sha1) && item.Sha1.Length == 40)
        {
            await VerifySlots.WaitAsync(ct).ConfigureAwait(false);
            string actual;
            try
            {
                actual = await Task.Run(() => HashHelper.Sha1File(item.Destination), ct).ConfigureAwait(false);
            }
            finally { VerifySlots.Release(); }
            if (!string.Equals(actual, item.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.F("String.Core.Err.Download.Sha1Mismatch", item.Sha1, actual));
        }
    }
}
