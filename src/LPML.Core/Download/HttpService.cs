using System.Net;
using System.Net.Http.Headers;
using LPML.Core.Config;
using LPML.Core.Utils;

namespace LPML.Core.Download;

/// <summary>统一 HTTP 出口：UA、代理、超时、重试、Range 探测。</summary>
public sealed class HttpService
{
    private HttpClient _client = new();
    private SocketsHttpHandler? _handler;
    private string _configKey = "";
    private DownloadSettings _settings = new();
    private readonly object _sync = new();

    public HttpService()
    {
        Configure(new DownloadSettings());
    }

    public void Configure(DownloadSettings settings)
    {
        lock (_sync)
        {
            _settings = settings;
            // 配置没变就继续复用同一个 HttpClient：重建会丢掉整个连接池，
            // 设置页点一次「应用」不该把正在复用的连接清空。
            var key = ConfigurationKey(settings);
            if (key == _configKey && _handler is not null) return;
            _configKey = key;
            // SocketsHttpHandler 才能设置连接池生命周期与 HTTP/2 多路复用。
            // HttpClientHandler 底层虽然也是它，但拿不到这些旋钮。
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                MaxConnectionsPerServer = 32,
                UseCookies = false,
                // 建连单独设超时：挂掉的源不会把整批请求拖到整体超时才放手
                ConnectTimeout = TimeSpan.FromSeconds(10),
                UseProxy = settings.UseProxy,

                // 连接池：连接尽量复用，减少重复的 TCP/TLS 握手（小文件场景开销大头）
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                // 响应头就绪即可返回，不等整个 body 缓冲完（大文件分段下载更快）
                ResponseDrainTimeout = TimeSpan.FromSeconds(10),
                // HTTP/2 多路复用：单连接并发多个请求，进一步省握手
                EnableMultipleHttp2Connections = true
            };
            // 证书容错：镜像源证书有问题时可选放行。
            // SocketsHttpHandler 没有 ServerCertificateCustomValidationCallback，
            // 要走 SslOptions 里的 RemoteCertificateValidationCallback。
            if (settings.IgnoreCertificateErrors)
            {
                handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                };
            }

