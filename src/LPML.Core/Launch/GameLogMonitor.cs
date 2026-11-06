using System.Globalization;
using System.Text.RegularExpressions;

namespace LPML.Core.Launch;

/// <summary>游戏日志级别，与 Minecraft / Log4j 的常用级别对应。</summary>
public enum GameLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5
}

/// <summary>解析后的一行游戏日志。</summary>
public sealed record GameLogLine(
    DateTimeOffset Time,
    string Thread,
    GameLogLevel Level,
    string Message,
    string Raw,
    bool IsCrashMarker);

public sealed record ServerPortOpenedEventArgs(int Port, string Raw);

public sealed record GameCrashEventArgs(string Reason, string? CrashReportPath, string Raw);

/// <summary>
/// 游戏输出监听：把原始 stdout/stderr 解析成结构化日志，
/// 并识别「服务器端口已开放」与「游戏崩溃」两类关键事件。
/// 可在设置 → 隐私 中整体关闭。
/// </summary>
public sealed class GameLogMonitor
{
    private static readonly Regex TimeThreadLevel = new(
        @"^\s*\[(?<time>\d{2}:\d{2}:\d{2})\]\s*\[(?<thread>[^\]]+?)\s*/\s*(?<level>TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL|SEVERE)\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 端口开放：不同版本 / 不同端（客户端局域网、独立服务器）措辞不同
    private static readonly Regex[] PortPatterns =
    {
        new(@"Started serving on\s*:?\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Opened to LAN on port\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Opened server on port\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Starting Minecraft server on\s*(?:\*|[\d.]+)\s*:\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Listening on\s*(?:\*|[\d.]+)\s*:\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    // 已进入主界面（不同版本 / 加载器措辞不同）
    private static readonly Regex[] ReadyPatterns =
    {
        // 官方客户端 1.13+ 资源加载完成
        new(@"Reloading ResourceManager", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // 主菜单出现
        new(@"OpenAL initialized|Sound engine started", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Forge / Fabric 常见
        new(@"Finished loading|Loading complete|Done loading", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // 独立服务端
        new(@"Done \(.*?\)! For help, type", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // 兜底：主界面线程启动
        new(@"Starting Potentially Dangerous|Client thread.*?main menu", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    // 崩溃特征
    private static readonly Regex CrashMarker = new(@"#@!@#\s*Game crashed", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CrashReport = new(@"Crash report saved to:?\s*(?<path>\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ThreadException = new(@"Exception in thread\s+""(?<thread>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex JvmFatal = new(@"A fatal error has been detected by the Java Runtime Environment", RegexOptions.Compiled);
    private static readonly Regex OutOfMemory = new(@"java\.lang\.OutOfMemoryError", RegexOptions.Compiled);

    private readonly object _sync = new();
    private readonly List<GameLogLine> _lines = new();

    public bool DetectPort { get; set; } = true;
    public bool DetectCrash { get; set; } = true;
    public int MaxLines { get; set; } = 5000;

    /// <summary>已解析的日志行（供界面绑定 / 导出）。</summary>
    public IReadOnlyList<GameLogLine> Lines
    {
        get { lock (_sync) return _lines.ToList(); }
    }

    public event EventHandler<GameLogLine>? LineReceived;

    /// <summary>监听到「端口已开放」时触发（例如局域网联机 / 独立服务器）。</summary>
    public event EventHandler<ServerPortOpenedEventArgs>? PortOpened;

    /// <summary>从日志中识别出崩溃特征时触发。</summary>
    public event EventHandler<GameCrashEventArgs>? CrashDetected;

    /// <summary>识别出「已进入主界面」时触发（用于关闭启动进度窗）。</summary>
    public event EventHandler? ReadyDetected;

    private readonly HashSet<int> _reportedPorts = new();
    private bool _readyFired;

    private void MatchReady(string raw)
    {
        if (_readyFired) return;
        foreach (var pattern in ReadyPatterns)
        {
            if (!pattern.IsMatch(raw)) continue;
            _readyFired = true;
            ReadyDetected?.Invoke(this, EventArgs.Empty);
            return;
        }
    }

    /// <summary>喂入一行原始输出。</summary>
    public void Feed(string? raw)
    {
        if (raw is null) return;
        var line = Parse(raw);

        lock (_sync)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        LineReceived?.Invoke(this, line);

        MatchReady(raw);
        if (DetectPort) MatchPort(raw);
        if (DetectCrash) MatchCrash(raw, line);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _lines.Clear();
            _reportedPorts.Clear();
        }
    }

    /// <summary>把原始行解析成结构化日志。</summary>
    public static GameLogLine Parse(string raw)
    {
        var match = TimeThreadLevel.Match(raw);
        var isCrash = CrashMarker.IsMatch(raw) || CrashReport.IsMatch(raw) || JvmFatal.IsMatch(raw);

        if (!match.Success)
        {
            // 没有标准前缀（例如异常堆栈、Fabric 启动横幅），按内容粗判级别
            var rough = GuessLevelByContent(raw);
            return new GameLogLine(DateTimeOffset.Now, "", rough, raw, raw, isCrash);
        }

        var timeText = match.Groups["time"].Value;
        var thread = match.Groups["thread"].Value.Trim();
        var level = ParseLevel(match.Groups["level"].Value);

        var message = raw[match.Length..].TrimStart();
        if (message.StartsWith(':')) message = message[1..].TrimStart();

        var time = DateTimeOffset.Now;
        if (TimeSpan.TryParseExact(timeText, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var span))
            time = DateTimeOffset.Now.Date.Add(span);

        return new GameLogLine(time, thread, level, message, raw, isCrash);
    }

    private static GameLogLevel ParseLevel(string text) => text.ToUpperInvariant() switch
    {
        "TRACE" => GameLogLevel.Trace,
        "DEBUG" => GameLogLevel.Debug,
        "INFO" => GameLogLevel.Info,
        "WARN" or "WARNING" => GameLogLevel.Warn,
        "ERROR" or "SEVERE" => GameLogLevel.Error,
        "FATAL" => GameLogLevel.Fatal,
        _ => GameLogLevel.Info
    };

    private static GameLogLevel GuessLevelByContent(string raw)
    {
        if (CrashMarker.IsMatch(raw) || JvmFatal.IsMatch(raw)) return GameLogLevel.Fatal;
        if (ThreadException.IsMatch(raw) || OutOfMemory.IsMatch(raw)) return GameLogLevel.Error;
        if (raw.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return GameLogLevel.Error;
        if (raw.Contains("WARN", StringComparison.OrdinalIgnoreCase)) return GameLogLevel.Warn;
        return GameLogLevel.Info;
    }

    private void MatchPort(string raw)
    {
        foreach (var pattern in PortPatterns)
        {
            var match = pattern.Match(raw);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups["port"].Value, out var port)) continue;
            if (port is < 1 or > 65535) continue;

            lock (_sync)
            {
                if (!_reportedPorts.Add(port)) return;
            }
            PortOpened?.Invoke(this, new ServerPortOpenedEventArgs(port, raw));
            return;
        }
    }

    private void MatchCrash(string raw, GameLogLine line)
    {
        string? reason = null;

        var marker = CrashMarker.Match(raw);
        var report = CrashReport.Match(raw);
        var threadEx = ThreadException.Match(raw);
        var jvm = JvmFatal.Match(raw);
        var oom = OutOfMemory.Match(raw);

        if (marker.Success) reason = L.T("String.Msg.Game.CrashReason.CrashMarker");
        else if (oom.Success) reason = L.T("String.Msg.Game.CrashReason.OutOfMemory");
        else if (jvm.Success) reason = L.T("String.Msg.Game.CrashReason.JvmFatal");
        else if (threadEx.Success) reason = L.F("String.Msg.Game.CrashReason.ThreadException", threadEx.Groups["thread"].Value);
        else if (report.Success) reason = L.T("String.Msg.Game.CrashReason.CrashReport");
        else if (line.Level == GameLogLevel.Fatal) reason = L.T("String.Msg.Game.CrashReason.FatalLog");

        if (reason is null) return;

        var path = report.Success ? report.Groups["path"].Value.Trim() : null;
        CrashDetected?.Invoke(this, new GameCrashEventArgs(reason, path, raw));
    }

    /// <summary>把日志级别转成界面显示用的短标签。</summary>
    public static string LevelText(GameLogLevel level) => level switch
    {
        GameLogLevel.Trace => L.T("String.GameLog.Level.Trace"),
        GameLogLevel.Debug => L.T("String.GameLog.Level.Debug"),
        GameLogLevel.Info => L.T("String.GameLog.Level.Info"),
        GameLogLevel.Warn => L.T("String.GameLog.Level.Warn"),
        GameLogLevel.Error => L.T("String.GameLog.Level.Error"),
        GameLogLevel.Fatal => L.T("String.GameLog.Level.Fatal"),
        _ => level.ToString()
    };
}
