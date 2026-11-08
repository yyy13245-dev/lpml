using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LPML.Core.Launch;

/// <summary>
/// 游戏输出总线：所有游戏实例（GUI / CLI）的原始 stdout / stderr 行都汇到这里。
/// 与日志窗口、隐私开关无关，联机页在打开之后也能读到开房前产生的日志。
/// </summary>
public static class GameOutputHub
{
    private static readonly object Sync = new();
    private static readonly Queue<string> Buffer = new();

    public const int MaxLines = 4000;

    /// <summary>新到一行游戏输出（后台线程触发，UI 侧需自行封送）。</summary>
    public static event EventHandler<string>? LinePushed;

    public static void Push(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (Sync)
        {
            Buffer.Enqueue(line);
            while (Buffer.Count > MaxLines) Buffer.Dequeue();
        }
        LinePushed?.Invoke(null, line);
    }

    public static IReadOnlyList<string> Recent(int count = MaxLines)
    {
        lock (Sync) return Buffer.TakeLast(count).ToList();
    }
}

/// <summary>
/// 从游戏日志里识别「对局域网开放」的端口。
/// 不按中文 / 英文关键词逐语言建表：主规则是「方括号里出现 4~5 位数字」，
/// 这是各语言版本聊天栏与日志共有的形态；识别错了用户可以直接覆盖输入框。
/// </summary>
public static class GamePortDetector
{
    // ① 主规则：[ ... 45231 ... ]，方括号内任意文字 + 4~5 位独立数字
    private static readonly Regex Bracket = new(
        @"\[[^\[\]\r\n]*?(?<!\d)(?<port>\d{4,5})(?!\d)[^\[\]\r\n]*?\]",
        RegexOptions.Compiled);

    // ② 官方英文日志的常见写法（保留，优先级低于方括号规则）
    private static readonly Regex[] Explicit =
    {
        new(@"Started serving on\s*:?\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Opened to LAN on port\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Opened server on port\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Starting Minecraft server on\s*(?:\*|[\d.]+)\s*:\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"Listening on\s*(?:\*|[\d.]+)\s*:\s*(?<port>\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bport\s*[:=]\s*(?<port>\d{4,5})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    // ③ 兜底：整行里独立的 4~5 位数字（可能误判，交由用户手动覆盖）
    private static readonly Regex Bare = new(@"(?<!\d)(?<port>\d{4,5})(?!\d)", RegexOptions.Compiled);

    /// <summary>单行的高置信度识别（方括号规则），用于实时自动填入。</summary>
    public static bool TryDetectBracket(string? line, out int port)
    {
        port = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var m = Bracket.Match(line);
        return m.Success && Valid(m.Groups["port"].Value, out port);
    }

    /// <summary>扫描一批日志行，按「方括号 → 明确写法 → 裸数字」的优先级返回最佳端口。</summary>
    public static int? Scan(IEnumerable<string> lines)
    {
        var snapshot = lines.Where(l => !string.IsNullOrEmpty(l)).TakeLast(GameOutputHub.MaxLines).ToList();

        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                var line = snapshot[i];
                var matches = pass switch
                {
                    0 => new[] { Bracket.Match(line) },
                    1 => Explicit.Select(r => r.Match(line)).ToArray(),
                    _ => new[] { Bare.Match(line) }
                };
                foreach (var m in matches)
                {
                    if (!m.Success) continue;
                    if (!Valid(m.Groups["port"].Value, out var port)) continue;
                    return port;
                }
            }
        }
        return null;
    }

    private static bool Valid(string text, out int port)
        => int.TryParse(text, out port) && port is > 0 and <= 65535;
}