            if (settings.UseProxy && !string.IsNullOrWhiteSpace(settings.ProxyAddress))
            {
                var address = settings.ProxyAddress.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? settings.ProxyAddress
                    : "http://" + settings.ProxyAddress;
                handler.Proxy = new WebProxy(address);
            }
            // 旧的 HttpClient 不主动释放：可能仍有请求在飞，交给 GC 回收更安全
            _client = new HttpClient(handler, true)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds)),
                // 请求 HTTP/2；服务器不支持会自动协商降级到 1.1，不会报错
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            _client.DefaultRequestHeaders.UserAgent.Clear();
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(settings.UserAgent)
                ? "LPML/" + LPML.Core.BuildInfo.Version
                : settings.UserAgent);
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            _client.DefaultRequestHeaders.ConnectionClose = false;
            _handler = handler;
        }
    }

    /// <summary>影响 HttpClient 构造的设置项签名；没变就不重建。</summary>
    private static string ConfigurationKey(DownloadSettings s) => string.Join("|",
        s.UseProxy, s.ProxyAddress, s.IgnoreCertificateErrors,
        s.TimeoutSeconds, s.UserAgent);

    /// <summary>
    /// 单域连接池上限跟随当前并发目标：不固定 128，也不低到成为瓶颈。
    /// 运行时可写，只影响后续新建连接，已在飞的请求不受影响。
    /// </summary>
    public void ApplyConnectionLimit(int target)
    {
        lock (_sync)
        {
            if (_handler is null) return;
            try
            {
                var limit = Math.Clamp(target, 8, 64);
                if (_handler.MaxConnectionsPerServer != limit)
                    _handler.MaxConnectionsPerServer = limit;
            }
            catch (InvalidOperationException) { /* 某些运行时不允许启动后写，忽略即可 */ }
        }
    }

    // ── DNS 预热 ───────────────────────────────────────────
    // .NET 自身不缓存 DNS，每次建新连接都会解析一遍；先解析一次把 OS 缓存填上，
    // 后续成千上万个小文件就都能命中缓存，省掉解析往返。
    private static readonly Dictionary<string, IPAddress[]> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _warmed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>对一批 URL 的域名做 DNS 预热（并发、只做一次、失败静默）。</summary>
    public static async Task WarmupDnsAsync(IEnumerable<string> urls)
    {
        var hosts = urls
            .Select(u => { try { return new Uri(u).Host; } catch { return null; } })
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(h => !_warmed.Contains(h))
            .Take(8)
            .ToList();
        if (hosts.Count == 0) return;

        foreach (var h in hosts) _warmed.Add(h);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = hosts.Select(async host =>
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                if (addresses.Length > 0)
                    lock (_dnsCache) _dnsCache[host] = addresses;
            }
            catch (Exception)
            {
                // 解析失败不影响下载，只是少了预热收益
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        Log.Info("Download", L.F("String.Core.Log.Download.DnsWarmup",
            string.Join("、", hosts), sw.ElapsedMilliseconds, _dnsCache.Count));
    }

    private HttpClient Client
    {
        get { lock (_sync) return _client; }
    }

    public async Task<string> GetStringAsync(string url, CancellationToken token = default)
        => await RetryAsync(async ct => await Client.GetStringAsync(url, ct).ConfigureAwait(false), url, token);

    public async Task<T?> GetJsonAsync<T>(string url, CancellationToken token = default)
    {
        var text = await GetStringAsync(url, token).ConfigureAwait(false);
        return Json.Deserialize<T>(text);
    }

    public async Task<byte[]> GetBytesAsync(string url, CancellationToken token = default)
        => await RetryAsync(async ct => await Client.GetByteArrayAsync(url, ct).ConfigureAwait(false), url, token);

    /// <summary>
    /// 可选资源（如更新签名文件）的探测：失败立即返回 null，不走重试退避。
    /// 逐个探测多个候选文件名时，用 GetBytesAsync 会被 404 的退避拖慢好几秒。
    /// </summary>
    public async Task<byte[]?> TryGetBytesAsync(string url, CancellationToken token = default)
    {
        try { return await Client.GetByteArrayAsync(url, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task DownloadToFileAsync(string url, string destination,
        IProgress<(long Received, long Total)>? progress = null, CancellationToken token = default,
        Func<int, CancellationToken, Task>? throttle = null)
    {
        await RetryAsync(async ct =>
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // 429/403 给出能看懂的原因，而不是只抛一句英文状态
            if ((int)response.StatusCode == 429)
                throw new HttpRequestException(L.T("String.Core.Err.Http.TooManyRequests"));
            if ((int)response.StatusCode == 403)
            {
                // 带上完整 URL：403 到底是路径不对、构件没同步，还是 WAF 拒绝，
                // 一眼就能分辨，不用再从文件名猜。
                Log.Warn("Download", L.T("String.Core.Err.Http.Forbidden") + " " + url);
                throw new HttpRequestException(L.T("String.Core.Err.Http.Forbidden"));
            }

            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            FileHelper.EnsureDirectory(Path.GetDirectoryName(destination));
            try
            {
                await using var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                // ResponseHeadersRead 之后 HttpClient.Timeout 不再约束 body 读取：
                // 连上却一直不吐字节的请求会永久挂起，必须按「无进展」自己判超时。
                while ((read = await ReadWithStallAsync(stream, buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (throttle is not null) await throttle(read, ct).ConfigureAwait(false);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report((received, total));
                }
                // 旧逻辑把「读循环正常结束」等同于下载成功：连接中途断开、chunked 提前
                // 终止、以及服务器返回 200 却一个字节不吐，都会留下半截甚至 0 字节的
                // jar；第三方加载器库没有 sha1/size，下一轮「跳过已存在文件」会永久放过它。
                if (received == 0)
                    throw new IOException("Empty response body for " + url);
                if (total > 0 && received != total)
                    throw new IOException($"Incomplete body for {url}: expected {total}, got {received}");
            }
            catch
            {
                // 失败的半成品绝不能留在目标路径：否则它会被当成「已存在」的完整文件，
                // 重试还没开始就已经宣告成功，classpath 里永远是这个坏 jar。
                FileHelper.TryDelete(destination);
                throw;
            }
            return true;
        }, url, token).ConfigureAwait(false);
    }

    /// <summary>探测文件大小与是否支持断点续传。</summary>
    public async Task<(long? Length, bool AcceptRanges)> ProbeAsync(string url, CancellationToken token = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return (null, false);
            var length = response.Content.Headers.ContentLength;
            var accept = response.Headers.AcceptRanges.Contains("bytes");
            return (length, accept);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            if (_settings.FriendlySslErrors && IsSslFailure(ex))
                throw new IOException(L.T("String.Core.Err.Ssl"), ex);
            return (null, false);
        }
        catch (TaskCanceledException)
        {
            // HEAD 超时也是源故障：交给外层换源，别静默当成「不支持 Range」
            throw new StalledTransferException(L.F("String.Core.Err.Http.Stalled", StallSeconds));
        }
    }

    /// <summary>是否 SSL / TLS 握手失败。</summary>
    internal static bool IsSslFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException) return true;
            var text = e.Message ?? "";
            if (text.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || text.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                || text.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<Stream> OpenRangeAsync(string url, long start, long? end, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = end.HasValue
            ? new RangeHeaderValue(start, end.Value)
            : new RangeHeaderValue(start, null);

        HttpResponseMessage response;
        try
        {
            // 等响应头由 HttpClient.Timeout 管住；ResponseHeadersRead 之后它不再约束 body，
            // body 交给调用方的 ReadWithStallAsync。这里不引入临时 CTS：返回的流会持有
            // 它的取消注册，提前释放会在后续读取时踩到已释放对象。
            response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StalledTransferException(L.F("String.Core.Err.Http.Stalled", StallSeconds));
        }
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}");
        }
        return await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
    }

    /// <summary>403 / 404 / 429 属于源的问题，必须在换源后重试，不能原地退避。</summary>
    private static bool IsSourceStatusError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var m = e.Message ?? "";
            if (m.Contains("HTTP 403") || m.Contains("(403)") || m.Contains("（403）")
                || m.Contains("HTTP 404") || m.Contains("(404)") || m.Contains("（404）")
                || m.Contains("(429)") || m.Contains("（429）") || m.Contains("HTTP 429"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 主机级故障（DNS 解析失败、SSL 握手失败、连接被拒）：重试同一个地址毫无意义。
    /// 立刻把异常交给外层，由外层熔断该域名并换源。
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

    public async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> action, string url, CancellationToken token = default)
    {
        var attempts = Math.Max(0, _settings.RetryCount) + 1;
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await action(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            // 停滞要交给外层换源，不能在同一个卡死的源上把重试次数耗光
            catch (StalledTransferException) { throw; }
            catch (Exception ex) when (IsHostFailure(ex)) { throw; }
            // 403 / 404 / 429 是「这个源不对」，不是暂时抖动：原地退避重试 4 次
            // 会把限流越撞越狠（外层还会再重试一轮，等于 16 次请求砸同一个域名）。
            // 立刻抛回外层，由 DownloadManager 换到另一个源。
            catch (Exception ex) when (IsSourceStatusError(ex)) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or TimeoutException)
            {
                last = ex;
                if (i == attempts - 1) break;
                // 指数退避 300 / 600 / 1200 … 封顶 8 秒，限流时别反复撞墙
                var delayMs = Math.Min(300 * Math.Pow(2, i), 8000);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), token).ConfigureAwait(false);
            }
        }
        throw last ?? new IOException(L.F("String.Core.Err.Download.Failed", url));
    }

    /// <summary>无进展超时（秒）：整体超时的一半，夹在 5~120 秒之间。</summary>
    private int StallSeconds => Math.Clamp(Math.Max(5, _settings.TimeoutSeconds / 2), 5, 120);

    /// <summary>
    /// 读一次数据，把「超过阈值没收到任何字节」判为源停滞。
    /// 调用方每读到一批数据就会再次进入本方法，计时自然重置——按无进展计时，
    /// 慢但持续在传的大文件不会被误杀。
    /// </summary>
    public async Task<int> ReadWithStallAsync(Stream stream, byte[] buffer, int offset, int count,
        CancellationToken token)
    {
        var seconds = StallSeconds;
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(token);
        guard.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(offset, count), guard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StalledTransferException(L.F("String.Core.Err.Http.Stalled", seconds));
        }
    }
}

/// <summary>
/// 连接已建立（甚至响应头都回来了），但长时间没有收到任何数据。
/// 按源故障处理：外层拉黑该域名并换到下一个镜像，而不是原地重试到超时。
/// </summary>
public sealed class StalledTransferException : IOException
{
    public StalledTransferException(string message) : base(message) { }
}
