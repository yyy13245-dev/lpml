using LPML.App.Services;
using LPML.App.Utils;
using LPML.Core;
using LPML.Core.Config;
using LPML.Core.Utils;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LPML.App.Windows;

namespace LPML.App.Views;

/// <summary>
/// 内置文档中心：先列出文档条目，点进去才看内容。
/// 这样以后加「MC 玩法教程」只要往 HelpDocuments 里加一项，界面不用改。
/// 文档分中英两份，按当前界面语言自动选择。
/// </summary>
public partial class HelpPage : UserControl
{
    /// <summary>列表条目：标题与简介随当前语言取值。</summary>
    public sealed class DocItem : ObservableObject
    {
        private readonly HelpDocument _document;

        public DocItem(HelpDocument document) => _document = document;

        public string Id => _document.Id;

        // 列表用单向绑定，只给 getter；语言切换时整个列表重建
        public string IconNow => _document.Icon;

        public string TitleNow => _document.Title(CurrentLanguage);

        public string SummaryNow => _document.Summary(CurrentLanguage);
    }

    /// <summary>分类条目：分类标题 + 该分类下的文档。</summary>
    public sealed class CategoryItem
    {
        private readonly HelpCategory _category;

        public CategoryItem(HelpCategory category, IEnumerable<DocItem> items)
        {
            _category = category;
            Items = items.ToList();
        }

        public string Icon => _category.Icon;

        public string TitleNow => _category.Title(CurrentLanguage);

        public List<DocItem> Items { get; }
    }

    private HelpDocument? _current;

    private LauncherContext Context => App.Context;

    public HelpPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        var language = CurrentLanguage;
        // 扫描 docs 目录：内置文档在前，目录里额外放的 Markdown 自动补上
        var documents = HelpDocuments.Resolve(Context.Paths.DocsDirectory, language);
        CategoryList.ItemsSource = HelpDocuments.Categories
            .Select(c => new CategoryItem(c,
                documents.Where(d => d.CategoryId == c.Id).Select(d => new DocItem(d))))
            .Where(g => g.Items.Count > 0)
            .ToList();
        // 语言切换后如果正在看文档，重新加载对应语言版本
        if (_current is not null && DocumentView.Visibility == Visibility.Visible)
            OpenDocument(_current);
    }

    private void Document_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id) return;
        var document = HelpDocuments.Find(id);
        if (document is null) return;
        OpenDocument(document);
    }

    private void OpenDocument(HelpDocument document)
    {
        _current = document;

        ListView.Visibility = Visibility.Collapsed;
        DocumentView.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        ToolPanel.Visibility = Visibility.Visible;
        LoadingMask.Visibility = Visibility.Visible;

        HeadTitle.Text = document.Title(CurrentLanguage);
        HeadSubtitle.Text = document.Summary(CurrentLanguage);

        _ = LoadAsync(document);
    }

    /// <summary>
    /// 读文件放到后台，解析回到 UI 线程（FlowDocument 有线程亲和性）。
    /// 解析很快，提示通常一闪而过，但慢机器上不会白屏。
    /// </summary>
    private async Task LoadAsync(HelpDocument document)
    {
        var language = CurrentLanguage;

        // 让出一帧，保证「正在解析中」有机会渲染出来
        await Task.Yield();

        var text = await Task.Run(() => ReadDocument(document, language)).ConfigureAwait(true);

        FlowDocument content;
        try
        {
            content = text is null
                ? BuildMessage(Lang.T("String.Help.NotFound"))
                : MarkdownRenderer.Render(text);
        }
        catch (Exception ex)
        {
            Log.Warn("Help", Lang.T("String.Help.ParseFailed") + " " + ex.Message);
            content = BuildMessage(Lang.T("String.Help.ParseFailed"));
        }

        DocViewer.Document = content;
        LoadingMask.Visibility = Visibility.Collapsed;
    }

    private static LauncherLanguage CurrentLanguage
        => App.Context?.Settings?.Language ?? LauncherLanguage.ZhHans;

    private static string? ReadDocument(HelpDocument document, LauncherLanguage language)
    {
        var relative = Path.Combine("docs", HelpDocuments.FolderName(language), document.FileName(language));

        // 当前语言没有就回退另一种语言，避免直接空白
        foreach (var name in new[] { relative, Fallback(document, language) })
        {
            if (string.IsNullOrEmpty(name)) continue;
            foreach (var root in Roots())
            {
                try
                {
                    var full = Path.Combine(root, name);
                    if (File.Exists(full)) return File.ReadAllText(full);
                }
                catch (Exception) { /* 换下一个候选 */ }
            }
        }
        return null;
    }

    private static string Fallback(HelpDocument document, LauncherLanguage language)
    {
        var other = language == LauncherLanguage.English ? LauncherLanguage.ZhHans : LauncherLanguage.English;
        return Path.Combine("docs", HelpDocuments.FolderName(other), document.FileName(other));
    }

    private static string[] Roots()
    {
        var list = new List<string> { AppContext.BaseDirectory };
        try
        {
            var root = App.Context?.Paths?.AppDirectory;
            if (!string.IsNullOrWhiteSpace(root)) list.Add(root);
        }
        catch (Exception) { /* 上下文还没好就只用 BaseDirectory */ }
        return list.ToArray();
    }

    private static FlowDocument BuildMessage(string message)
    {
        var document = new FlowDocument();
        document.Blocks.Add(new Paragraph(new Run(message)));
        return document;
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
    {
        _current = null;
        DocumentView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        HeadTitle.Text = Lang.T("String.Help.Title");
        HeadSubtitle.Text = Lang.T("String.Help.Subtitle");
    }

    private void OpenExternal_OnClick(object sender, RoutedEventArgs e)
    {
        var path = FindDocumentPath();
        if (path is null)
        {
            Notice.Info(Lang.T("String.Help.NotFound"));
            return;
        }
        TryOpen(path);
    }

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var path = FindDocumentPath();
        if (path is null)
        {
            Notice.Info(Lang.T("String.Help.NotFound"));
            return;
        }
        TryOpen(Path.GetDirectoryName(path)!);
    }

    private string? FindDocumentPath()
    {
        if (_current is null) return null;
        foreach (var language in new[] { CurrentLanguage, CurrentLanguage == LauncherLanguage.English ? LauncherLanguage.ZhHans : LauncherLanguage.English })
        {
            var relative = Path.Combine("docs", HelpDocuments.FolderName(language), _current.FileName(language));
            foreach (var root in Roots())
            {
                var full = Path.Combine(root, relative);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private static void TryOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception) { /* 打开失败忽略 */ }
    }
}
