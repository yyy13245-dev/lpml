using System.IO;
using System.Text.Json;
using LPML.Core;

namespace LPML.App.Guard;

/// <summary>
/// 日志守护进程的配置。Program.Main 在建立 WPF Application 之前就要用它，
/// 所以不能依赖 LauncherContext，直接轻量读一次 launcher.json。
/// </summary>
public sealed class GuardOptions
{
    /// <summary>是否启用独立守护进程。关掉后所有日志仍由主进程直接写文件。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>启动器多久没有心跳就算"未响应"（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>未响应时直接强制关闭，不弹窗确认。</summary>
    public bool AutoKill { get; set; } = false;

    /// <summary>守护进程提示框用哪种语言（zh / en）。</summary>
    public string Language { get; set; } = "zh";

    public bool IsChinese => !string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);

    public static readonly string[] DisableSwitches = { "--no-guard", "--disable-guard" };

    /// <summary>从配置文件读，再用命令行参数覆盖。</summary>
    public static GuardOptions Load(string configFile, string[] args)
    {
        var o = new GuardOptions();
        try
        {
            if (File.Exists(configFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configFile));
                if (doc.RootElement.TryGetProperty("Advanced", out var adv))
                {
                    o.Enabled = ReadBool(adv, "EnableGuardProcess", o.Enabled);
                    o.TimeoutSeconds = ReadInt(adv, "GuardTimeoutSeconds", o.TimeoutSeconds);
                    o.AutoKill = ReadBool(adv, "GuardAutoKill", o.AutoKill);
                }
                // LauncherSettings.Language 是枚举：0 = 简体中文，其余按英文处理
                if (doc.RootElement.TryGetProperty("Language", out var lang))
                {
                    if (lang.ValueKind == JsonValueKind.String) o.Language = lang.GetString() ?? o.Language;
                    else if (lang.ValueKind == JsonValueKind.Number && lang.TryGetInt32(out var n)) o.Language = n == 0 ? "zh" : "en";
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var a in args)
        {
            if (DisableSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)) o.Enabled = false;
            if (a.StartsWith("--guard-timeout=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(a[16..], out var t)) o.TimeoutSeconds = Math.Clamp(t, 5, 300);
            if (a.Equals("--guard-auto-kill", StringComparison.OrdinalIgnoreCase)) o.AutoKill = true;
            if (a.StartsWith("--guard-lang=", StringComparison.OrdinalIgnoreCase)) o.Language = a[13..];
        }

        o.TimeoutSeconds = Math.Clamp(o.TimeoutSeconds, 5, 300);
        return o;
    }

    private static bool ReadBool(JsonElement e, string name, bool fallback)
        => e.TryGetProperty(name, out var v)
            && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    private static int ReadInt(JsonElement e, string name, int fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : fallback;

    /// <summary>解析 --key=value 形式的守护进程参数。</summary>
    public static string? Value(string[] args, string key)
    {
        var prefix = "--" + key + "=";
        foreach (var a in args)
            if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a[prefix.Length..].Trim('"');
        return null;
    }

    public static int IntValue(string[] args, string key, int fallback)
        => int.TryParse(Value(args, key), out var n) ? n : fallback;
}
