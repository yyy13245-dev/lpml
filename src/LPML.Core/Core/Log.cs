using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using LPML.Core.Utils;

namespace LPML.Core;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}

/// <summary>
/// 一条日志。Seq 为全局递增编号（从 1 开始），便于排查时直接引用"第 N 条"。
/// Seq &lt;= 0 表示不编号（如转发的游戏进程输出，它不属于启动器自身的日志）。
/// </summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message, long Seq = 0)
{
    private string SeqText => Seq > 0 ? $"[#{Seq:D4}] " : "";

    public override string ToString() => $"{SeqText}[{Time:HH:mm:ss}] [{Level,-5}] [{Source}] {Message}";
}

/// <summary>
/// 日志落盘目标。默认写本地文件；守护进程就绪后可以换成跨进程管道，
/// 由守护进程统一写同一个文件。切换在 LogService 的锁内完成，不会丢日志、不会写串。
/// </summary>
public interface ILogSink : IDisposable
{
    void Write(LogEntry entry);
    void Flush();
}

/// <summary>本地文件落盘。FileShare.ReadWrite 允许守护进程同时打开同一文件。</summary>
public sealed class FileLogSink : ILogSink
{
    /// <summary>
    /// 所有日志文件统一用「UTF-8 + BOM」。不带 BOM 时 Windows 记事本会按系统 ANSI
    /// （简体 GBK / 繁体 Big5）猜编码，中文日志就会整篇乱码。
    /// Append 模式下 StreamWriter 只在文件为 0 字节时写 BOM，不会在文件中间重复插入。
    /// </summary>
    public static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private StreamWriter? _writer;
    public string Path { get; }

