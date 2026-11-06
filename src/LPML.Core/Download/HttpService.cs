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
                ? "LPML/1.0"
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
                throw new HttpRequestException(L.T("String.Core.Err.Http.Forbidden"));

            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            FileHelper.EnsureDirectory(Path.GetDirectoryName(destination));
            await using var fs = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (throttle is not null) await throttle(read, ct).ConfigureAwait(false);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report((received, total));
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
        catch (TaskCanceledException) { return (null, false); }
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
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = end.HasValue
            ? new RangeHeaderValue(start, end.Value)
            : new RangeHeaderValue(start, null);
        var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} for {url}");
        }
        return await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
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
            catch (Exception ex) when (IsHostFailure(ex)) { throw; }
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
}
