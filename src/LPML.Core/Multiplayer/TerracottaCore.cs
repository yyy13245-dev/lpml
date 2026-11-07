using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.Core.Multiplayer;

/// <summary>
/// 联机核心（Core 层）：陶瓦联机 Terracotta / EasyTier。
///
/// 与 v0.4.2 官方集成方式一致（HMCL 同款）：
///  1) 下载未经修改的官方发行包（默认路线），解压出可执行文件放到
///     &lt;root&gt;\LPML\tools\terracotta\；本类不静态/动态链接任何陶瓦代码；
///  2) 以 <c>--hmcl</c> 集成模式拉起子进程，HTTP 服务只监听 127.0.0.1，
///     端口由系统随机分配，通过 terracotta.lock / 端口 JSON / stdout 发现；
///  3) 全部操作走本地 REST API：/state、/state/scanning、/state/guesting、
///     /state/ide、/meta、/log、/panic?peaceful=true；
///  4) 邀请码使用官方 U/NNNN-NNNN-SSSS-SSSS 码制，因此与 HMCL / PCL CE / FCL 互通。
///
/// 版权：Terracotta | 陶瓦联机 © Burning_TNT 及贡献者，AGPL-3.0-or-later（含例外条款）。
/// 本启动器通过进程间通信接口与未经修改的官方程序交互，界面上有明显版权标注。
/// </summary>
public sealed class TerracottaCore : IDisposable
{
    private static readonly Regex PortInJson = new(@"""port""\s*:\s*(\d{2,5})", RegexOptions.Compiled);
    private static readonly Regex PortInText = new(@"\bport\s*[:=]\s*(\d{2,5})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly TerracottaSettings _settings;
    private readonly object _sync = new();

    private Process? _daemon;
    private readonly StringBuilder _daemonOutput = new();
    private readonly List<TerracottaPeer> _peers = new();
    private readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(3) };
    private CancellationTokenSource? _pollCts;
    private int _apiPort;
    private long _lastStateIndex = -1;
    private bool _disposed;

    /// <summary>联机核心目录：&lt;root&gt;\LPML\tools\terracotta。</summary>
    public string ToolDirectory => Path.Combine(_paths.AppDirectory, "tools", "terracotta");

    /// <summary>可执行文件路径（安装后由默认路线落盘）。</summary>
    public string ExecutablePath
    {
        get
        {
            var fileName = OperatingSystem.IsWindows()
                ? "terracotta.exe"
                : OperatingSystem.IsMacOS() ? "terracotta-macos" : "terracotta-linux";
            return Path.Combine(ToolDirectory, fileName);
        }
    }

    /// <summary>当前是否已安装（可执行文件存在，且校验通过或未要求校验）。</summary>
    public bool IsInstalled
    {
        get
        {
            if (!File.Exists(ExecutablePath)) return false;
            if (!_settings.VerifyChecksum || string.IsNullOrWhiteSpace(_settings.ExpectedChecksum)) return true;
            try
            {
                var actual = _settings.ExpectedChecksum.Trim().Length == 64
                    ? HashHelper.Sha256(ExecutablePath)
                    : Sha512Of(ExecutablePath);
                return actual.Equals(_settings.ExpectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public string InstalledVersion => _settings.InstalledVersion;

    public bool IsRunning
    {
        get { lock (_sync) return _daemon is not null && !_daemon.HasExited; }
    }

    /// <summary>本机 HTTP API 端口（0 = 尚未发现）。</summary>
    public int ApiPort => _apiPort;

    /// <summary>陶瓦上报的当前邀请码（host-starting 及以上状态）。</summary>
    public string? RoomCode { get; private set; }

    /// <summary>加入成功后可用的地址（guest-ok 的 url 字段）。</summary>
    public string? GuestUrl { get; private set; }

    /// <summary>当前房间状态。</summary>
    public TerracottaStateKind StateKind { get; private set; } = TerracottaStateKind.Waiting;

    public string? VirtualIp { get; private set; }

    public IReadOnlyList<TerracottaPeer> Peers
    {
        get { lock (_sync) return _peers.ToList(); }
    }

    public event EventHandler<TerracottaStatus>? StatusChanged;
    public event EventHandler<string>? OutputLine;
    public event EventHandler<string>? RoomCodeChanged;
    public event EventHandler<TerracottaStateSnapshot>? StateUpdated;

    public TerracottaCore(AppPaths paths, HttpService http, TerracottaSettings settings)
    {
        _paths = paths;
        _http = http;
        _settings = settings;
    }

    // ── 安装 ────────────────────────────────────────────────

    /// <summary>
    /// 安装联机核心。未指定下载地址时走默认路线（与 HMCL 相同的镜像与元数据）。
    /// <paramref name="expectedChecksum"/> 支持 SHA-512（128 位）与旧配置的 SHA-256（64 位）。
    /// </summary>
    public async Task InstallAsync(string? sourceUrl = null, string? expectedChecksum = null,
        IProgress<DownloadAggregate>? progress = null, CancellationToken token = default)
    {
        if (IsRunning) Stop();

        progress?.Report(new DownloadAggregate
        {
            Active = 1, Total = 1, ReceivedBytes = 0, TotalBytes = 0, BytesPerSecond = 0
        });

        var bridge = new Progress<(long Received, long Total)>(tuple =>
            progress?.Report(new DownloadAggregate
            {
                Active = 1,
                Total = 1,
                ReceivedBytes = tuple.Received,
                TotalBytes = tuple.Total,
                BytesPerSecond = 0
            }));

        var installer = new TerracottaPackage(_paths, _http, _settings)
        {
            Log = line =>
            {
                OutputLine?.Invoke(this, line);
                LPML.Core.Log.Info("Multi", line);
            }
        };

        var result = await installer.InstallAsync(sourceUrl, expectedChecksum, bridge, token).ConfigureAwait(false);
        SaveSettings();

        progress?.Report(new DownloadAggregate
        {
            Active = 0, Total = 1, ReceivedBytes = 1, TotalBytes = 1, BytesPerSecond = 0
        });
        RaiseStatus();
        OutputLine?.Invoke(this, L.F("String.Multi.InstalledFrom", result.Version, result.SourceUrl));
    }

    /// <summary>卸载（只删除文件，不停止进程；调用方应先 Stop）。</summary>
    public void Uninstall()
    {
        if (IsRunning) Stop();
        foreach (var file in new[] { ExecutablePath, Path.Combine(ToolDirectory, "VCRUNTIME140.DLL") })
        {
            if (!File.Exists(file)) continue;
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _settings.InstalledVersion = "";
        _settings.ExpectedChecksum = "";
        SaveSettings();
        RaiseStatus();
    }

    /// <summary>读取 GET /meta，用于确认核心版本与平台。</summary>
    public async Task<string> GetMetaAsync(CancellationToken token = default)
    {
        EnsurePort();
        return await _api.GetStringAsync($"http://127.0.0.1:{_apiPort}/meta", token).ConfigureAwait(false);
    }

    // ── 房间号 ──────────────────────────────────────────────

    /// <summary>归一化为官方 U/XXXX-XXXX-XXXX-XXXX 格式；格式不对返回空串。</summary>
    public static string NormalizeRoomCode(string? code) => TerracottaRoomCode.Normalize(code);

    /// <summary>生成官方格式的邀请码（用于固定房间码）。</summary>
    public static string GenerateOfficialRoomCode() => TerracottaRoomCode.Generate();

    /// <summary>
    /// 本地 8 位短码（旧版遗留，只用于「人传人」的备注，不参与陶瓦组网）。
    /// 保留是为了兼容旧配置与已有调用；新流程一律使用 GenerateOfficialRoomCode。
    /// </summary>
    public static string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var buffer = new char[8];
        for (var i = 0; i < buffer.Length; i++) buffer[i] = chars[Random.Shared.Next(chars.Length)];
        return new string(buffer);
    }

    // ── 守护进程 ────────────────────────────────────────────

    /// <summary>
    /// 启动集成模式守护进程（<c>--hmcl</c>）。
    /// 兼容旧签名：roomCode 仅作为会话备注，port &lt;= 0 记为创建模式。
    /// 真正的开房/加入请调用 CreateRoomAsync / JoinRoomAsync。
    /// </summary>
    public TerracottaSession Start(string roomCode, string? bindAddr = null, int port = 0)
    {
        if (!IsInstalled) throw new InvalidOperationException(L.T("String.Multi.Err.NotInstalled"));
        if (string.IsNullOrWhiteSpace(roomCode))
            throw new ArgumentException(L.T("String.Multi.Err.RoomCodeEmpty"), nameof(roomCode));

        Stop();
        EnsureDaemon();

        var session = new TerracottaSession
        {
            RoomCode = roomCode,
            StartedAt = DateTimeOffset.Now,
            Mode = port <= 0 ? TerracottaSessionMode.Create : TerracottaSessionMode.Join,
            BindAddress = bindAddr,
            Port = port
        };
        SaveSettings();
        RaiseStatus();
        return session;
    }

    /// <summary>确保守护进程在跑并已发现端口；已运行则直接复用。</summary>
    public void EnsureDaemon()
    {
        if (IsRunning && _apiPort > 0) return;
        if (IsRunning) Stop();
        if (!IsInstalled) throw new InvalidOperationException(L.T("String.Multi.Err.NotInstalled"));

        lock (_sync)
        {
            _daemonOutput.Clear();
            _peers.Clear();
        }
        _apiPort = 0;
        _lastStateIndex = -1;
        RoomCode = null;
        GuestUrl = null;
        StateKind = TerracottaStateKind.Waiting;
        VirtualIp = null;

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = ToolDirectory
        };
        startInfo.ArgumentList.Add("--hmcl");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        process.Exited += (_, _) => OnExited(process.ExitCode);

        if (!process.Start())
            throw new InvalidOperationException(L.T("String.Multi.Err.DaemonStartFailed"));
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_sync) _daemon = process;

        _apiPort = WaitForPort(12_000);
        if (_apiPort <= 0)
        {
            Stop();
            throw new InvalidOperationException(L.T("String.Multi.Err.PortNotFound"));
        }

        StartPolling();
        RaiseStatus();
    }

    /// <summary>
    /// 创建房间（房主）。不传固定邀请码时由陶瓦生成，随后从 /state 的 room 字段读取。
    /// </summary>
    public async Task CreateRoomAsync(string? fixedRoomCode, string playerName,
        CancellationToken token = default)
    {
        EnsureDaemon();
        var normalized = TerracottaRoomCode.Normalize(fixedRoomCode);
        if (!string.IsNullOrWhiteSpace(fixedRoomCode) && normalized.Length == 0)
            throw new ArgumentException(L.T("String.Multi.Err.CodeInvalid"), nameof(fixedRoomCode));

        var url = BuildStateUrl("scanning", normalized, playerName);
        using var response = await _api.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(L.F("String.Multi.Err.ApiFailed", (int)response.StatusCode));
        StateKind = TerracottaStateKind.HostScanning;
        RaiseStatus();
    }

    /// <summary>加入房间。邀请码使用官方 U/ 格式，与 HMCL / PCL CE / FCL 互通。</summary>
    public async Task JoinRoomAsync(string roomCode, string playerName,
        CancellationToken token = default)
    {
        var normalized = TerracottaRoomCode.Normalize(roomCode);
        if (normalized.Length == 0)
            throw new ArgumentException(L.T("String.Multi.Err.CodeInvalid"), nameof(roomCode));

        EnsureDaemon();
        var url = BuildStateUrl("guesting", normalized, playerName);
        using var response = await _api.GetAsync(url, token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            throw new InvalidOperationException(L.T("String.Multi.Err.CodeRejected"));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(L.F("String.Multi.Err.ApiFailed", (int)response.StatusCode));

        _settings.LastRoomCode = normalized;
        SaveSettings();
        StateKind = TerracottaStateKind.GuestConnecting;
        RaiseStatus();
    }

    /// <summary>回到待机状态（GET /state/ide，端点拼写来自官方 v0.4.2 文档）。</summary>
    public async Task ResetAsync(CancellationToken token = default)
    {
        if (_apiPort <= 0) return;
        try
        {
            using var response = await _api.GetAsync($"http://127.0.0.1:{_apiPort}/state/ide", token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException) { }
        RoomCode = null;
        GuestUrl = null;
        StateKind = TerracottaStateKind.Waiting;
        RaiseStatus();
    }

    private string BuildStateUrl(string action, string room, string playerName)
    {
        var builder = new StringBuilder($"http://127.0.0.1:{_apiPort}/state/{action}");
        var first = true;
        void Add(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            builder.Append(first ? '?' : '&');
            builder.Append(key).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        Add("room", room);
        Add("player", string.IsNullOrWhiteSpace(playerName)
            ? (_settings.PlayerName ?? "").Trim() : playerName.Trim());

        // v0.4.2 支持自定义公共节点；可重复传，与内置节点合并。
        foreach (var node in _settings.CustomPublicNodes ?? new List<string>())
            Add("public_nodes", node);
        return builder.ToString();
    }

    /// <summary>停止组网守护进程：先走官方优雅退出，再兜底结束进程树。</summary>
    public void Stop()
    {
        _pollCts?.Cancel();
        _pollCts = null;

        if (_apiPort > 0)
        {
            try
            {
                _ = _api.GetAsync($"http://127.0.0.1:{_apiPort}/panic?peaceful=true");
            }
            catch (HttpRequestException) { }
            catch (ObjectDisposedException) { }
        }

        Process? target;
        lock (_sync) { target = _daemon; _daemon = null; }
        _apiPort = 0;

        if (target is not null)
        {
            try
            {
                if (!target.HasExited)
                {
                    try { target.StandardInput.WriteLine("quit"); target.StandardInput.Flush(); }
                    catch (IOException) { }
                    if (!target.WaitForExit(1200)) target.Kill(true);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { target.Dispose(); }
            catch (InvalidOperationException) { }
        }

        lock (_sync) _peers.Clear();
        RoomCode = null;
        GuestUrl = null;
        VirtualIp = null;
        StateKind = TerracottaStateKind.Waiting;
        RaiseStatus();
    }

    // ── 端口发现 ────────────────────────────────────────────

    /// <summary>
    /// 端口由系统随机分配，官方提供三种发现方式，这里全部兼容：
    ///  ① 数据目录下含 {"port":N} 的 JSON（先 .tmp 再改名，读到的一定完整）
    ///  ② terracotta.lock：2 字节大端端口
    ///  ③ 进程 stdout 里的 port 提示
    /// 找到后用 TCP 连一次回环确认可用，避免误读残留文件。
    /// </summary>
    private int WaitForPort(int timeoutMs)
    {
        var dataDir = TerracottaPlatform.DataDirectory();
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            foreach (var candidate in DiscoverPorts(dataDir))
            {
                if (candidate is > 0 and < 65536 && IsLoopbackOpen(candidate)) return candidate;
            }
            if (!IsRunning) break;
            Thread.Sleep(120);
        }
        return 0;
    }

    private List<int> DiscoverPorts(string dataDir)
    {
        // 不用 yield：带 catch 的 try 块里不允许 yield（CS1626），改为先收集再返回。
        var result = new List<int>();

        // ② 锁文件：2 字节大端
        try
        {
            var lockPath = TerracottaPlatform.LockFilePath;
            if (File.Exists(lockPath))
            {
                var bytes = File.ReadAllBytes(lockPath);
                if (bytes.Length >= 2) result.Add((bytes[0] << 8) | bytes[1]);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // ① 端口 JSON（文件名由官方决定，扫描数据目录里的小文件即可）
        try
        {
            if (Directory.Exists(dataDir))
            {
                foreach (var file in Directory.EnumerateFiles(dataDir))
                {
                    if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file);
                    if (info.Length is <= 0 or > 4096) continue;
                    var text = File.ReadAllText(file);
                    var match = PortInJson.Match(text);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var port)) result.Add(port);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // ③ stdout
        string captured;
        lock (_sync) captured = _daemonOutput.ToString();
        foreach (Match match in PortInJson.Matches(captured))
            if (int.TryParse(match.Groups[1].Value, out var p)) result.Add(p);
        foreach (Match match in PortInText.Matches(captured))
            if (int.TryParse(match.Groups[1].Value, out var p)) result.Add(p);

        return result;
    }

    private static bool IsLoopbackOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(250)) && client.Connected;
        }
        catch (SocketException) { return false; }
        catch (AggregateException) { return false; }
    }

    private void EnsurePort()
    {
        if (_apiPort <= 0)
            throw new InvalidOperationException(L.T("String.Multi.Err.NotRunning"));
    }

    // ── 状态轮询 ────────────────────────────────────────────

    private void StartPolling()
    {
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        var port = _apiPort;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var json = await _api.GetStringAsync($"http://127.0.0.1:{port}/state", cts.Token)
                        .ConfigureAwait(false);
                    var snapshot = TerracottaStateParser.Parse(json);
                    ApplyState(snapshot);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpRequestException) { /* 核心正在启动，下一轮再试 */ }
                catch (ObjectDisposedException) { break; }
                try { await Task.Delay(500, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, cts.Token);
    }

    private void ApplyState(TerracottaStateSnapshot snapshot)
    {
        // index 单调递增；忽略重复响应，避免重复刷新界面。
        if (snapshot.Index > 0 && snapshot.Index <= _lastStateIndex) return;
        if (snapshot.Index > 0) _lastStateIndex = snapshot.Index;

        StateKind = snapshot.Kind;
        if (!string.IsNullOrWhiteSpace(snapshot.RoomCode) && snapshot.RoomCode != RoomCode)
        {
            RoomCode = snapshot.RoomCode;
            _settings.LastRoomCode = RoomCode;
            SaveSettings();
            RoomCodeChanged?.Invoke(this, RoomCode);
        }
        if (!string.IsNullOrWhiteSpace(snapshot.Url)) GuestUrl = snapshot.Url;

        if (snapshot.Profiles.Count > 0)
        {
            lock (_sync)
            {
                _peers.Clear();
                foreach (var p in snapshot.Profiles)
                    _peers.Add(new TerracottaPeer
                    {
                        Ip = "", PublicKey = p.MachineId, ConnectedAt = DateTimeOffset.Now,
                        Raw = $"{p.Name} · {p.Vendor} · {p.Kind}"
                    });
            }
        }

        StateUpdated?.Invoke(this, snapshot);
        RaiseStatus();
    }

    // ── 进程输出 ────────────────────────────────────────────

    private void OnOutput(string line)
    {
        lock (_sync) _daemonOutput.AppendLine(line);
        OutputLine?.Invoke(this, line);

        if (TryParseVirtualIp(line, out var ip)) { VirtualIp = ip; RaiseStatus(); }
        var peer = TryParsePeer(line);
        if (peer is not null)
        {
            lock (_sync)
            {
                _peers.RemoveAll(p => p.PublicKey == peer.PublicKey || p.Ip == peer.Ip);
                _peers.Add(peer);
            }
            RaiseStatus();
        }
    }

    private void OnExited(int exitCode)
    {
        lock (_sync) _daemon = null;
        _apiPort = 0;
        _pollCts?.Cancel();
        VirtualIp = null;
        RoomCode = null;
        GuestUrl = null;
        StateKind = TerracottaStateKind.Waiting;
        lock (_sync) _peers.Clear();
        RaiseStatus();
        OutputLine?.Invoke(this, L.F("String.Multi.DaemonExited", exitCode));
    }

    private void RaiseStatus()
    {
        TerracottaStatus snapshot;
        lock (_sync)
        {
            snapshot = new TerracottaStatus(
                Installed: IsInstalled,
                InstalledVersion: InstalledVersion,
                Running: IsRunning,
                VirtualIp: VirtualIp,
                Peers: _peers.ToList());
        }
        StatusChanged?.Invoke(this, snapshot);
    }

    // ── 行解析（保留，供单测与日志兼容） ─────────────────────

    public static bool TryParseVirtualIp(string line, out string ip)
    {
        ip = "";
        var match = Regex.Match(line, @"inet\s+(\d{1,3}(?:\.\d{1,3}){3})");
        if (match.Success && IsPrivateOrVirtual(match.Groups[1].Value))
        {
            ip = match.Groups[1].Value;
            return true;
        }
        match = Regex.Match(line, @"(?:virtual[_\s]?ip|vip)\s*[:=]\s*(\d{1,3}(?:\.\d{1,3}){3})",
            RegexOptions.IgnoreCase);
        if (match.Success) { ip = match.Groups[1].Value; return true; }
        return false;
    }

    public static TerracottaPeer? TryParsePeer(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains("peer", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("node", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("client", StringComparison.OrdinalIgnoreCase))
            return null;

        var ipMatch = Regex.Match(trimmed, @"(\d{1,3}(?:\.\d{1,3}){3})");
        if (!ipMatch.Success || !IsPrivateOrVirtual(ipMatch.Groups[1].Value)) return null;

        var ip = ipMatch.Groups[1].Value;
        var keyMatch = Regex.Match(trimmed, @"([0-9a-fA-F]{16,64})");
        return new TerracottaPeer
        {
            Ip = ip,
            PublicKey = keyMatch.Success ? keyMatch.Groups[1].Value : ip,
            ConnectedAt = DateTimeOffset.Now,
            Raw = trimmed
        };
    }

    private static bool IsPrivateOrVirtual(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b)) return false;
        return a == 10 ||
               (a == 172 && b is >= 16 and <= 31) ||
               (a == 192 && b == 168) ||
               (a == 169 && b == 254) ||
               a == 127;
    }

    private static string Sha512Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(stream));
    }

    private void SaveSettings()
    {
        try { Json.Save(_paths.SettingsFile, _settings); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _api.Dispose();
    }
}

/// <summary>一次组网会话（创建房间或加入房间）的只读快照。</summary>
public sealed record TerracottaSession
{
    public string RoomCode { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public TerracottaSessionMode Mode { get; init; }
    public string? BindAddress { get; init; }
    public int Port { get; init; }
}

public enum TerracottaSessionMode
{
    Create,
    Join
}

/// <summary>联机核心状态快照（线程安全：属性都是只读的）。</summary>
public sealed record TerracottaStatus(
    bool Installed,
    string InstalledVersion,
    bool Running,
    string? VirtualIp,
    IReadOnlyList<TerracottaPeer> Peers);

/// <summary>组网对端。</summary>
public sealed record TerracottaPeer
{
    public string Ip { get; init; } = "";
    public string PublicKey { get; init; } = "";
    public DateTimeOffset ConnectedAt { get; init; }
    public string Raw { get; init; } = "";
}

/// <summary>联机核心的设置（随 launcher.json 持久化）。</summary>
public sealed class TerracottaSettings
{
    /// <summary>联机核心包下载地址（留空 = 走默认路线；可自建镜像或填模板）。</summary>
    public string PackageUrl { get; set; } = "";

    /// <summary>版本覆盖（留空 = 用在线元数据的 version_latest，再回退内置 0.4.2）。</summary>
    public string VersionOverride { get; set; } = "";

    /// <summary>是否尝试拉取 HMCL 同款 terracotta.json 元数据（默认开；失败自动回退内置）。</summary>
    public bool UseOnlineMetadata { get; set; } = true;

    /// <summary>复用 HMCL 已下载的核心目录（填目录路径；留空 = 自己下载）。</summary>
    public string? ExternalCorePath { get; set; }

    /// <summary>附加的 EasyTier 公共节点（v0.4.2 起支持，可重复，与内置节点合并）。</summary>
    public List<string> CustomPublicNodes { get; set; } = new();

    /// <summary>房间成员列表里显示的名字（留空则由界面传当前账户名）。</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>固定房间码（留空 = 每次由陶瓦生成）。须为 U/XXXX-XXXX-XXXX-XXXX。</summary>
    public string FixedRoomCode { get; set; } = "";

    /// <summary>已安装的核心版本号。</summary>
    public string InstalledVersion { get; set; } = "";

    /// <summary>安装包预期校验和（SHA-512，兼容旧的 SHA-256）。空 = 不校验。</summary>
    public string ExpectedChecksum { get; set; } = "";

    /// <summary>是否校验校验和（默认开）。</summary>
    public bool VerifyChecksum { get; set; } = true;

    public string? LatestKnownVersion { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }

    /// <summary>上次使用的邀请码。</summary>
    public string LastRoomCode { get; set; } = "";

    /// <summary>旧版：邀请码 → 房主共享地址（保留兼容，新流程不再依赖）。</summary>
    public Dictionary<string, string> KnownRooms { get; set; } = new();

    /// <summary>旧版：自建发现服务地址（保留兼容）。</summary>
    public string? DiscoveryUrl { get; set; }

    public TerracottaSessionMode LastMode { get; set; } = TerracottaSessionMode.Join;
}
