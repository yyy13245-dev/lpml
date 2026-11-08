using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using LPML.Core.Download;
using LPML.Core.Utils;
using LPML.Core;

namespace LPML.Core.Multiplayer;

/// <summary>
/// 联机核心（Core 层）：陶瓦联机 Terracotta / EasyTier。
///
/// 与 v0.4.2 官方集成方式一致（HMCL 同款）：
///  1) 下载未经修改的官方发行包（默认路线），解压出可执行文件放到
///     &lt;root&gt;\LPML\tools\terracotta\；本类不静态/动态链接任何陶瓦代码；
///  2) 直接拉起未经修改的官方二进制，不向它传递任何私有启动参数；
///     本启动器不主动打开浏览器，HTTP 服务只监听 127.0.0.1；端口优先探固定 8000，
///     再走 terracotta.lock / 端口 JSON / stdout；
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
    // 首次打开联机页会预热，用户也可能同时点创建/加入；没有这把锁会拉起两个陶瓦进程抢 8000 端口。
    private readonly SemaphoreSlim _daemonGate = new(1, 1);

    private Process? _daemon;
    // 只有本启动器亲手拉起的进程才可以结束；复用 HMCL 的核心时不能替它关掉。
    private bool _ownsDaemon;
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

    /// <summary>
    /// 是否真的开着房间。预热 / 待机不算——否则首次打开联机页自动唤醒核心后，
    /// 用户一离开页面就会被问「房间还开着，确定离开吗」。
    /// </summary>
    public bool HasActiveRoom => StateKind is
        TerracottaStateKind.HostScanning or TerracottaStateKind.HostStarting or TerracottaStateKind.HostOk or
        TerracottaStateKind.GuestConnecting or TerracottaStateKind.GuestStarting or TerracottaStateKind.GuestOk;

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
    /// 启动联机核心守护进程（不传任何私有启动参数）。
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

    /// <summary>确保守护进程在跑并已发现端口；已运行则直接复用。并发调用会排队，不会重复拉起进程。</summary>
    public void EnsureDaemon()
    {
        _daemonGate.Wait();
        try { EnsureDaemonCore(); }
        finally { _daemonGate.Release(); }
    }

    /// <summary>
    /// 界面「尝试重新启动联机核心」按钮调用：先结束本启动器拉起的残留进程，
    /// 再完整走一遍 EnsureDaemon。失败会抛出由调用方显示，状态落到 Exception。
    /// 复用外部（例如 HMCL 的）核心时不会去结束别人的进程，只会重新探测并复用。
    /// </summary>
    public async Task RestartAsync(CancellationToken token = default)
    {
        Log.Info("Multi", "用户请求重新启动联机核心");
        bool owned;
        lock (_sync) owned = _ownsDaemon;

        // Stop 要抢守护进程锁（预热 / 端口发现可能正占着），放线程池别冻界面。
        await Task.Run(Stop, token).ConfigureAwait(false);

        // 刚被结束的进程可能还占着 8000：等端口释放，否则 EnsureDaemon 会把
        // 这个垂死的端口当成「已有核心」复用，轮询就挂在一个死地址上。
        if (owned)
        {
            var deadline = Environment.TickCount64 + 1500;
            while (Environment.TickCount64 < deadline &&
                   IsLoopbackOpen(TerracottaPlatform.DefaultApiPort))
                await Task.Delay(100, token).ConfigureAwait(false);
        }

        await Task.Run(EnsureDaemon, token).ConfigureAwait(false);
        Log.Info("Multi", $"联机核心重新启动完成：127.0.0.1:{_apiPort}");
    }

    private void EnsureDaemonCore()
    {
        if (IsRunning && _apiPort > 0) return;

        // 从创建/加入页返回入口页时页面会重建，旧的 core 实例连同守护进程一起被丢下。
        // 若 8000 上已经有一个陶瓦（本启动器上次留下的，或 HMCL 开的），直接复用，
        // 不要再拉第二个进程——否则端口占用会让新进程起不来，界面就一直报通讯失败。
        var existingPort = FindExistingApiPort();
        if (existingPort > 0)
        {
            _apiPort = existingPort;
            _ownsDaemon = false;
            Log.Info("Multi", $"检测到已有联机核心在运行，复用 127.0.0.1:{_apiPort}");
            StartPolling();
            RaiseStatus();
            return;
        }

        if (IsRunning) StopInternal();
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
        VirtualIp = null;

        // 本地状态：从拉起进程到 /state 就绪之前，界面显示「核心启动中」，
        // 不再让用户面对一段没有任何反馈的空白等待。
        RaiseLocalState(TerracottaStateKind.CoreStarting);
        Log.Info("Multi", $"启动联机核心：{ExecutablePath}（默认端口 {TerracottaPlatform.DefaultApiPort}）");

        // 直接启动官方二进制，不传任何私有启动参数。本启动器自身绝不打开浏览器；
        // 若陶瓦自行打开网页界面，用户可在应用内或该页面里继续操作（房间页有说明）。
        var process = StartProcess();
        if (process.HasExited)
        {
            Log.Warn("Multi",
                $"联机核心启动后很快退出，未重复拉起进程。输出尾部：{OutputTail(8)}");
            // 多半是锁文件冲突：若本机已有陶瓦服务，直接复用，不再拉第二个进程。
            var runningPort = FindExistingApiPort();
            if (runningPort > 0)
            {
                _apiPort = runningPort;
                _ownsDaemon = false;
                lock (_sync) _daemon = null;
                RaiseLocalState(TerracottaStateKind.Waiting);
                StartPolling();
                Log.Info("Multi", $"已有联机核心在运行，改为复用 127.0.0.1:{_apiPort}");
                return;
            }
            StopInternal();
            var reason = L.T("String.Multi.Err.DaemonStartFailed");
            RaiseLocalState(TerracottaStateKind.Exception, reason);
            throw new InvalidOperationException(reason);
        }

        // 官方在 waiting / scanning 状态 10 秒无活动会自动退出。端口发现必须
        // 在这个窗口内完成：先探固定端口，再走文件/输出发现，总超时 8 秒。
        _apiPort = WaitForPort(8_000);
        if (_apiPort <= 0)
        {
            Log.Warn("Multi",
                $"联机核心端口发现失败（8 秒）。进程存活={!process.HasExited}；输出尾部：{OutputTail(12)}");
            StopInternal();
            RaiseLocalState(TerracottaStateKind.Exception, L.T("String.Multi.Err.PortNotFound"));
            throw new InvalidOperationException(L.T("String.Multi.Err.PortNotFound"));
        }

        Log.Info("Multi", $"联机核心本地服务已就绪：127.0.0.1:{_apiPort}");
        // 先落到待机，随后第一次 /state 轮询会给出官方真实状态。
        RaiseLocalState(TerracottaStateKind.Waiting);
        StartPolling();
    }

    private Process StartProcess()
    {
        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = ToolDirectory
        };
        // 不传任何私有启动参数：陶瓦是否自行打开网页由它决定，
        // 本启动器只通过本地 HTTP API 交互，绝不主动调用浏览器。
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        process.Exited += (_, _) => OnExited(process.ExitCode);

        if (!process.Start())
        {
            KillQuietly(process);
            Log.Error("Multi", "联机核心进程启动失败（Process.Start 返回 false）");
            throw new InvalidOperationException(L.T("String.Multi.Err.DaemonStartFailed"));
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_sync) { _daemon = process; _ownsDaemon = true; }

        // 只给 600ms 判断「启动即退出」，正常情况会等满这 600ms。
        process.WaitForExit(600);
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { process.Dispose(); }
        catch (InvalidOperationException) { }
    }

    private string CapturedOutput()
    {
        lock (_sync) return _daemonOutput.ToString();
    }

    private string OutputTail(int lines)
    {
        var all = CapturedOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var take = all.Length > lines ? all[(all.Length - lines)..] : all;
        return take.Length == 0 ? "（无输出）" : string.Join(" | ", take.Select(s => s.Trim()));
    }

    /// <summary>
    /// 创建房间（房主）。不传固定邀请码时由陶瓦生成，随后从 /state 的 room 字段读取。
    /// </summary>
    public async Task CreateRoomAsync(string? fixedRoomCode, string playerName,
        int port = 0, CancellationToken token = default)
    {
        // 端口发现是同步等待（最多 8 秒），放线程池跑，别把界面冻住。
        await Task.Run(EnsureDaemon, token).ConfigureAwait(false);
        var normalized = TerracottaRoomCode.Normalize(fixedRoomCode);
        if (!string.IsNullOrWhiteSpace(fixedRoomCode) && normalized.Length == 0)
            throw new ArgumentException(L.T("String.Multi.Err.CodeInvalid"), nameof(fixedRoomCode));

        // 官方 /state/scanning 不接受 room 参数，房间码由陶瓦自己生成后从 /state 读回。
        if (!string.IsNullOrWhiteSpace(fixedRoomCode))
            Log.Warn("Multi", "官方 /state/scanning 不接受固定邀请码，本次房间码由陶瓦生成");
        // 端口从游戏日志里读出来后，自己按官方 HTTP API 拼请求，不依赖任何启动器的私有协议。
        // 官方构建若忽略 port 参数，会自行扫描局域网广播，不影响开房。
        if (port is > 0 and <= 65535)
            Log.Info("Multi", $"使用游戏日志识别到的端口：{port}");
        var url = BuildStateUrl("scanning", "", playerName, port);
        Log.Info("Multi", $"创建房间请求：{url}");

        using var response = await _api.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodyAsync(response, token).ConfigureAwait(false);
            Log.Warn("Multi", $"创建房间被拒：HTTP {(int)response.StatusCode}，响应={body}");
            throw new InvalidOperationException(L.F("String.Multi.Err.ApiFailed", (int)response.StatusCode));
        }
        Log.Info("Multi", "创建房间请求已接受，等待 /state 进入 hosting（需游戏已「对局域网开放」）");
        RaiseLocalState(TerracottaStateKind.HostScanning);
    }

    /// <summary>加入房间。邀请码使用官方 U/ 格式，与 HMCL / PCL CE / FCL 互通。</summary>
    public async Task JoinRoomAsync(string roomCode, string playerName,
        CancellationToken token = default)
    {
        var normalized = TerracottaRoomCode.Normalize(roomCode);
        if (normalized.Length == 0)
            throw new ArgumentException(L.T("String.Multi.Err.CodeInvalid"), nameof(roomCode));

        await Task.Run(EnsureDaemon, token).ConfigureAwait(false);
        var url = BuildStateUrl("guesting", normalized, playerName);
        Log.Info("Multi", $"加入房间请求：{url}");

        using var response = await _api.GetAsync(url, token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var body = await ReadBodyAsync(response, token).ConfigureAwait(false);
            Log.Warn("Multi", $"邀请码被联机核心拒绝（HTTP 400）：{normalized}，响应={body}");
            throw new InvalidOperationException(L.T("String.Multi.Err.CodeRejected"));
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodyAsync(response, token).ConfigureAwait(false);
            Log.Warn("Multi", $"加入房间被拒：HTTP {(int)response.StatusCode}，响应={body}");
            throw new InvalidOperationException(L.F("String.Multi.Err.ApiFailed", (int)response.StatusCode));
        }

        Log.Info("Multi", $"加入房间请求已接受：{normalized}");
        _settings.LastRoomCode = normalized;
        SaveSettings();
        RaiseLocalState(TerracottaStateKind.GuestConnecting);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? "（空）" : Shorten(text.Trim(), 300);
        }
        catch (HttpRequestException) { return "（读取失败）"; }
        catch (OperationCanceledException) { return "（已取消）"; }
    }

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>回到待机状态（GET /state/ide，端点拼写来自官方 v0.4.2 文档）。</summary>
    public async Task ResetAsync(CancellationToken token = default)
    {
        if (_apiPort <= 0) return;
        try
        {
            using var response = await _api.GetAsync($"http://127.0.0.1:{_apiPort}/state/ide", token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { Log.Warn("Multi", $"复位联机状态失败：{ex.Message}"); }
        RoomCode = null;
        GuestUrl = null;
        RaiseLocalState(TerracottaStateKind.Waiting);
    }

    /// <summary>
    /// 离开 / 关闭房间：先走官方 /state/ide 复位状态机；
    /// 只有本启动器亲手拉起的核心才结束进程，复用 HMCL 的只复位、不替别人关掉。
    /// 返回按钮与「关闭房间 / 退出房间」按钮都走这里。
    /// </summary>
    public async Task ExitRoomAsync(CancellationToken token = default)
    {
        try { await ResetAsync(token).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("Multi", $"退出房间时复位状态失败：{ex.GetType().Name}: {ex.Message}"); }

        if (_ownsDaemon)
        {
            Log.Info("Multi", "关闭房间：结束本启动器拉起的联机核心");
            // Stop 内部要抢守护进程锁，预热 / 启动可能占着，放线程池别冻界面。
            await Task.Run(Stop).ConfigureAwait(false);
        }
        else
        {
            Log.Info("Multi", "退出房间：复用外部联机核心，只复位状态，不结束该进程");
            RoomCode = null;
            GuestUrl = null;
            VirtualIp = null;
            RaiseLocalState(TerracottaStateKind.Waiting);
        }
    }

    private string BuildStateUrl(string action, string room, string playerName, int port = 0)
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
        if (port is > 0 and <= 65535)
            Add("port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // v0.4.2 支持自定义公共节点；可重复传，与内置节点合并。
        foreach (var node in _settings.CustomPublicNodes ?? new List<string>())
            Add("public_nodes", node);
        return builder.ToString();
    }

    /// <summary>停止组网守护进程：先走官方优雅退出，再兜底结束进程树。</summary>
    public void Stop()
    {
        _daemonGate.Wait();
        try { StopInternal(); }
        finally { _daemonGate.Release(); }
    }

    // 供 EnsureDaemonCore 在已持有 _daemonGate 时调用，避免自锁。
    private void StopInternal()
    {
        if (_apiPort > 0 || IsRunning) Log.Info("Multi", "正在停止联机核心");
        _pollCts?.Cancel();
        _pollCts = null;

        // /panic 会结束整个核心进程。复用 HMCL 的核心时不能发，否则会把对方的联机也关掉。
        if (_apiPort > 0 && _ownsDaemon)
        {
            try
            {
                _ = _api.GetAsync($"http://127.0.0.1:{_apiPort}/panic?peaceful=true");
            }
            catch (HttpRequestException) { }
            catch (ObjectDisposedException) { }
        }

        Process? target;
        lock (_sync) { target = _daemon; _daemon = null; _ownsDaemon = false; }
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
        RaiseLocalState(TerracottaStateKind.Waiting);
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
            // 官方 web server 固定在 8000，先探它；以前只扫 lock/JSON/stdout，
            // 而官方并不保证写这些文件，于是 12 秒都发现不了端口，
            // 等超过官方 10 秒自动退出窗口后必然报「没能连上联机核心的本地服务」。
            var candidates = new List<int> { TerracottaPlatform.DefaultApiPort };
            candidates.AddRange(DiscoverPorts(dataDir));

            foreach (var candidate in candidates)
            {
                if (candidate is <= 0 or >= 65536 || !IsLoopbackOpen(candidate)) continue;
                if (candidate == TerracottaPlatform.DefaultApiPort && !IsTerracottaApi(candidate)) continue;
                return candidate;
            }
            if (!IsRunning) break;
            Thread.Sleep(120);
        }
        return 0;
    }

    /// <summary>固定端口 + 锁文件 / 端口 JSON 里所有候选，找出真正活着的陶瓦服务。</summary>
    private int FindExistingApiPort()
    {
        var candidates = new List<int> { TerracottaPlatform.DefaultApiPort };
        candidates.AddRange(DiscoverPorts(TerracottaPlatform.DataDirectory()));
        foreach (var port in candidates.Distinct())
        {
            if (port is <= 0 or >= 65536 || !IsLoopbackOpen(port)) continue;
            if (!IsTerracottaApi(port)) continue;
            return port;
        }
        return 0;
    }

    /// <summary>用 GET /state 确认端口上确实是陶瓦，而不是别的程序。</summary>
    private bool IsTerracottaApi(int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var text = _api.GetStringAsync($"http://127.0.0.1:{port}/state", cts.Token)
                .GetAwaiter().GetResult();
            return text.Contains("\"state\"", StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) { return false; }
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
        {
            Log.Warn("Multi", "联机核心本地服务未就绪（端口 0），请求被拒绝");
            throw new InvalidOperationException(L.T("String.Multi.Err.NotRunning"));
        }
    }

    // ── 状态轮询 ────────────────────────────────────────────

    private void StartPolling()
    {
        _pollCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        var port = _apiPort;
        _ = Task.Run(async () =>
        {
            var failures = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var json = await _api.GetStringAsync($"http://127.0.0.1:{port}/state", cts.Token)
                        .ConfigureAwait(false);
                    if (failures > 0)
                    {
                        Log.Info("Multi", "已恢复与联机核心的通讯");
                        failures = 0;
                    }
                    var snapshot = TerracottaStateParser.Parse(json);
                    ApplyState(snapshot);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpRequestException ex)
                {
                    failures++;
                    if (failures is 1 or 5 or 10)
                        Log.Warn("Multi", $"读取 /state 失败（第 {failures} 次）：{ex.Message}");
                }
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

        var previousKind = StateKind;
        StateKind = snapshot.Kind;
        if (previousKind != snapshot.Kind)
            Log.Info("Multi", $"联机状态：{previousKind} → {snapshot.Kind}（raw={snapshot.RawState}）");
        if (snapshot.Kind == TerracottaStateKind.Exception)
            Log.Warn("Multi", $"联机核心报告异常：{Shorten(snapshot.Error ?? snapshot.RawState, 300)}");
        else if (snapshot.Kind == TerracottaStateKind.Unknown && !string.IsNullOrWhiteSpace(snapshot.Error))
            Log.Warn("Multi", $"无法解析 /state 响应：{Shorten(snapshot.Error, 300)}");
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
        // 联机核心的原始输出以前只进界面，不进 lpml.log，排查时一条都看不到。
        if (!string.IsNullOrWhiteSpace(line)) Log.Info("Multi", "[core] " + Shorten(line.Trim(), 300));
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
        lock (_sync) { _daemon = null; _ownsDaemon = false; }
        _apiPort = 0;
        _pollCts?.Cancel();
        VirtualIp = null;
        RoomCode = null;
        GuestUrl = null;
        lock (_sync) _peers.Clear();
        RaiseLocalState(TerracottaStateKind.Waiting);
        Log.Warn("Multi", $"联机核心进程退出（代码 {exitCode}）。输出尾部：{OutputTail(8)}");
        OutputLine?.Invoke(this, L.F("String.Multi.DaemonExited", exitCode));
    }

    /// <summary>
    /// 发布一个本地状态（不由 /state 上报）：核心启动中、停止后回到待机、
    /// 以及创建/加入请求刚被接受时的即时反馈。同时刷新状态栏与所有子页。
    /// </summary>
    private void RaiseLocalState(TerracottaStateKind kind, string? error = null)
    {
        StateKind = kind;
        StateUpdated?.Invoke(this, new TerracottaStateSnapshot(
            kind, LocalStateName(kind), 0, RoomCode, GuestUrl,
            Array.Empty<TerracottaProfile>(), error));
        RaiseStatus();
    }

    private static string LocalStateName(TerracottaStateKind kind) => kind switch
    {
        TerracottaStateKind.CoreStarting => "core-starting",
        TerracottaStateKind.Waiting => "waiting",
        TerracottaStateKind.HostScanning => "scanning",
        TerracottaStateKind.GuestConnecting => "guesting",
        TerracottaStateKind.Exception => "exception",
        _ => kind.ToString().ToLowerInvariant()
    };

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
