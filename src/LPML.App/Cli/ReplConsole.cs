using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace LPML.App.Cli;

/// <summary>
/// 交互式控制台：上半区滚动显示游戏日志，底部固定一行输入。
/// 靠 ANSI 转义码把「日志流」和「输入行」分开，日志刷屏不会吃掉输入。
/// ANSI 不可用时自动退化成普通逐行输出（不分区，但输入仍可用）。
/// </summary>
public sealed class ReplConsole
{
    private const int MaxBuffer = 3000;

    private readonly object _sync = new();
    private readonly object _renderLock = new();
    private readonly List<string> _log = new(MaxBuffer);

    private string _input = "";
    private string _header = "";
    private string _hint = "";
    private readonly string _defaultHint;
    private bool _transcriptHintActive;

    /// <summary>整段文本（完整命令表）输出后临时显示的底部提示；下一条命令自动恢复默认提示。</summary>
    public string? TranscriptHint { get; set; }
    private bool _running;
    private bool _ansiEnabled;
    private bool _autoScroll = true;
    private volatile bool _suppressRender;
    private volatile bool _clearOnRender;

    private DateTime _lastRender = DateTime.MinValue;
    private static readonly TimeSpan RenderThrottle = TimeSpan.FromMilliseconds(60);

    /// <summary>停止信号：让阻塞在 ReadKey 的循环能及时退出（如游戏已退出）。</summary>
    private readonly TaskCompletionSource<bool> _stopSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>用户敲下回车时触发；返回 true 表示要退出 REPL。</summary>
    public Func<string, bool>? OnCommand { get; set; }

    /// <summary>是否需要重绘（供外部判断）。</summary>
    public bool IsRunning => _running;

    public ReplConsole(string header, string hint)
    {
        _header = header;
        _hint = hint;
        _defaultHint = hint;
        _ansiEnabled = TryEnableAnsi();
    }

    // ── ANSI 支持 ──────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>Windows 10 1511+ 需要显式开启虚拟终端，否则转义码会被当普通字符打印。</summary>
    public static bool TryEnableAnsi()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (!GetConsoleMode(handle, out var mode)) return false;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ANSI 是否可用（也用于外部决定是否走 REPL）。</summary>
    public bool AnsiEnabled => _ansiEnabled;

    /// <summary>输出被重定向时无法交互，应退化为纯日志模式。</summary>
    public static bool CanInteractive =>
        !Console.IsOutputRedirected && !Console.IsInputRedirected;

    // ── 日志与状态 ─────────────────────────────────────────

    /// <summary>线程安全追加一行日志（可从 MC 输出线程调用）。</summary>
    public void AppendLog(string line)
    {
        lock (_sync)
        {
            _log.Add(line);
            if (_log.Count > MaxBuffer) _log.RemoveRange(0, _log.Count - MaxBuffer);
        }

        // 节流：日志密集时不必每行都重绘
        var now = DateTime.Now;
        if (_autoScroll && now - _lastRender >= RenderThrottle)
        {
            _lastRender = now;
            Render();
        }
    }

    public void SetHeader(string header)
    {
        lock (_sync) _header = header;
        Render();
    }

    /// <summary>底部提示行（如 "输入 help 查看命令"）。</summary>
    public void SetHint(string hint)
    {
        lock (_sync) _hint = hint;
        Render();
    }

    public void ClearLog()
    {
        lock (_sync) _log.Clear();
        Render();
    }

    /// <summary>切换自动滚动（暂停后可手动翻看，但实际是暂停重绘）。</summary>
    public bool ToggleAutoScroll()
    {
        _autoScroll = !_autoScroll;
        Render();
        return _autoScroll;
    }

    // ── 渲染 ───────────────────────────────────────────────

    private void Render()
    {
        lock (_renderLock)
        {
            try
            {
                if (_ansiEnabled) RenderAnsi();
                else RenderPlain();
            }
            catch (IOException) { }
        }
    }

    private void RenderAnsi()
    {
        int width, height;
        try { width = Console.WindowWidth; height = Console.WindowHeight; }
        catch (IOException) { RenderPlain(); return; }

        if (width < 20 || height < 6) return;

        // 保留：header 1 行 + 分隔 1 行 + 输入 1 行 + 提示 1 行
        const int reserved = 4;
        var logArea = Math.Max(1, height - reserved);

        List<string> lines;
        string header, hint, input;
        lock (_sync)
        {
            var snapshot = new List<string>(_log);
            header = _header;
            hint = _hint;
            input = _input;
            lines = Wrap(snapshot, width - 1);
        }
        if (lines.Count > logArea)
            lines = lines.GetRange(lines.Count - logArea, logArea);

        var sb = new StringBuilder();
        if (_clearOnRender)
        {
            _clearOnRender = false;
            sb.Append("\u001b[H\u001b[J");   // transcript 之后重建分区，先清掉残留
        }
        else sb.Append("\u001b[H");         // 平时只归位，逐行 2K 清，不闪

        // header
        sb.Append("\u001b[2K").Append(Fit(header, width - 1)).Append('\n');

        // 日志区（不足的补空行，保证输入行位置固定）
        for (var i = 0; i < logArea; i++)
        {
            sb.Append("\u001b[2K");
            if (i < lines.Count) sb.Append(Fit(lines[i], width - 1));
            sb.Append('\n');
        }

        // 分隔线
        sb.Append("\u001b[2K").Append(new string('─', Math.Min(width - 1, 80))).Append('\n');

        // 输入行
        sb.Append("\u001b[2K").Append("> ").Append(Fit(input, width - 3));

        // 提示行
        sb.Append('\n').Append("\u001b[2K").Append(Fit(hint, width - 1));

        Console.Write(sb.ToString());
    }

