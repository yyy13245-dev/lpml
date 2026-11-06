using System.Diagnostics;
using System.IO;
using System.Windows;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Instances;
using LPML.Core.Launch;

namespace LPML.App.Services;

/// <summary>运行中的游戏会话（给界面列表用，不持有进程对象）。</summary>
public sealed class GameSessionInfo
{
    public string Id { get; set; } = "";
    public string InstanceName { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public int Pid { get; set; }
    public string DetailText => "PID " + Pid;
}

/// <summary>
/// 全局游戏会话：允许多个实例同时运行，每个会话单独登记、单独结束。
/// 界面上的「结束游戏」展开板列出全部会话，选中哪个就结束哪个。
/// PID 记在配置目录，启动器重启后也能认出"上次启动的游戏还开着"。
/// </summary>
public sealed class GameSessionService
{
    private const string StateFileName = "running-game.json";

    private sealed class Session
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string InstanceName = "";
        public string InstanceId = "";
        public GameProcess? Owned;      // 本次启动器启动的
        public Process? External;       // 上一次启动器启动、本进程只做监视的
        public int Pid;
        public long StartTicks;
        public bool KilledByUser;
    }

    private readonly object _sync = new();
    private readonly string _stateFile;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _killedFlags = new(StringComparer.Ordinal);

    /// <summary>最近一次结束是不是用户主动点的，供旧的全局判断兼容使用。</summary>
    private bool _lastKilledByUser;

    public GameSessionService(LauncherContext context)
    {
        _stateFile = Path.Combine(context.Paths.ConfigDirectory, StateFileName);
    }

    /// <summary>是否至少有一个游戏在跑。</summary>
    public bool IsRunning => Snapshot().Count > 0;

    /// <summary>保留兼容：现在允许多个实例并存，启动按钮不再因为"忙"而置灰。</summary>
    public bool IsBusy => false;

    /// <summary>兼容旧的全局判断。</summary>
    public bool KilledByUser { get { lock (_sync) return _lastKilledByUser; } }

    /// <summary>兼容旧的全局判断：第一个运行中实例的名字。</summary>
    public string InstanceName => Snapshot().FirstOrDefault()?.InstanceName ?? "";

    /// <summary>正在运行的会话数量。</summary>
    public int RunningCount => Snapshot().Count;

    /// <summary>运行状态变化（true = 还有游戏在跑）。事件已封送到 UI 线程。</summary>
    public event EventHandler<bool>? RunningChanged;

    /// <summary>当前运行中的会话快照；顺带清掉已经退出的会话。</summary>
    public List<GameSessionInfo> Snapshot()
    {
        List<GameSessionInfo> result;
        var changed = false;
        lock (_sync)
        {
            foreach (var dead in _sessions.Values.Where(s => !Alive(s)).ToList())
            {
                _sessions.Remove(dead.Id);
                changed = true;
            }
            result = _sessions.Values.Select(s => new GameSessionInfo
            {
                Id = s.Id,
                InstanceName = s.InstanceName,
                InstanceId = s.InstanceId,
                Pid = s.Pid
            }).ToList();
        }
        if (changed) WriteState();
        return result;
    }

