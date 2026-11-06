using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using LPML.Core;

namespace LPML.App.Guard;

/// <summary>
/// 守护进程主体。同一个 exe 带 --guard 启动，由主进程 Process.Start 创建，
/// 因此任务管理器的「进程」页会把两个 LPML 折叠在同一组里。
///
/// 它做两件事：
///   ① 日志接管：主进程就绪后把日志帧通过管道发来，由它统一写入 lpml.log；
///   ② 看门狗：主进程 UI 心跳停止超过阈值时，提示并强制关闭卡死的启动器。
///
/// 不创建 WPF Application、不加载任何界面资源，启动极快且不会被界面崩溃拖累。
/// </summary>
public static class GuardProcess
{
    private static readonly object FileLock = new();
    private static StreamWriter? _writer;
    private static readonly List<string> _pending = new();
    private static bool _ready;
    private static bool IsZh = true;

    private static DateTime _lastHeartbeatUtc = DateTime.UtcNow;
    private static DateTime _snoozeUntilUtc = DateTime.MinValue;
    private static volatile bool _heartbeatConnected;
    private static volatile bool _handling;

    // ── 退出原因判定 ───────────────────────────────────────
    private static volatile bool _logConnected;      // 主进程连上过日志管道吗
    private static volatile bool _normalShutdown;    // 收到过主进程的退出帧吗
    private static volatile int _shutdownReasonValue = (int)GuardExitReason.Unknown;
    private static volatile bool _killedByGuard;     // 看门狗主动杀过主进程吗

