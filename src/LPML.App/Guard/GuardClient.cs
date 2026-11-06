using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using LPML.Core;

namespace LPML.App.Guard;

/// <summary>
/// 主进程侧的守护进程客户端。
/// 时序：启动守护进程 → 期间主进程自己写日志文件（一条不丢）→ 守护进程回 READY
/// → 主进程把落盘目标整体切到管道 → 之后全部由守护进程写文件。
/// 另外按 UI 线程的心跳判断启动器是否卡死，心跳停了守护进程会处理。
/// </summary>
public sealed class GuardClient
{
    public static GuardClient? Current { get; private set; }

    private readonly string _session = Guid.NewGuid().ToString("N")[..12];
    private readonly int _pid = Environment.ProcessId;
    private readonly GuardOptions _options;
    private readonly string _logPath;
    private readonly BlockingCollection<LogEntry> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _pipeLock = new();

    private NamedPipeClientStream? _logPipe;
    private NamedPipeClientStream? _hbPipe;
    private BinaryWriter? _logWriter;
    private BinaryWriter? _hbWriter;
    private Process? _guardProcess;
    private PipeLogSink? _sink;
    private long _lastPulse;
    private volatile bool _uiHeartbeatEnabled;
    private volatile bool _stopped;

    private GuardClient(string logPath, GuardOptions options)
    {
        _logPath = logPath;
        _options = options;
        _lastPulse = Environment.TickCount64;
    }

