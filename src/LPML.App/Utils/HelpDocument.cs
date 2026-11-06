using System.IO;
using LPML.Core.Config;

namespace LPML.App.Utils;

/// <summary>
/// 文档分类。设置 → 说明 打开后先按分类分组，再列出该分类下的文档。
/// </summary>
public sealed class HelpCategory
{
    public string Id { get; init; } = "";

    public string Icon { get; init; } = "📁";

    public string TitleZh { get; init; } = "";

    public string TitleEn { get; init; } = "";

    public string Title(LauncherLanguage language)
        => language == LauncherLanguage.English ? TitleEn : TitleZh;
}

/// <summary>
/// 内置文档条目。以后要加「MC 玩法教程」之类的文档，
/// 只要在这里加一项，并把对应的 Markdown 放到 docs/&lt;语言&gt;/ 下即可，界面不用改。
/// </summary>
public sealed class HelpDocument
{
    /// <summary>所属分类标识（同一分类的文档会归到一组显示）。</summary>
    public string CategoryId { get; init; } = "launcher";

    /// <summary>唯一标识。</summary>
    public string Id { get; init; } = "";

    /// <summary>图标（列表里显示）。</summary>
    public string Icon { get; init; } = "📄";

    /// <summary>中文标题。</summary>
    public string TitleZh { get; init; } = "";

    /// <summary>英文标题。</summary>
    public string TitleEn { get; init; } = "";

    /// <summary>中文简介（列表里标题下那行）。</summary>
    public string SummaryZh { get; init; } = "";

    /// <summary>英文简介。</summary>
    public string SummaryEn { get; init; } = "";

    /// <summary>中文文档文件名（位于 docs/zh-CN/ 下）。</summary>
    public string FileZh { get; init; } = "";

    /// <summary>英文文档文件名（位于 docs/en-US/ 下）。</summary>
    public string FileEn { get; init; } = "";

    public string Title(LauncherLanguage language)
        => language == LauncherLanguage.English ? TitleEn : TitleZh;

    public string Summary(LauncherLanguage language)
        => language == LauncherLanguage.English ? SummaryEn : SummaryZh;

    public string FileName(LauncherLanguage language)
        => language == LauncherLanguage.English ? FileEn : FileZh;
}

/// <summary>内置文档清单。按列表顺序显示。</summary>
public static class HelpDocuments
{
    /// <summary>分类定义。以后加「Minecraft」分类放玩法教程，往这里加一项即可。</summary>
    public static IReadOnlyList<HelpCategory> Categories { get; } = new List<HelpCategory>
    {
        new() { Id = "launcher", Icon = "🚀", TitleZh = "启动器", TitleEn = "Launcher" }
        // new() { Id = "minecraft", Icon = "🎮", TitleZh = "Minecraft", TitleEn = "Minecraft" }
    };

    public static IReadOnlyList<HelpDocument> All { get; } = new List<HelpDocument>
    {
        new()
        {
            CategoryId = "launcher",
            Id = "guide",
            Icon = "📖",
            TitleZh = "使用说明",
            TitleEn = "User Guide",
            SummaryZh = "从第一次使用到常见问题，共 11 章",
            SummaryEn = "From first run to FAQ — 11 chapters",
            FileZh = "guide.md",
            FileEn = "guide.md"
        },
        new()
        {
            CategoryId = "launcher",
            Id = "readme",
            Icon = "📄",
            TitleZh = "项目说明",
            TitleEn = "Project README",
            SummaryZh = "功能总览、项目结构与编译方式",
            SummaryEn = "Feature overview, project layout and build instructions",
            FileZh = "readme.md",
            FileEn = "readme.md"
        },
        new()
        {
            CategoryId = "launcher",
            Id = "markdown",
            Icon = "✍️",
            TitleZh = "Markdown 语法支持",
            TitleEn = "Markdown Syntax",
            SummaryZh = "内置解释器支持哪些写法，以及如何加新文档",
            SummaryEn = "What the built-in parser supports, and how to add documents",
            FileZh = "markdown.md",
            FileEn = "markdown.md"
        },
        new()
        {
            CategoryId = "launcher",
            Id = "agreement",
            Icon = "⚖️",
            TitleZh = "用户协议与免责声明",
            TitleEn = "User Agreement & Disclaimer",
            SummaryZh = "首次启动时显示的条款，可随时重新查阅",
            SummaryEn = "The terms shown on first run; available anytime",
            FileZh = "agreement.md",
            FileEn = "agreement.md"
        }
        // 以后加「MC 玩法教程」就在这里再加一项，并把 CategoryId 指向新分类：
        // new() { CategoryId = "minecraft", Id = "mc-guide", Icon = "🎮",
        //         TitleZh = "MC 玩法教程", TitleEn = "Gameplay Guide",
        //         FileZh = "mc-guide.md", FileEn = "mc-guide.md" }
    };

    /// <summary>语言对应的文档子目录名（与 Locales 语言包一致）。</summary>
    public static string FolderName(LauncherLanguage language)
        => language == LauncherLanguage.English ? "en-US" : "zh-CN";

    public static HelpDocument? Find(string id)
        => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按当前语言列出实际可用的文档。
    /// 内置清单里的条目按原顺序在前（带图标和简介），
    /// 目录里多出来的 .md 会自动补进来 —— 往 docs 里丢一个 Markdown 就会出现在列表上，
    /// 不用改任何代码。
    /// </summary>
    public static List<HelpDocument> Resolve(string docsRoot, LauncherLanguage language)
    {
        var folder = Path.Combine(docsRoot, FolderName(language));
        var otherFolder = Path.Combine(docsRoot, FolderName(Other(language)));
        var result = new List<HelpDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in All)
        {
            var name = document.FileName(language);
            if (Exists(folder, name) || Exists(otherFolder, name))
            {
                result.Add(document);
                seen.Add(name);
            }
        }

        // 目录里额外放的 Markdown：用文件里的一级标题当标题，取不到就用文件名
        foreach (var file in SafeList(folder).Concat(SafeList(otherFolder)))
        {
            var name = Path.GetFileName(file);
            if (!seen.Add(name)) continue;

            var id = Path.GetFileNameWithoutExtension(name);
            var title = ReadTitle(file) ?? id.Replace('-', ' ');
            result.Add(new HelpDocument
            {
                CategoryId = "launcher",
                Id = id,
                Icon = "📄",
                TitleZh = title,
                TitleEn = title,
                SummaryZh = "",
                SummaryEn = "",
                FileZh = name,
                FileEn = name
            });
        }

        return result;
    }

    private static LauncherLanguage Other(LauncherLanguage language)
        => language == LauncherLanguage.English ? LauncherLanguage.ZhHans : LauncherLanguage.English;

    private static bool Exists(string folder, string fileName)
        => !string.IsNullOrWhiteSpace(fileName) && File.Exists(Path.Combine(folder, fileName));

    private static IEnumerable<string> SafeList(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.md") : Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>读 Markdown 里第一个一级标题，用来给"目录里多出来的文档"起名字。</summary>
    private static string? ReadTitle(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                var text = line.Trim().TrimStart('\uFEFF');
                if (!text.StartsWith("# ")) continue;
                var title = text.Substring(2).Trim();
                return title.Length == 0 ? null : title;
            }
        }
        catch (Exception) { }
        return null;
    }
}