    public static int Run(string[] args)
    {
        var session = GuardOptions.Value(args, "session") ?? "";
        var pid = GuardOptions.IntValue(args, "pid", 0);
        var logPath = GuardOptions.Value(args, "log") ?? "";
        var timeout = Math.Clamp(GuardOptions.IntValue(args, "timeout", 20), 5, 300);
        var autoKill = GuardOptions.IntValue(args, "autokill", 0) == 1;
        IsZh = !string.Equals(GuardOptions.Value(args, "guard-lang"), "en", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(session) || pid <= 0 || string.IsNullOrWhiteSpace(logPath)) return 2;

        Process? host = null;
        try { host = Process.GetProcessById(pid); }
        catch (ArgumentException) { return 3; }   // 主进程已经没了

        try
        {
            FileHelperEnsure(logPath);
            // 和主进程共用同一套编码（UTF-8 + BOM），否则记事本会把中文按 ANSI 猜成乱码
            _writer = new StreamWriter(
                new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                FileLogSink.LogEncoding)
            {
                AutoFlush = true
            };
        }
        catch (IOException) { return 4; }
        catch (UnauthorizedAccessException) { return 4; }

        Self("Guard", IsZh
            ? $"守护进程已启动（主进程 {pid}，会话 {session}）"
            : $"Guard started (host {pid}, session {session})");

        using var done = new CancellationTokenSource();
        NamedPipeServerStream? logServer = null;
        NamedPipeServerStream? hbServer = null;

        try
        {
            logServer = new NamedPipeServerStream(GuardProtocol.LogPipe(session),
                PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            hbServer = new NamedPipeServerStream(GuardProtocol.HeartbeatPipe(session),
                PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            _ = Task.Run(() => HeartbeatLoop(hbServer, done.Token));
            _ = Task.Run(() => WatchdogLoop(host, timeout, autoKill, done.Token));
            _ = Task.Run(() => HostAliveLoop(host, logServer, done.Token));

            // 主进程没连上（启动极早期就崩了、或被 --no-guard 之类绕开）时不能一直挂着，
            // 否则会变成任务管理器里删不掉的残留进程。
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var connectToken = CancellationTokenSource.CreateLinkedTokenSource(
                done.Token, connectTimeout.Token);
            logServer.WaitForConnectionAsync(connectToken.Token).GetAwaiter().GetResult();
            _logConnected = true;

            // 回 READY 才算「完全启动」。主进程收到它之后才会把落盘目标切到管道，
            // 所以 READY 之前主进程写文件、守护进程不写文件，两边不会抢同一行。
            logServer.WriteByte(GuardProtocol.Ready);
            logServer.Flush();
            lock (FileLock)
            {
                _ready = true;
                foreach (var line in _pending) _writer?.WriteLine(line);
                _pending.Clear();
                _writer?.Flush();
            }

            using var reader = new BinaryReader(logServer);
            while (!done.IsCancellationRequested)
            {
                LogEntry? entry;
                GuardExitReason reason;
                byte kind;
                try { kind = GuardProtocol.ReadFrame(reader, out entry, out reason); }
                catch (EndOfStreamException) { break; }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }

                if (kind == GuardProtocol.FrameShutdown)
                {
                    _normalShutdown = true;
                    _shutdownReasonValue = (int)reason;
                    break;
                }
                if (kind != GuardProtocol.FrameEntry || entry is null) continue;

                lock (FileLock)
                {
                    try { _writer?.WriteLine(entry.ToString()); }
                    catch (IOException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Self("Guard", (IsZh ? "守护进程异常：" : "Guard error: ") + ex.Message);
        }
        finally
        {
            done.Cancel();
            try { logServer?.Dispose(); } catch { }
            try { hbServer?.Dispose(); } catch { }

            // 所有退出路径都在这里汇合，原因只记一次、位置固定在最后两行。
            var exitReason = ResolveExitReason();
            var exitWhy = GuardExitReasonText.Describe(exitReason, IsZh);
            // 正常退出时主进程往往还没真正结束，ExitCode 取不到，就别写个难看的「?」；
            // 异常结束（外部杀 / 崩溃 / 看门狗）才带退出码。
            var code = exitReason is GuardExitReason.ExternalTermination or GuardExitReason.KilledByGuard
                ? HostExitCode(host)
                : null;
            Self("Guard", IsZh
                ? (code is null ? $"启动器退出原因：{exitWhy}" : $"启动器退出原因：{exitWhy}（退出码 {code}）")
                : (code is null ? $"Launcher exit reason: {exitWhy}" : $"Launcher exit reason: {exitWhy} (exit code {code})"));
            Self("Guard", IsZh ? "守护进程退出" : "Guard exiting");
            lock (FileLock)
            {
                // 没等到 READY 就退出的情况（连接超时等），把攒着的日志直接写出去
                if (_pending.Count > 0 && _writer is not null)
                {
                    foreach (var line in _pending)
                    {
                        try { _writer.WriteLine(line); } catch (IOException) { }
                    }
                    _pending.Clear();
                }
                try { _writer?.Flush(); } catch { }
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
        return 0;
    }

    /// <summary>读心跳管道。主进程 UI 卡住时会故意停发，这里就再也读不到数据。</summary>
    private static void HeartbeatLoop(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
            _lastHeartbeatUtc = DateTime.UtcNow;
            _heartbeatConnected = true;
            using var reader = new BinaryReader(server);
            while (!ct.IsCancellationRequested)
            {
                GuardProtocol.ReadHeartbeat(reader);
                _lastHeartbeatUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 主进程还活着吗？它退出后我们不残留。
    /// 正常情况下管道 EOF 会让读循环结束；万一 EOF 没来（比如进程被强杀），
    /// 等 2 秒给缓冲里的日志留时间，再主动关管道打断同步读。
    /// </summary>
    private static void HostAliveLoop(Process host, NamedPipeServerStream logServer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Task.Delay(1000, ct).GetAwaiter().GetResult();
                if (ct.IsCancellationRequested) break;
                if (!host.HasExited) continue;

                Task.Delay(2000, ct).GetAwaiter().GetResult();
                if (ct.IsCancellationRequested) break;
                try { logServer.Dispose(); } catch { }
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (SystemException) { }
    }

    private static void WatchdogLoop(Process host, int timeout, bool autoKill, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Task.Delay(1000, ct).GetAwaiter().GetResult();
                if (ct.IsCancellationRequested) break;

                // 日志还没接管、心跳通道还没连上（启动段）都不判定，避免误杀
                if (!_ready || !_heartbeatConnected) continue;
                if (DateTime.UtcNow < _snoozeUntilUtc) continue;
                if (host.HasExited) break;

                var silent = DateTime.UtcNow - _lastHeartbeatUtc;
                if (silent.TotalSeconds < timeout) continue;
                if (_handling) continue;

                _handling = true;
                try { HandleUnresponsive(host, (int)silent.TotalSeconds, timeout, autoKill); }
                finally { _handling = false; }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        catch (SystemException) { }
    }

    private static void HandleUnresponsive(Process host, int silentSeconds, int timeout, bool autoKill)
    {
        Self("Guard", IsZh
            ? $"启动器已 {silentSeconds} 秒没有响应（阈值 {timeout} 秒）"
            : $"Launcher unresponsive for {silentSeconds}s (threshold {timeout}s)");

        var kill = autoKill;
        if (!kill)
        {
            var caption = IsZh ? "LPML 守护进程" : "LPML Guard";
            var text = IsZh
                ? $"启动器已经 {silentSeconds} 秒没有响应了。\n\n要强制关闭它吗？\n选「是」立即结束启动器；选「否」继续等待。"
                : $"The launcher has not responded for {silentSeconds} seconds.\n\nForce close it?\nYes = close now, No = keep waiting.";
            kill = AskOnStaThread(text, caption);
        }

        if (!kill)
        {
            // 用户选择继续等待：给一个完整的宽限期，不反复弹窗
            _lastHeartbeatUtc = DateTime.UtcNow;
            _snoozeUntilUtc = DateTime.UtcNow.AddSeconds(timeout);
            Self("Guard", IsZh ? "用户选择继续等待" : "User chose to keep waiting");
            return;
        }

        _killedByGuard = true;
        Self("Guard", IsZh ? "正在强制关闭未响应的启动器" : "Killing unresponsive launcher");
        try { host.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    /// <summary>
    /// 判定主进程为什么没了。优先级：没连上 &gt; 收到正常退出帧 &gt; 看门狗强杀 &gt; 外部结束/崩溃。
    /// </summary>
    private static GuardExitReason ResolveExitReason()
    {
        if (!_logConnected) return GuardExitReason.NeverConnected;
        if (_normalShutdown) return (GuardExitReason)_shutdownReasonValue;
        if (_killedByGuard) return GuardExitReason.KilledByGuard;
        return GuardExitReason.ExternalTermination;
    }

    private static string HostExitCode(Process? host)
    {
        if (host is null) return "?";
        try { return host.ExitCode.ToString(); }
        catch (InvalidOperationException) { return "?"; }
        catch (System.ComponentModel.Win32Exception) { return "?"; }
        catch (NotSupportedException) { return "?"; }
    }

    /// <summary>守护进程自己的日志。READY 之前先攒着，避免和主进程抢写同一文件。</summary>
    private static void Self(string source, string message)
    {
        var line = new LogEntry(DateTime.Now, LogLevel.Info, source, message, 0).ToString();
        lock (FileLock)
        {
            if (_ready && _writer is not null)
            {
                try { _writer.WriteLine(line); } catch (IOException) { }
            }
            else _pending.Add(line);
        }
    }

    private static void FileHelperEnsure(string logPath)
    {
        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MbYesNo = 0x00000004;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbTopMost = 0x00040000;
    private const uint MbSetForeground = 0x00010000;
    private const int IdYes = 6;

    /// <summary>MessageBox 要跑在 STA 线程上；Task.Run 的线程池线程是 MTA。</summary>
    private static bool AskOnStaThread(string text, string caption)
    {
        var yes = false;
        try
        {
            var thread = new Thread(() => yes = AskYesNo(text, caption));
            if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
        }
        catch (Exception) { return false; }
        return yes;
    }

    private static bool AskYesNo(string text, string caption)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return MessageBoxW(IntPtr.Zero, text, caption,
                MbYesNo | MbIconWarning | MbTopMost | MbSetForeground) == IdYes;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
