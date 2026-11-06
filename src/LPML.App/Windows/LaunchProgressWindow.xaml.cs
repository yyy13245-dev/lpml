using System.ComponentModel;
using System.Threading;
using System.Windows;
using LPML.Core;
using LPML.Core.Download;

namespace LPML.App.Windows;

/// <summary>
/// 启动进度窗口：依赖补全 → 登录 → 准备 → 启动 → 直到 MC 输出"进入主界面"的日志才关闭。
/// 解决"提示启动成功但游戏窗口还没出来"的落差感。
/// </summary>
public partial class LaunchProgressWindow : Window
{
    private CancellationTokenSource _cts = new();
    private bool _closed;

    public LaunchProgressWindow(string versionText)
    {
        InitializeComponent();
        VersionText.Text = versionText;
    }

    public CancellationToken Token => _cts.Token;

    // ── 阶段控制 ────────────────────────────────────────────

    public void SetStage(int stage)
    {
        Dispatcher.Invoke(() =>
        {
            SetIcon(Icon1, stage, 1);
            SetIcon(Icon2, stage, 2);
            SetIcon(Icon3, stage, 3);
            SetIcon(Icon4, stage, 4);
        });
    }

    private static void SetIcon(System.Windows.Controls.TextBlock icon, int current, int mine)
    {
        icon.Text = current > mine ? "✔" : (current == mine ? "◉" : "○");
        icon.Foreground = current > mine
            ? System.Windows.Media.Brushes.MediumSeaGreen
            : (current == mine
                ? (System.Windows.Media.Brush)icon.FindResource("Brush.Accent")
                : (System.Windows.Media.Brush)icon.FindResource("Brush.ForegroundMuted"));
    }

    public void SetDetail(int stage, string text)
    {
        Dispatcher.Invoke(() =>
        {
            var target = stage switch { 1 => Detail1, 2 => Detail2, 3 => Detail3, _ => Detail4 };
            target.Text = text;
        });
    }

    /// <summary>第 1 阶段：显示依赖下载进度与速度。</summary>
    public void SetDownloadProgress(DownloadAggregate aggregate)
    {
        Dispatcher.Invoke(() =>
        {
            DepBar.Visibility = Visibility.Visible;
            DepSpeed.Visibility = Visibility.Visible;
            DepBar.Value = aggregate.Progress;
            DepSpeed.Text = aggregate.Summary;
        });
    }

    /// <summary>启动中：显示 MC 日志尾部，让用户知道游戏在动。</summary>
    public void AppendLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.Visibility = Visibility.Visible;
            var text = (LogTail.Text + "\n" + line).Trim();
            var lines = text.Split('\n');
            LogTail.Text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 6)));
        });
    }

    public void RequestClose() => Dispatcher.Invoke(() => { if (!_closed) { _closed = true; Close(); } });

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        RequestClose();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closed = true;
        base.OnClosing(e);
    }
}
