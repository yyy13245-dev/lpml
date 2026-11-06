using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LPML.Core;

namespace LPML.App.Windows;

/// <summary>
/// 启动器自己的提示窗。
/// 系统 MessageBox 在 Warning / Error 图标下会播放 Windows 的"叮"声，
/// 听着像程序崩了，其实只是普通提示。这里改成自绘窗口：有图标、无声音。
/// </summary>
public partial class NoticeWindow : Window
{
    public bool Accepted { get; private set; }

    public NoticeWindow(string title, string message, NoticeKind kind, bool ask)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "LPML" : title;
        MessageText.Text = message ?? "";

        // 图标用文字符号，不用系统图标（也就不会有系统提示音）
        IconText.Text = kind switch
        {
            NoticeKind.Error => "⛔",
            NoticeKind.Warning => "⚠",
            _ => "ℹ"
        };
        IconText.Foreground = kind switch
        {
            NoticeKind.Error => (Brush)FindResource("Brush.Danger"),
            NoticeKind.Warning => (Brush)FindResource("Brush.Warning"),
            _ => (Brush)FindResource("Brush.Accent")
        };

        if (ask)
        {
            NoButton.Visibility = Visibility.Visible;
            NoButton.Content = L.T("String.Common.No");
            OkButton.Content = L.T("String.Common.Yes");
        }

        // 标题栏拖动 + Esc / Enter
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Accepted = false; Close(); }
            else if (e.Key == Key.Enter) { Accepted = true; Close(); }
        };

        Loaded += (_, _) => OkButton.Focus();
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void No_OnClick(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}

public enum NoticeKind
{
    Info,
    Warning,
    Error
}

/// <summary>统一的提示入口，替换 MessageBox.Show。</summary>
public static class Notice
{
    /// <summary>普通提示（无声音）。</summary>
    public static void Info(string message) => Show(message, null, NoticeKind.Info, false);

    /// <summary>需要注意但不算错误（无声音）。</summary>
    public static void Warn(string message) => Show(message, null, NoticeKind.Warning, false);

    /// <summary>出错了（无声音）。</summary>
    public static void Error(string message) => Show(message, null, NoticeKind.Error, false);

    public static void Info(string title, string message) => Show(message, title, NoticeKind.Info, false);

    public static void Warn(string title, string message) => Show(message, title, NoticeKind.Warning, false);

    public static void Error(string title, string message) => Show(message, title, NoticeKind.Error, false);

    /// <summary>问一个是/否的问题（无声音）。</summary>
    public static bool Ask(string message) => Show(message, null, NoticeKind.Warning, true);

    public static bool Ask(string title, string message) => Show(message, title, NoticeKind.Warning, true);

    private static bool Show(string message, string? title, NoticeKind kind, bool ask)
    {
        Window? owner = null;
        try { owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive); }
        catch (Exception) { /* 取不到就算了 */ }

        var window = new NoticeWindow(title ?? TitleFor(kind), message, kind, ask);
        if (owner is not null && !ReferenceEquals(owner, window))
        {
            try { window.Owner = owner; } catch (Exception) { }
        }
        window.ShowDialog();
        return window.Accepted;
    }

    private static string TitleFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Error => L.T("String.Common.ErrorTitle"),
        NoticeKind.Warning => L.T("String.Common.WarnTitle"),
        _ => L.T("String.Common.InfoTitle")
    };
}