    public FileLogSink(string path)
    {
        Path = path;
        try
        {
            FileHelper.EnsureDirectory(System.IO.Path.GetDirectoryName(path));
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                LogEncoding)
            {
                AutoFlush = true
            };
        }
        catch (IOException) { _writer = null; }
        catch (UnauthorizedAccessException) { _writer = null; }
    }

    public void Write(LogEntry entry)
    {
        try { _writer?.WriteLine(entry.ToString()); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Flush()
    {
        try { _writer?.Flush(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }
}

/// <summary>
/// 轻量日志：内存环形缓冲（供界面实时显示）+ 可切换的落盘目标。
/// 启动阶段由主进程直接写文件；守护进程准备好之后整体切到管道，由守护进程写文件。
/// </summary>
public sealed class LogService
{
    private readonly object _sync = new();
    private long _seq;

    /// <summary>日志目录（AttachFile 时记下，用于启动时归档旧日志）。</summary>
    private string? _logDirectory;

    /// <summary>归档压缩包保留上限：oldlog1..oldlog5，超出后删除最旧的。</summary>
    public int MaxArchives { get; set; } = 5;
    private readonly List<LogEntry> _entries = new();
    private ILogSink? _sink;
    private readonly int _capacity;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public bool WriteToFile { get; set; } = true;
    public int Capacity => _capacity;

    /// <summary>当前是否已经挂上了落盘目标。</summary>
    public bool HasSink { get { lock (_sync) return _sink is not null; } }

    public event EventHandler<LogEntry>? EntryAdded;

    /// <summary>控制台输出：命令行模式下把日志同步打到 stdout。</summary>
    private Action<LogEntry>? _consoleSink;

    /// <summary>true 时同时写控制台（CLI 模式开启）。</summary>
    public bool WriteToConsole => _consoleSink is not null;

    /// <summary>注册控制台输出。传 null 关闭。</summary>
    public void AttachConsole(Action<LogEntry>? sink) => _consoleSink = sink;

    /// <summary>游戏进程输出转发（直接打印，不走日志缓冲，避免刷爆内存）。</summary>
    public void Console(string line) => _consoleSink?.Invoke(
        new LogEntry(DateTime.Now, LogLevel.Debug, "Game", line, 0));

    public LogService(int capacity = 2000)
    {
        _capacity = capacity;
    }

    /// <summary>
    /// 挂上本地文件。同一路径重复调用会直接跳过（不会二次归档、不会清空刚写的日志）。
    /// </summary>
    public void AttachFile(string path)
    {
        lock (_sync)
        {
            if (_sink is FileLogSink same && string.Equals(same.Path, path, StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var directory = Path.GetDirectoryName(path);
                FileHelper.EnsureDirectory(directory);
                if (!string.IsNullOrEmpty(directory)) _logDirectory = directory;

                // 只在第一次挂文件时归档；守护进程接管后不能再清空日志
                if (_sink is null) ArchivePrevious(path);

                var old = _sink;
                _sink = new FileLogSink(path);
                old?.Dispose();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 把落盘目标换成守护进程。调用前请先 Flush()，
    /// 切换在锁内完成：切换瞬间的日志要么全部落到旧文件，要么全部进管道，不会丢。
    /// </summary>
    public void AttachSink(ILogSink sink)
    {
        lock (_sync)
        {
            var old = _sink;
            _sink = sink;
            old?.Dispose();
        }
    }

    /// <summary>
    /// 把上一次遗留的 lpml.log 打包归档。
    /// 编号规则：oldlog1.zip 最新，oldlogN.zip 最旧；已有 N 个时，先整体后移一位（5→删除），再存 1。
    /// </summary>
    public void ArchivePrevious(string currentLogPath)
    {
        try
        {
            if (!File.Exists(currentLogPath)) return;
            if (new FileInfo(currentLogPath).Length == 0) return;

            var directory = Path.GetDirectoryName(currentLogPath);
            if (string.IsNullOrEmpty(directory)) return;
            Directory.CreateDirectory(directory);

            var max = Math.Max(1, MaxArchives);

            // 最旧的那个（第 max 个）直接删除，腾出位置
            var oldest = Path.Combine(directory, $"oldlog{max}.zip");
            if (File.Exists(oldest)) File.Delete(oldest);

            // 其余依次后移：oldlog4.zip → oldlog5.zip ... oldlog1.zip → oldlog2.zip
            for (var i = max - 1; i >= 1; i--)
            {
                var from = Path.Combine(directory, $"oldlog{i}.zip");
                var to = Path.Combine(directory, $"oldlog{i + 1}.zip");
                if (File.Exists(from)) File.Move(from, to, overwrite: true);
            }

            // 当前日志 → oldlog1.zip
            var target = Path.Combine(directory, "oldlog1.zip");
            if (File.Exists(target)) File.Delete(target);
            using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(currentLogPath, Path.GetFileName(currentLogPath), CompressionLevel.Optimal);
            }

            // 归档完清空当前日志，本次运行从「只含 BOM 的空文件」开始，
            // 保证记事本从第一行就按 UTF-8 解读；后续 Append 不会再重复写 BOM。
            File.WriteAllBytes(currentLogPath, FileLogSink.LogEncoding.GetPreamble());
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine("Log archive failed: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine("Log archive failed: " + ex.Message);
        }
        catch (ArgumentException ex)
        {
            System.Diagnostics.Debug.WriteLine("Log archive failed: " + ex.Message);
        }
    }

    public void Log(LogLevel level, string source, string message)
    {
        if (level < MinimumLevel) return;
        var entry = new LogEntry(DateTime.Now, level, source, message, Interlocked.Increment(ref _seq));
        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > _capacity)
                _entries.RemoveRange(0, _entries.Count - _capacity);
            if (WriteToFile)
            {
                try { _sink?.Write(entry); } catch { }
            }
        }

        try { _consoleSink?.Invoke(entry); } catch { }

        EntryAdded?.Invoke(this, entry);
    }

    public void Debug(string source, string message) => Log(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Log(LogLevel.Info, source, message);
    public void Warn(string source, string message) => Log(LogLevel.Warn, source, message);
    public void Error(string source, string message) => Log(LogLevel.Error, source, message);
    public void Error(string source, string message, Exception ex) => Log(LogLevel.Error, source, message + Environment.NewLine + ex);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync) return _entries.ToList();
    }

    public string ExportText()
    {
        lock (_sync) return string.Join(Environment.NewLine, _entries.Select(e => e.ToString()));
    }

    public void Clear()
    {
        lock (_sync) _entries.Clear();
    }

    public void Flush()
    {
        lock (_sync)
        {
            try { _sink?.Flush(); } catch { }
        }
    }
}

/// <summary>全局静态日志入口，便于各处直接调用。</summary>
public static class Log
{
    public static LogService Service { get; } = new();

    public static void Debug(string source, string message) => Service.Debug(source, message);
    public static void Info(string source, string message) => Service.Info(source, message);
    public static void Warn(string source, string message) => Service.Warn(source, message);
    public static void Error(string source, string message) => Service.Error(source, message);
    public static void Error(string source, string message, Exception ex) => Service.Error(source, message, ex);
}