    /// <summary>启动守护进程。失败时静默降级为主进程自己写文件，不影响启动器。</summary>
    public static GuardClient? Start(string logPath, GuardOptions options, string exePath)
    {
        if (!options.Enabled) return null;
        try
        {
            var client = new GuardClient(logPath, options);
            Current = client;

            // 主进程先挂上本地文件：守护进程还没起来的这段日志一条都不能丢
            Log.Service.AttachFile(logPath);

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,   // 由本进程直接创建，任务管理器里父子同组
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("--guard");
            psi.ArgumentList.Add("--session=" + client._session);
            psi.ArgumentList.Add("--pid=" + client._pid);
            psi.ArgumentList.Add("--log=" + logPath);
            psi.ArgumentList.Add("--timeout=" + options.TimeoutSeconds);
            psi.ArgumentList.Add("--autokill=" + (options.AutoKill ? 1 : 0));
            psi.ArgumentList.Add("--guard-lang=" + options.Language);

            client._guardProcess = Process.Start(psi);
            if (client._guardProcess is null) return null;

            _ = Task.Run(client.ConnectAsync);
            return client;
        }
        catch (Exception ex)
        {
            Log.Warn("Guard", "守护进程启动失败，日志继续由主进程记录：" + ex.Message);
            return null;
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            // InOut：既要写日志帧，也要读守护进程回的 READY 字节
            var logPipe = new NamedPipeClientStream(".", GuardProtocol.LogPipe(_session),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            // 守护进程启动需要一点时间，给 10 秒重试窗口
            for (var i = 0; i < 100 && !_cts.IsCancellationRequested; i++)
            {
                try { await logPipe.ConnectAsync(100, _cts.Token).ConfigureAwait(false); break; }
                catch (TimeoutException) { }
                if (i == 99) { Log.Warn("Guard", "守护进程没连上，日志继续由主进程记录"); return; }
            }

            var reader = new BinaryReader(logPipe);
            if (reader.ReadByte() != GuardProtocol.Ready)
            {
                Log.Warn("Guard", "守护进程就绪信号异常，日志继续由主进程记录");
                return;
            }

            // 屏障：先把主进程直写的文件全部刷完，再换落盘目标。
            // LogService.AttachSink 在锁内切换，切换瞬间不会有日志丢在半路。
            Log.Service.Flush();

            lock (_pipeLock)
            {
                _logPipe = logPipe;
                _logWriter = new BinaryWriter(logPipe);
            }

            _sink = new PipeLogSink(this);
            Log.Service.AttachSink(_sink);
            _ = Task.Run(SenderLoop);

            Log.Info("Guard", "日志已交给守护进程记录（会话 " + _session + "）");

            await ConnectHeartbeatAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Guard", "日志接管失败，继续由主进程记录：" + ex.Message);
        }
    }

    private async Task ConnectHeartbeatAsync()
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", GuardProtocol.HeartbeatPipe(_session),
                PipeDirection.Out, PipeOptions.Asynchronous);
            for (var i = 0; i < 100 && !_cts.IsCancellationRequested; i++)
            {
                try { await pipe.ConnectAsync(100, _cts.Token).ConfigureAwait(false); break; }
                catch (TimeoutException) { }
                if (i == 99) return;
            }
            lock (_pipeLock) { _hbPipe = pipe; _hbWriter = new BinaryWriter(pipe); }
            _ = Task.Run(HeartbeatLoop);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Guard", "心跳通道没建起来：" + ex.Message); }
    }

    private void SenderLoop()
    {
        try
        {
            foreach (var entry in _queue.GetConsumingEnumerable(_cts.Token))
            {
                lock (_pipeLock)
                {
                    if (_logWriter is null) break;
                    GuardProtocol.WriteEntry(_logWriter, entry);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { FallbackToLocalFile(); }
        catch (ObjectDisposedException) { FallbackToLocalFile(); }
    }

    /// <summary>守护进程中途没了：立刻切回主进程自己写文件，后面的日志不丢。</summary>
    private void FallbackToLocalFile()
    {
        if (_stopped) return;
        try
        {
            var sink = new FileLogSink(_logPath);
            Log.Service.AttachSink(sink);
            // 切换瞬间队列里可能还压着几条，补写进本地文件
            while (_queue.TryTake(out var leftover)) sink.Write(leftover);
            Log.Warn("Guard", "守护进程断开，日志改由主进程记录");
        }
        catch (Exception ex) { Log.Warn("Guard", "日志回退失败：" + ex.Message); }
    }

    private void HeartbeatLoop()
    {
        while (!_cts.IsCancellationRequested && !_stopped)
        {
            try
            {
                // UI 接管心跳之前（启动段），由后台线程自己续命，避免误判成卡死
                if (!_uiHeartbeatEnabled) Pulse();

                var fresh = Environment.TickCount64 - Interlocked.Read(ref _lastPulse) < 2000;
                if (fresh)
                {
                    lock (_pipeLock)
                    {
                        if (_hbWriter is not null) GuardProtocol.WriteHeartbeat(_hbWriter);
                    }
                }
                // fresh == false 就是 UI 卡住了：故意不发，让守护进程超时处理
            }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { break; }

            try { Task.Delay(500, _cts.Token).Wait(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (AggregateException) { break; }
        }
    }

    /// <summary>UI 线程每次心跳调一下。UI 卡死时这里不再被调用。</summary>
    public void Pulse() => Interlocked.Exchange(ref _lastPulse, Environment.TickCount64);

    /// <summary>主窗口起来之后调用：心跳改由 UI 线程驱动，才能真正反映"未响应"。</summary>
    public void EnableUiHeartbeat()
    {
        Pulse();
        _uiHeartbeatEnabled = true;
    }

    /// <summary>把一条日志交给守护进程（只入队，不阻塞调用线程）。</summary>
    public void Enqueue(LogEntry entry)
    {
        if (_stopped) return;
        try { _queue.Add(entry); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// 正常退出：把剩余日志刷完，再发一个「退出帧」告诉守护进程原因，然后断开。
    /// 守护进程读到 EOF 后自己退出；没收到这个帧就说明是被外部结束或崩溃。
    /// </summary>
    public void Stop(GuardExitReason reason = GuardExitReason.ApplicationExit)
    {
        if (_stopped) return;
        var isZh = _options.IsChinese;
        var why = GuardExitReasonText.Describe(reason, isZh);

        // 先把最后一条日志送出去，再置停止标记（否则 Enqueue 会把它丢掉）
        try
        {
            Log.Info("Guard", isZh
                ? "启动器正在退出（原因：" + why + "）"
                : "Launcher exiting (reason: " + why + ")");
            Log.Service.Flush();
        }
        catch { }

        _stopped = true;

        // 管道关掉之后若还有收尾日志（OnExit 的后续事件等），仍要有地方可写
        try { Log.Service.AttachSink(new FileLogSink(_logPath)); } catch { }

        _cts.Cancel();
        _queue.CompleteAdding();
        lock (_pipeLock)
        {
            // SenderLoop 已随 Cancel 退出；这里补上退出帧，必须是管道里最后一段数据
            try
            {
                if (_logWriter is not null)
                {
                    GuardProtocol.WriteShutdown(_logWriter, reason);
                    _logWriter.Flush();
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }

            try { _hbWriter?.Dispose(); } catch { }
            try { _logWriter?.Dispose(); } catch { }
            _logWriter = null;
            _hbWriter = null;
        }
        // 不主动杀守护进程：它要把管道里最后的日志写完
        Current = null;
    }

    /// <summary>挂到 LogService 上的落盘目标：日志只入队，由 SenderLoop 写管道。</summary>
    private sealed class PipeLogSink : ILogSink
    {
        private readonly GuardClient _owner;
        public PipeLogSink(GuardClient owner) => _owner = owner;

        public void Write(LogEntry entry) => _owner.Enqueue(entry);

        public void Flush()
        {
            // 等队列排空即可，不用真的 Flush 管道（WriteEntry 已经逐条 Flush）
            for (var i = 0; i < 100 && _owner._queue.Count > 0; i++) Thread.Sleep(10);
        }

        public void Dispose() { /* 管道由 GuardClient.Stop 统一关闭 */ }
    }
}
