using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LPML.Core.Utils;
using LPML.Core.Download;

namespace LPML.Core.Multiplayer;

/// <summary>
/// 联机核心（Core 层）：陶瓦 / EasyTier 组网。
/// 只依赖 BCL + HttpService，不引用任何 WPF 类型，可在纯逻辑测试里直接驱动。
///
/// 设计原则：
///  1) 联机核心作为独立可执行文件放在 &lt;root&gt;\LPML\tools\terracotta\（随启动器一起发布），
///     本类只负责下载、校验、拉起子进程、解析它的 stdout，不在 LPML 内静态链接任何陶瓦代码；
///  2) 房间号是本地生成的随机码（8 位字母数字），仅用于人传人，网络侧是否要邀请码由陶瓦决定；
///  3) 本类不内置任何官方下载地址：工具包地址由设置里的「联机核心地址」给出（可自建镜像），留空则不允许下载。
/// </summary>
public sealed class TerracottaCore : IDisposable
{
    private const string DefaultNetworkName = "lpml-net";
    private const string DefaultNetworkSecret = "lpml-shared";
    private static readonly char[] RoomChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly TerracottaSettings _settings;
    private readonly object _sync = new();

    private Process? _daemon;
    private readonly StringBuilder _daemonOutput = new();
    private readonly List<TerracottaPeer> _peers = new();
    private bool _disposed;

    /// <summary>联机核心目录：&lt;root&gt;\LPML\tools\terracotta。</summary>
    public string ToolDirectory => Path.Combine(_paths.AppDirectory, "tools", "terracotta");

    /// <summary>可执行文件路径（按当前操作系统选名字，是否真实存在以 IsInstalled 为准）。</summary>
    public string ExecutablePath
    {
        get
        {
            var fileName = OperatingSystem.IsWindows()
                ? "terracotta.exe"
                : OperatingSystem.IsOSPlatform("osx")
                    ? "terracotta-macos"
                    : "terracotta-linux";
            return Path.Combine(ToolDirectory, fileName);
        }
    }

