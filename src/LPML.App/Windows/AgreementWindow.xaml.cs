using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using LPML.App.Utils;
using LPML.Core;
using LPML.Core.Config;

namespace LPML.App.Windows;

/// <summary>
/// 首次启动（或协议版本更新后）显示的用户协议窗口。
/// 正文直接用内置 Markdown 渲染器，跟「文档」页是同一套排版。
/// 同意状态写进 launcher.json 的 AcceptedAgreementVersion，不上传、不写注册表。
/// </summary>
public partial class AgreementWindow : Window
{
    /// <summary>协议版本。改动后所有用户下次启动都会重新看到协议。</summary>
    public const int CurrentVersion = 1;

    private bool _accepted;

    public bool Accepted => _accepted;

    public AgreementWindow(string markdown)
    {
        InitializeComponent();

        try
        {
            DocViewer.Document = MarkdownRenderer.Render(markdown, 13.5);
        }
        catch (Exception)
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph(new Run(L.T("String.Agreement.FallbackBody"))));
            DocViewer.Document = doc;
        }

        // Esc 等同「不同意并退出」；关闭窗口也按拒绝处理
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _accepted = false; Close(); }
        };
        Loaded += (_, _) => AcceptButton.Focus();
    }

    private void Accept_OnClick(object sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void Decline_OnClick(object sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }

    /// <summary>
    /// 图形界面启动前调用。返回 false 表示用户拒绝，调用方必须结束进程。
    /// 协议文件缺失时用内置兜底文本，绝不因文档问题导致启动器打不开。
    /// </summary>
    public static bool EnsureAccepted(LauncherContext context)
    {
        if (context.Settings.AcceptedAgreementVersion >= CurrentVersion) return true;

        var markdown = ReadAgreement(context) ?? FallbackMarkdown();
        var window = new AgreementWindow(markdown);

        // 此时真正的主窗口还没建。WPF 可能把第一个显示的窗口当成 MainWindow，
        // 而关闭时当前是 OnMainWindowClose —— 那样点「同意」后应用会直接退出。
        // 显示协议期间临时改成显式退出，之后恢复并让 App 显式指定 MainWindow。
        var app = Application.Current;
        var previousMode = app?.ShutdownMode;
        if (app is not null) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { window.ShowDialog(); }
        finally
        {
            if (app is not null && previousMode is { } mode) app.ShutdownMode = mode;
        }

        if (!window.Accepted)
        {
            Log.Warn("App", L.T("String.Agreement.DeclinedLog"));
            return false;
        }

        context.Settings.AcceptedAgreementVersion = CurrentVersion;
        context.Save();
        Log.Info("App", L.F("String.Agreement.AcceptedLog", CurrentVersion));
        return true;
    }

    private static string? ReadAgreement(LauncherContext context)
    {
        var language = context.Settings.Language == LauncherLanguage.English ? "en-US" : "zh-CN";
        var other = language == "en-US" ? "zh-CN" : "en-US";

        var roots = new List<string>();
        try { roots.Add(context.Paths.DocsDirectory); } catch (Exception) { }
        roots.Add(Path.Combine(AppContext.BaseDirectory, "docs"));
        try { roots.Add(Path.Combine(context.Paths.AppDirectory, "docs")); } catch (Exception) { }

        // 当前语言优先，缺失时回退另一种语言
        foreach (var folder in new[] { language, other })
        {
            foreach (var root in roots)
            {
                try
                {
                    var full = Path.Combine(root, folder, "agreement.md");
                    if (File.Exists(full)) return File.ReadAllText(full);
                }
                catch (Exception) { /* 换下一个候选 */ }
            }
        }
        return null;
    }

    private static string FallbackMarkdown()
        => "# " + L.T("String.Agreement.Title") + "\n\n> " + L.T("String.Agreement.FallbackTitle")
           + "\n\n" + L.T("String.Agreement.FallbackBody") + "\n";
}
