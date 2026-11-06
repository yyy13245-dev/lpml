using LPML.App.Services;
using LPML.Core;
using LPML.Core.Launch;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using LPML.App.Windows;

namespace LPML.App.Windows;

/// <summary>日志行视图模型：把解析结果转成界面能直接绑定的形状。</summary>
public sealed class LogLineView
{
    public GameLogLevel Level { get; init; }
    public string TimeText { get; init; } = "";
    public string LevelText { get; init; } = "";
    public string ThreadText { get; init; } = "";
    public string Message { get; init; } = "";
    public string Raw { get; init; } = "";
    public Brush LevelBrush { get; init; } = Brushes.Transparent;

    public static LogLineView From(GameLogLine line)
    {
        var brush = line.Level switch
        {
            GameLogLevel.Fatal => SafeBrush("Brush.Danger"),
            GameLogLevel.Error => SafeBrush("Brush.Danger"),
            GameLogLevel.Warn => SafeBrush("Brush.Warning"),
            GameLogLevel.Debug or GameLogLevel.Trace => SafeBrush("Brush.ForegroundMuted"),
            _ => SafeBrush("Brush.Foreground")
        };

        return new LogLineView
        {
            Level = line.Level,
            TimeText = line.Time.ToString("HH:mm:ss"),
            LevelText = GameLogMonitor.LevelText(line.Level),
            ThreadText = line.Thread,
            Message = line.Message,
            Raw = line.Raw,
            LevelBrush = line.IsCrashMarker ? SafeBrush("Brush.Danger") : brush
        };
    }

    private static Brush SafeBrush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>
/// 游戏日志窗口：实时捕获游戏 stdout/stderr，支持按级别筛选、关键字搜索、
/// 自动滚动、复制整行 / 复制全部、导出到文件。
/// </summary>
public partial class GameLogWindow : Window
{
    private readonly ObservableCollection<LogLineView> _all = new();
    private readonly ObservableCollection<LogLineView> _view = new();
    private GameLogLevel _minimum = GameLogLevel.Trace;
    private string _keyword = "";
    private int _total;
    private readonly List<int> _ports = new();

    public GameLogWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _view;
        TitleText.Text = Lang.T("String.GameLog.Title");
        RunningBadgeText.Text = Lang.T("String.GameLog.Running");
        UpdateStatus();
    }

    /// <summary>游戏进程结束后调用，去掉「运行中」标记。</summary>
    public void MarkStopped()
    {
        RunningBadge.Visibility = Visibility.Collapsed;
        UpdateStatus();
    }

    public void Append(GameLogLine line)
    {
        _total++;
        var view = LogLineView.From(line);
        _all.Add(view);
        if (Matches(view))
        {
            _view.Add(view);
            Trim();
            if (AutoScrollCheck.IsChecked == true && _view.Count > 0)
                LogList.ScrollIntoView(_view[^1]);
        }
        else Trim();

        UpdateStatus();
    }

    public void AddPort(int port)
    {
        if (_ports.Contains(port)) return;
        _ports.Add(port);
        PortText.Text = Lang.F("String.GameLog.PortsOpened", string.Join(", ", _ports));
    }

    private void Trim()
    {
        const int cap = 5000;
        while (_view.Count > cap) _view.RemoveAt(0);
        while (_all.Count > cap) _all.RemoveAt(0);
    }

    private bool Matches(LogLineView view)
    {
        if (view.Level < _minimum) return false;
        if (string.IsNullOrWhiteSpace(_keyword)) return true;
        return view.Raw.Contains(_keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void Rebuild()
    {
        _view.Clear();
        foreach (var item in _all)
            if (Matches(item)) _view.Add(item);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = Lang.F("String.GameLog.Status", _view.Count, _total);
    }

    // ── 交互 ────────────────────────────────────────────────

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_OnClick(object sender, RoutedEventArgs e) => Hide();

    private void LevelCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _minimum = LevelCombo.SelectedIndex switch
        {
            1 => GameLogLevel.Info,
            2 => GameLogLevel.Warn,
            3 => GameLogLevel.Error,
            4 => GameLogLevel.Fatal,
            _ => GameLogLevel.Trace
        };
        Rebuild();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _keyword = SearchBox.Text.Trim();
        Rebuild();
    }

    private void AutoScrollCheck_OnChanged(object sender, RoutedEventArgs e)
    {
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (LogList is null) return;
        if (AutoScrollCheck.IsChecked == true && _view.Count > 0) LogList.ScrollIntoView(_view[^1]);
    }

    private void Clear_OnClick(object sender, RoutedEventArgs e)
    {
        _all.Clear();
        _view.Clear();
        _total = 0;
        _ports.Clear();
        PortText.Text = "";
        UpdateStatus();
    }

    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L.T("String.Msg.Log.FileFilter"),
            FileName = $"game-{DateTime.Now:yyyyMMdd-HHmmss}.log"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllLines(dialog.FileName, _view.Select(v => v.Raw));
            Notice.Info(Lang.T("String.GameLog.Exported"));
        }
        catch (IOException ex)
        {
            Notice.Error(Lang.F("String.GameLog.ExportFailed", ex.Message));
        }
    }

    private void LogList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中即复制整行，方便直接粘贴报错
        if (LogList.SelectedItem is LogLineView view && Keyboard.Modifiers == ModifierKeys.Control)
            TrySetClipboard(view.Raw);
    }

    private void CopyLine_OnClick(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is LogLineView view) TrySetClipboard(view.Raw);
    }

    private void CopyAll_OnClick(object sender, RoutedEventArgs e)
        => TrySetClipboard(string.Join(Environment.NewLine, _view.Select(v => v.Raw)));

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