    private static bool Alive(Session s)
    {
        try
        {
            if (s.Owned is not null) return s.Owned.IsRunning;
            if (s.External is not null) return !s.External.HasExited;
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>
    /// 保留旧签名以兼容其它启动入口。现在恒返回 true：
    /// 允许同时启动第二个、第三个实例，不再做全局占位拦截。
    /// </summary>
    public bool TryBeginLaunch() => true;

    /// <summary>保留旧签名：不再需要释放占位。</summary>
    public void CancelLaunch() { }

    /// <summary>保留旧签名：不再阻止启动，只提示当前有几个实例在跑。</summary>
    public void NotifyAlreadyRunning()
    {
        var count = RunningCount;
        if (count <= 0) return;
        var title = Lang.T("String.Msg.Game.AlreadyRunningTitle");
        var message = Lang.F("String.Msg.Game.RunningCount", count);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => Notice.Info(title, message));
        else
            Notice.Info(title, message);
    }

    /// <summary>启动成功后登记进程，返回会话 Id（结束某个实例时要用它）。</summary>
    public string Register(GameProcess process, GameInstance instance)
    {
        var s = new Session
        {
            InstanceName = instance.Name,
            InstanceId = instance.Id,
            Owned = process
        };

        try { s.Pid = process.ProcessId ?? 0; }
        catch (Exception) { }
        if (s.Pid > 0)
        {
            try
            {
                using var native = Process.GetProcessById(s.Pid);
                s.StartTicks = native.StartTime.Ticks;
            }
            catch (Exception) { /* 拿不到启动时间就退化为只认 PID + 进程名 */ }
        }

        lock (_sync)
        {
            _sessions[s.Id] = s;
            _killedFlags[s.Id] = false;
            _lastKilledByUser = false;
        }

        process.Exited += (sender, args) => OnGameExited(s.Id);
        WriteState();
        Raise(true);
        return s.Id;
    }

    /// <summary>某个会话的退出是否由用户主动结束造成，避免误报崩溃。</summary>
    public bool WasKilledByUser(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;
        lock (_sync) return _killedFlags.TryGetValue(sessionId, out var v) && v;
    }

    /// <summary>结束指定的一个会话（含子进程），不影响其它正在运行的实例。</summary>
    public bool Kill(string sessionId)
    {
        Session? s;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out s)) return false;
            s.KilledByUser = true;
            _killedFlags[sessionId] = true;
            _lastKilledByUser = true;
        }

        var killed = false;
        if (s.Owned is not null)
        {
            try { s.Owned.Kill(); killed = true; }
            catch (Exception ex) { Log.Warn("Launch", Lang.F("String.Msg.Game.KillFailed", ex.Message)); }
        }
        else if (s.External is not null)
        {
            try { s.External.Kill(true); killed = true; }
            catch (Exception ex) { Log.Warn("Launch", Lang.F("String.Msg.Game.KillFailed", ex.Message)); }
        }

        Log.Info("Launch", Lang.F("String.Msg.Game.SessionClosed", s.InstanceName));
        lock (_sync) _sessions.Remove(sessionId);
        WriteState();
        Raise(Snapshot().Count > 0);
        return killed;
    }

    /// <summary>兼容旧的"关闭游戏"入口：结束当前全部会话。</summary>
    public bool KillRunning()
    {
        var ids = Snapshot().Select(x => x.Id).ToList();
        var killed = false;
        foreach (var id in ids) killed |= Kill(id);
        return killed;
    }

    private void OnGameExited(string sessionId)
    {
        lock (_sync) _sessions.Remove(sessionId);
        WriteState();
        Raise(Snapshot().Count > 0);
    }

    /// <summary>
    /// 启动器重启后恢复状态：逐行读上次写下的 PID，进程还活着且确实是 java/javaw 就认为游戏在跑。
    /// 只认 java 系进程，避免 PID 被系统复用后误杀别的程序。
    /// 文件按行存，兼容旧版单行三段格式。
    /// </summary>
    public void Restore()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var lines = File.ReadAllLines(_stateFile);
            var restored = 0;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('|');
                if (parts.Length < 1 || !int.TryParse(parts[0], out var pid) || pid <= 0) continue;

                try
                {
                    var process = Process.GetProcessById(pid);
                    var name = process.ProcessName;
                    if (!name.StartsWith("java", StringComparison.OrdinalIgnoreCase) || process.HasExited) continue;

                    // PID 可能被系统复用：对一下启动时间，不是同一个进程就当状态过期
                    long ticks = 0;
                    if (parts.Length > 1 && long.TryParse(parts[1], out ticks) && ticks > 0
                        && process.StartTime.Ticks != ticks) continue;

                    var s = new Session
                    {
                        InstanceName = parts.Length > 2 ? parts[2] : "",
                        InstanceId = parts.Length > 3 ? parts[3] : "",
                        External = process,
                        Pid = pid,
                        StartTicks = ticks
                    };

                    process.EnableRaisingEvents = true;
                    process.Exited += (sender, args) => OnGameExited(s.Id);
                    lock (_sync)
                    {
                        _sessions[s.Id] = s;
                        _killedFlags[s.Id] = false;
                    }
                    restored++;
                    Log.Info("Launch", Lang.F("String.Msg.Game.Restored", pid, s.InstanceName));
                }
                catch (Exception) { /* 单个 PID 过期或取不到信息，跳过即可 */ }
            }

            if (restored == 0) { DeleteState(); return; }
            Raise(true);
        }
        catch (Exception)
        {
            DeleteState();
        }
    }

    /// <summary>把所有还在跑的会话写回状态文件（每行一条）。</summary>
    private void WriteState()
    {
        try
        {
            List<Session> alive;
            lock (_sync) alive = _sessions.Values.Where(Alive).ToList();
            var text = string.Join(Environment.NewLine, alive.Select(s =>
                string.Join("|", s.Pid, s.StartTicks, s.InstanceName.Replace("|", " "),
                    s.InstanceId.Replace("|", " "), s.Id)));
            File.WriteAllText(_stateFile, text);
        }
        catch (IOException ex) { Log.Warn("Launch", Lang.F("String.Msg.Game.StateFailed", ex.Message)); }
        catch (UnauthorizedAccessException ex) { Log.Warn("Launch", Lang.F("String.Msg.Game.StateFailed", ex.Message)); }
    }

    private void DeleteState()
    {
        try { if (File.Exists(_stateFile)) File.Delete(_stateFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Raise(bool running)
    {
        var handler = RunningChanged;
        if (handler is null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => handler(this, running));
        else
            handler(this, running);
    }
}