    /// <summary>ANSI 不可用时的退化渲染：只把新日志追加打印，输入行不清屏。</summary>
    private void RenderPlain()
    {
        string header, hint, input;
        lock (_sync) { header = _header; hint = _hint; input = _input; }

        Console.WriteLine();
        Console.WriteLine(header);
        Console.WriteLine(hint);
        Console.Write("> " + input);
    }

    /// <summary>
    /// 头部 / 提示 / 输入行的单行适配。超宽时硬截断，不加省略号
    /// （旧版用省略号截断，容易被误认为命令没打完）。
    /// </summary>
    private static string Fit(string text, int width)
    {
        if (width <= 0) return "";
        return text.Length <= width ? text : text[..width];
    }

    /// <summary>
    /// 日志区按宽度软折行：内容一个字不丢，也不加任何省略号。
    /// 超宽行拆成多行，仍逐行 2K 清行，分区布局不变。
    /// </summary>
    private static List<string> Wrap(IEnumerable<string> lines, int width)
    {
        var result = new List<string>();
        if (width <= 0) return result;
        foreach (var raw in lines)
        {
            if (raw.Length <= width) { result.Add(raw); continue; }
            for (var i = 0; i < raw.Length; i += width)
                result.Add(raw.Substring(i, Math.Min(width, raw.Length - i)));
        }
        return result;
    }

    /// <summary>
    /// 完整命令表等大段文本：一次性逐行写到控制台，全部可见、可上滚。
    /// 不截断、不加省略号，也不走日志区的行数限制；同时并入日志缓冲，
    /// 分区恢复后仍保留尾部内容。输出后跳过一次重绘，免得立刻被分区盖住。
    /// </summary>
    public void WriteTranscript(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        lock (_renderLock)
        {
            try
            {
                if (_ansiEnabled)
                    Console.Write("\u001b[H\u001b[2J");   // 撤掉固定分区，光标归位
                foreach (var line in all) Console.WriteLine(line);
                Console.Out.Flush();
            }
            catch (IOException)
            {
                foreach (var line in all) Console.WriteLine(line);
            }
        }

        lock (_sync)
        {
            foreach (var line in all) _log.Add(line);
            if (_log.Count > MaxBuffer) _log.RemoveRange(0, _log.Count - MaxBuffer);
            if (TranscriptHint is not null)
            {
                _hint = TranscriptHint;
                _transcriptHintActive = true;
            }
        }
        _suppressRender = true;
        _clearOnRender = true;
    }

    // ── 输入循环 ───────────────────────────────────────────

    /// <summary>进入输入循环，阻塞当前线程直到用户退出。</summary>
    public void Run()
    {
        _running = true;

        bool oldTreatCtrlC;
        try { oldTreatCtrlC = Console.TreatControlCAsInput; }
        catch (IOException) { oldTreatCtrlC = false; }

        if (_ansiEnabled)
        {
            try { Console.CursorVisible = false; } catch (IOException) { }
            try { Console.Clear(); } catch (IOException) { }
        }

        // 让 Ctrl+C 作为普通按键进入输入循环（当作 stop），而不是直接终止进程
        try { Console.TreatControlCAsInput = true; } catch (IOException) { }

        Render();

        while (_running)
        {
            // Console.ReadKey 无法取消，用 Task 包装后与停止信号竞争等待，
            // 这样游戏退出时能立刻跳出循环，而不是卡在等用户下一次按键。
            ConsoleKeyInfo key;
            try
            {
                var readTask = Task.Run(() =>
                {
                    try { return Console.ReadKey(intercept: true); }
                    catch { return default; }
                });

                if (Task.WaitAny(readTask, _stopSignal.Task) != 0) break;
                key = readTask.GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { break; }
            catch (IOException) { break; }

            string? command = null;
            lock (_sync)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        command = _input;
                        _input = "";
                        break;

                    case ConsoleKey.Backspace:
                        if (_input.Length > 0) _input = _input[..^1];
                        break;

                    case ConsoleKey.Escape:
                        _input = "";
                        break;

                    case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                        command = "stop";
                        break;

                    default:
                        // 只接受可打印字符
                        if (!char.IsControl(key.KeyChar)) _input += key.KeyChar;
                        break;
                }
            }

            if (command is not null)
            {
                lock (_sync)
                {
                    if (_transcriptHintActive)
                    {
                        _hint = _defaultHint;
                        _transcriptHintActive = false;
                    }
                }
                var shouldExit = OnCommand?.Invoke(command) ?? false;
                if (shouldExit) { _running = false; break; }
            }

            if (_suppressRender) _suppressRender = false;
            else Render();
        }

        if (_ansiEnabled)
        {
            try { Console.CursorVisible = true; } catch (IOException) { }
        }
        try { Console.TreatControlCAsInput = oldTreatCtrlC; } catch (IOException) { }
    }

    /// <summary>从外部（如游戏退出事件）结束输入循环。</summary>
    public void Stop()
    {
        _running = false;
        _stopSignal.TrySetResult(true);
    }
}
