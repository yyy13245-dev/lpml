using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Config;

namespace LPML.App.Cli;

/// <summary>
/// 命令行模式的用户协议门禁。
/// 与图形界面共用 launcher.json 里的 AcceptedAgreementVersion，
/// 版本常量直接取 AgreementWindow.CurrentVersion（编译期常量，不会创建任何窗口），
/// 所以 GUI 同意过 CLI 不再问，CLI 同意过 GUI 也不再问。
/// </summary>
public static class CliAgreement
{
    /// <summary>拒绝时返回给 Main 的退出码。拒绝是预期行为，不算错误。</summary>
    public const int DeclinedExitCode = 0;

    /// <summary>
    /// 首次使用（或协议版本更新后）必须显式同意。
    /// 只有「是 / 我同意 / 同意 / yes」算同意；空输入、EOF、其它内容一律关闭命令行。
    /// </summary>
    public static bool EnsureAccepted(LauncherContext context)
    {
        if (context.Settings.AcceptedAgreementVersion >= AgreementWindow.CurrentVersion)
            return true;

        Console.WriteLine();
        Console.WriteLine(Lang.T("String.Cli.Agreement.Title"));
        Console.WriteLine(Lang.T("String.Cli.Agreement.Intro"));
        Console.WriteLine(new string('=', 60));

        var markdown = FindMarkdown(context, "agreement.md");
        if (markdown is null)
        {
            Console.WriteLine(Lang.T("String.Cli.Agreement.Missing"));
            Console.WriteLine(L.T("String.Agreement.FallbackBody"));
        }
        else
        {
            // 门禁发生在 REPL 建立之前，逐行打印即可；CLI 没有 Markdown 渲染，
            // 这里只做最轻量的纯文本化，保证用户真的能看到条款全文。
            foreach (var line in ToPlainText(markdown).Split('\n'))
                Console.WriteLine(line.TrimEnd('\r'));
        }

        Console.WriteLine(new string('=', 60));
        Console.WriteLine(Lang.F("String.Cli.Agreement.FullText", DisclaimerPath(context)));
        Console.WriteLine(Lang.T("String.Cli.Agreement.Prompt"));

        // 输入被重定向（管道 / 无人值守）时没法交互，按「未同意」处理并退出，
        // 不能让脚本卡在 ReadLine 上，也不能默认替用户同意。
        if (Console.IsInputRedirected)
        {
            Decline();
            return false;
        }

        string? input;
        try { input = Console.ReadLine(); }
        catch (IOException) { input = null; }

        if (!IsAcceptedWord(input))
        {
            Decline();
            return false;
        }

        context.Settings.AcceptedAgreementVersion = AgreementWindow.CurrentVersion;
        context.Save();
        Console.WriteLine(Lang.F("String.Cli.Agreement.Accepted", AgreementWindow.CurrentVersion));
        Log.Info("App", L.F("String.Agreement.AcceptedLog", AgreementWindow.CurrentVersion));
        Console.WriteLine();
        return true;
    }

    private static void Decline()
    {
        Console.WriteLine(Lang.T("String.Cli.Agreement.Declined"));
        Log.Warn("App", L.T("String.Agreement.DeclinedLog"));
    }

    /// <summary>只认这四个词；大小写不敏感只作用于 yes。</summary>
    private static bool IsAcceptedWord(string? input)
    {
        var s = (input ?? "").Trim();
        return s.Equals("是", StringComparison.Ordinal)
               || s.Equals("我同意", StringComparison.Ordinal)
               || s.Equals("同意", StringComparison.Ordinal)
               || s.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    // ── 文档定位（doc / agreement 命令也用这一套） ──────────

    /// <summary>按界面语言找 docs 下的 Markdown；当前语言缺失时回退另一种语言。</summary>
    public static string? FindMarkdownPath(LauncherContext context, string fileName)
    {
        var language = context.Settings.Language == LauncherLanguage.English ? "en-US" : "zh-CN";
        var other = language == "en-US" ? "zh-CN" : "en-US";

        var roots = new List<string>();
        try { roots.Add(context.Paths.DocsDirectory); } catch (Exception) { }
        roots.Add(Path.Combine(AppContext.BaseDirectory, "docs"));
        try { roots.Add(Path.Combine(context.Paths.AppDirectory, "docs")); } catch (Exception) { }

        foreach (var folder in new[] { language, other })
        {
            foreach (var root in roots)
            {
                try
                {
                    var full = Path.Combine(root, folder, fileName);
                    if (File.Exists(full)) return full;
                }
                catch (Exception) { /* 换下一个候选 */ }
            }
        }
        return null;
    }

    public static string? FindMarkdown(LauncherContext context, string fileName)
    {
        var path = FindMarkdownPath(context, fileName);
        if (path is null) return null;
        try { return File.ReadAllText(path); }
        catch (Exception) { return null; }
    }

    public static string DisclaimerPath(LauncherContext context)
    {
        var p = Path.Combine(AppContext.BaseDirectory, "DISCLAIMER.md");
        if (File.Exists(p)) return p;
        try { return Path.Combine(context.Paths.RootDirectory, "DISCLAIMER.md"); }
        catch (Exception) { return p; }
    }

    /// <summary>Markdown → 纯文本：去掉标题/引用/粗体/代码标记与链接语法，保留换行。</summary>
    public static string ToPlainText(string markdown)
    {
        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s+", "");
            line = Regex.Replace(line, @"^\s{0,3}>\s?", "");
            line = Regex.Replace(line, @"^\s*[-*]\s+", "  - ");
            line = line.Replace("**", "").Replace("`", "");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