    /// <summary>当前是否已安装（可执行文件存在，且 SHA-256 校验通过或未要求校验）。</summary>
    public bool IsInstalled
    {
        get
        {
            if (!File.Exists(ExecutablePath)) return false;
            if (!_settings.VerifyChecksum || string.IsNullOrWhiteSpace(_settings.ExpectedChecksum)) return true;
            try { return HashHelper.Sha256(ExecutablePath).Equals(_settings.ExpectedChecksum, StringComparison.OrdinalIgnoreCase); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>当前核心版本号（由设置保存，安装时写入）。</summary>
    public string InstalledVersion => _settings.InstalledVersion;

    /// <summary>守护进程是否还在运行。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _daemon is not null && !_daemon.HasExited;
        }
    }

    /// <summary>本机虚拟 IP（守护进程上报后更新）。</summary>
    public string? VirtualIp { get; private set; }

    /// <summary>当前已知的对端列表（不含自己）。</summary>
    public IReadOnlyList<TerracottaPeer> Peers
    {
        get { lock (_sync) return _peers.ToList(); }
    }

    /// <summary>房间状态变化：安装状态、运行状态、虚拟 IP、对端列表。</summary>
    public event EventHandler<TerracottaStatus>? StatusChanged;

    /// <summary>守护进程输出一行（含陶瓦自身的日志）。</summary>
    public event EventHandler<string>? OutputLine;

    public TerracottaCore(AppPaths paths, HttpService http, TerracottaSettings settings)
    {
        _paths = paths;
        _http = http;
        _settings = settings;
    }

    // ── 安装 ────────────────────────────────────────────────

    /// <summary>
    /// 下载并安装联机核心。
    /// <paramref name="sourceUrl"/> 为空时使用设置里的地址；仍为空则抛出 InvalidOperationException。
    /// 下载后用 <paramref name="expectedChecksum"/> 做 SHA-256 校验（空字符串 = 不校验）。
    /// </summary>
    public async Task InstallAsync(string? sourceUrl = null, string? expectedChecksum = null,
        IProgress<DownloadAggregate>? progress = null, CancellationToken token = default)
    {
        var url = sourceUrl?.Trim() ?? _settings.PackageUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(L.T("String.Multi.Err.NoPackageUrl"));

        var target = ExecutablePath;
        Directory.CreateDirectory(ToolDirectory);

        // 报告“准备中”，让界面有响应
        progress?.Report(new DownloadAggregate
        {
            Active = 1,
            Total = 1,
            ReceivedBytes = 0,
            TotalBytes = 0,
            BytesPerSecond = 0,
        });

        var tmp = Path.Combine(_paths.TempDirectory, "terracotta_" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        try
        {
            // 现有 HttpService.DownloadToFileAsync 不直接支持 IProgress<DownloadAggregate>，
            // 这里用 (Received, Total) 元组进度桥接，并把 SHA-256 校验放到下载完成后做一次整体核对。
            var bridge = new Progress<(long Received, long Total)>(tuple =>
                progress?.Report(new DownloadAggregate
                {
                    Active = 1,
                    Total = 1,
                    ReceivedBytes = tuple.Received,
                    TotalBytes = tuple.Total,
                    BytesPerSecond = 0,
                }));
            await _http.DownloadToFileAsync(url, tmp, bridge, token).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(expectedChecksum))
            {
                string actual;
                try { actual = HashHelper.Sha256(tmp); }
                catch (IOException ex) { throw new InvalidOperationException(L.T("String.Multi.Err.ChecksumFailed") + " " + ex.Message, ex); }

                if (!actual.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(L.F("String.Multi.Err.ChecksumMismatch", actual, expectedChecksum));
            }

            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            // 原子替换：先写临时名，再移动，避免只写了一半的坏文件被当成“已安装”
            var stage = target + ".part";
            if (File.Exists(stage)) File.Delete(stage);
            File.Move(tmp, stage);
            File.Move(stage, target);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsOSPlatform("osx"))
            {
                try { File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            _settings.InstalledVersion = _settings.LatestKnownVersion ?? L.T("String.Multi.VersionUnknown");
            _settings.ExpectedChecksum = expectedChecksum ?? "";
            _settings.LastUpdatedAt = DateTimeOffset.Now;
            SaveSettings();

            progress?.Report(new DownloadAggregate
            {
                Active = 0,
                Total = 1,
                ReceivedBytes = 1,
                TotalBytes = 1,
                BytesPerSecond = 0,
            });
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>卸载（只删除文件，不停止进程；调用方应先 Stop）。</summary>
    public void Uninstall()
    {
        if (IsRunning) Stop();
        if (File.Exists(ExecutablePath))
        {
            try { File.Delete(ExecutablePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _settings.InstalledVersion = "";
        _settings.ExpectedChecksum = "";
        SaveSettings();
    }

    // ── 房间号 ──────────────────────────────────────────────

    /// <summary>生成本地房间号（8 位，去掉易混的 0/O/1/I）。</summary>
    public static string NormalizeRoomCode(string? code)
    {
        var text = (code ?? "").Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
        return text.Length == 8 ? text : "";
    }

    public static string GenerateRoomCode()
    {
        var random = new Random();
        var buffer = new char[8];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = RoomChars[random.Next(RoomChars.Length)];
        return new string(buffer);
    }

    // ── 守护进程：创建 / 加入 ───────────────────────────────

    /// <summary>启动组网守护进程（加入房间）。创建房间同样用本方法：<paramref name="port"/> 填 0 表示自动分配。</summary>
    public TerracottaSession Start(string roomCode, string? bindAddr = null, int port = 0)
    {
        if (!IsInstalled) throw new InvalidOperationException(L.T("String.Multi.Err.NotInstalled"));
        if (string.IsNullOrWhiteSpace(roomCode)) throw new ArgumentException(L.T("String.Multi.Err.RoomCodeEmpty"), nameof(roomCode));

        Stop();

        lock (_sync)
        {
            _daemonOutput.Clear();
            _peers.Clear();
            VirtualIp = null;
        }

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = ToolDirectory
        };

        // 协议参数（与陶瓦 / EasyTier 对齐）：网络名 + 共享密钥由房间号派生，保证同房间的人拿到同一组参数。
        // 注意：这是“防误连”而不是“加密”——局域网联机本就不走公网，不需要强鉴权。
        startInfo.ArgumentList.Add("--network-name");
        startInfo.ArgumentList.Add(DeriveNetworkName(roomCode));
        startInfo.ArgumentList.Add("--network-secret");
        startInfo.ArgumentList.Add(DeriveNetworkSecret(roomCode));
        startInfo.ArgumentList.Add("--ipv4");
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "10.233.233.1" : "10.233.233.1");

        if (!string.IsNullOrWhiteSpace(bindAddr))
        {
            startInfo.ArgumentList.Add("--bind-addr");
            startInfo.ArgumentList.Add(bindAddr.Trim());
        }

        if (port > 0)
        {
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(port.ToString());
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        process.Exited += (_, _) => OnExited(process.ExitCode);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_sync) _daemon = process;

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

    /// <summary>停止组网守护进程。</summary>
    public void Stop()
    {
        Process? target;
        lock (_sync) { target = _daemon; _daemon = null; }

        if (target is null) return;

        try
        {
            // 先给一次优雅退出机会（多数组网工具收到空行/quit 即退出）
            if (target.StartInfo.RedirectStandardInput && target.HasExited == false)
            {
                try { target.StandardInput.WriteLine("quit"); target.StandardInput.Flush(); }
                catch (IOException) { }
                target.WaitForExit(1500);
            }
        }
        catch (InvalidOperationException) { }

        if (!target.HasExited)
        {
            try { target.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        try { target.Dispose(); }
        catch (InvalidOperationException) { }

        lock (_sync) _peers.Clear();
        VirtualIp = null;
        RaiseStatus();
    }

    private void OnOutput(string line)
    {
        lock (_sync) _daemonOutput.AppendLine(line);
        OutputLine?.Invoke(this, line);

        if (TryParseVirtualIp(line, out var ip))
        {
            VirtualIp = ip;
            RaiseStatus();
        }

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
        lock (_sync)
        {
            if (ReferenceEquals(_daemon, null) == false) _daemon = null;
        }
        VirtualIp = null;
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

    // ── 行解析（可被单测直接调用） ──────────────────────────

    /// <summary>从一行输出里尝试解析虚拟 IP（ipconfig / ifconfig 风格 + 工具自定义格式）。</summary>
    public static bool TryParseVirtualIp(string line, out string ip)
    {
        ip = "";

        // 常见输出：tun0: inet 10.233.233.1  netmask 255.255.255.0
        var match = System.Text.RegularExpressions.Regex.Match(line, @"inet\s+(\d{1,3}(?:\.\d{1,3}){3})");
        if (match.Success)
        {
            var candidate = match.Groups[1].Value;
            if (IsPrivateOrVirtual(candidate))
            {
                ip = candidate;
                return true;
            }
        }

        // 工具自定义：VIRTUAL_IP=10.233.233.1 或 virtual ip: 10.233.233.1
        match = System.Text.RegularExpressions.Regex.Match(line, @"(?:virtual[_\s]?ip|vip)\s*[:=]\s*(\d{1,3}(?:\.\d{1,3}){3})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            ip = match.Groups[1].Value;
            return true;
        }

        return false;
    }

    /// <summary>从一行输出里尝试解析对端信息（没有标准格式，这里只认常见的 peer / node 行）。</summary>
    public static TerracottaPeer? TryParsePeer(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains("peer", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("node", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("client", StringComparison.OrdinalIgnoreCase))
            return null;

        var ipMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"(\d{1,3}(?:\.\d{1,3}){3})");
        if (!ipMatch.Success) return null;

        var ip = ipMatch.Groups[1].Value;
        if (!IsPrivateOrVirtual(ip)) return null;

        var keyMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"([0-9a-fA-F]{16,64})");
        var publicKey = keyMatch.Success ? keyMatch.Groups[1].Value : ip;

        return new TerracottaPeer
        {
            Ip = ip,
            PublicKey = publicKey,
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

        // 10.x.x.x / 172.16-31.x.x / 192.168.x.x / 169.254.x.x（link-local）/ 127.x.x.x
        return a == 10 ||
               (a == 172 && b is >= 16 and <= 31) ||
               (a == 192 && b == 168) ||
               (a == 169 && b == 254) ||
               a == 127;
    }

    private static string DeriveNetworkName(string roomCode)
        => DefaultNetworkName + "-" + roomCode.Trim().ToUpperInvariant();

    private static string DeriveNetworkSecret(string roomCode)
    {
        // 房间号本身只是人传人用的短码，把它做一次哈希再截断，避免把明文当密钥写进日志。
        var hash = HashHelper.Sha256(DefaultNetworkSecret + ":" + roomCode.Trim().ToUpperInvariant());
        return hash.Length > 32 ? hash[..32] : hash;
    }

    // ── 持久化 ──────────────────────────────────────────────

    private void SaveSettings()
    {
        try { Json.Save(_paths.SettingsFile, _settings); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── IDisposable ─────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
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
    /// <summary>创建房间：本机作为首个节点，端口自动分配。</summary>
    Create,

    /// <summary>加入房间：连接到已有节点。</summary>
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
    /// <summary>联机核心包下载地址（用户填写，可自建镜像；留空则禁止下载）。</summary>
    public string PackageUrl { get; set; } = "";

    /// <summary>已安装的核心版本号。</summary>
    public string InstalledVersion { get; set; } = "";

    /// <summary>安装包预期 SHA-256（空 = 不校验）。</summary>
    public string ExpectedChecksum { get; set; } = "";

    /// <summary>是否校验 SHA-256（默认开）。</summary>
    public bool VerifyChecksum { get; set; } = true;

    /// <summary>已知最新版本号（由界面从服务端查询后回填）。</summary>
    public string? LatestKnownVersion { get; set; }

    /// <summary>上次更新时间。</summary>
    public DateTimeOffset LastUpdatedAt { get; set; }

    /// <summary>上次使用的房间号（不自动回显给用户，仅用于“恢复会话”提示）。</summary>
    public string LastRoomCode { get; set; } = "";

    /// <summary>邀请码 → 房主共享地址（本机保存，不联网传播）。</summary>
    public Dictionary<string, string> KnownRooms { get; set; } = new();

    /// <summary>自建 / 私有的邀请码发现服务地址；留空则只使用本机已保存的映射。{code} 会被替换为邀请码。</summary>
    public string? DiscoveryUrl { get; set; }

    /// <summary>上次会话模式。</summary>
    public TerracottaSessionMode LastMode { get; set; } = TerracottaSessionMode.Join;
}
