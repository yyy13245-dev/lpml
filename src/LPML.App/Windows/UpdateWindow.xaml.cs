using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LPML.App.Services;
using LPML.App.Utils;
using LPML.Core;
using LPML.Core.Config;
using LPML.Core.Update;
using LPML.Core.Utils;

namespace LPML.App.Windows;

/// <summary>
/// 更新提示窗。important=1 时没有「稍后」、关闭按钮与 Esc/Alt+F4 都拦住，
/// 只能点「立即更新」；important=2/3 可以稍后或关闭。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateCheckResult _result;
    private readonly bool _mandatory;
    private bool _allowClose;
    private bool _busy;
    private bool _started;

    public UpdateWindow(UpdateCheckResult result, LauncherContext context)
    {
        InitializeComponent();
        _result = result;
        _mandatory = result.Manifest.Important == 1;

        var chinese = context.Settings.Language == LauncherLanguage.ZhHans;
        TitleText.Text = result.DisplayName(chinese);
        MetaText.Text = Lang.F("String.Update.Meta", result.Manifest.Id, result.Manifest.Num);
        LevelText.Text = result.Manifest.Important switch
        {
            1 => Lang.T("String.Update.LevelRequired"),
            2 => Lang.T("String.Update.LevelImportant"),
            _ => Lang.T("String.Update.LevelOptional")
        };

        var markdown = string.IsNullOrWhiteSpace(result.Introduce)
            ? Lang.T("String.Update.IntroEmpty")
            : result.Introduce;
        DocViewer.Document = MarkdownRenderer.Render(markdown);

        LineCombo.SelectedIndex = Math.Clamp(context.Settings.Update.DownloadLine, 0, 2);
        PackageCombo.SelectedIndex = Math.Clamp(context.Settings.Update.PackageKind, 0, 2);
        LaterButton.Visibility = _mandatory ? Visibility.Collapsed : Visibility.Visible;
        MandatoryHint.Visibility = _mandatory ? Visibility.Visible : Visibility.Collapsed;
        ReleaseButton.Visibility = IsHttps(result.Manifest.Github)
            ? Visibility.Visible : Visibility.Collapsed;

        StatusText.Text = Lang.T("String.Update.Hint");
        UpdateRuntimeInfo();

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_mandatory || _busy) return;
            _allowClose = true;
            Close();
        };
        Loaded += (_, _) => UpdateButton.Focus();
    }

    /// <summary>下拉切换时实时说明本次会下哪一类包；自动模式跟随当前安装。</summary>
    private void Package_OnChanged(object sender, SelectionChangedEventArgs e) => UpdateRuntimeInfo();

    private void UpdateRuntimeInfo()
    {
        var bundled = UpdateRuntimeDetector.HasBundledRuntime();
        var current = Lang.T(bundled ? "String.Update.RuntimeWith" : "String.Update.RuntimeWithout");
        var selected = new UpdateSettings { PackageKind = Math.Clamp(PackageCombo.SelectedIndex, 0, 2) };
        var withRuntime = UpdateService.ResolvePackageKind(selected, bundled);
        var target = Lang.T(withRuntime ? "String.Update.RuntimeWith" : "String.Update.RuntimeWithout");
        RuntimeInfoText.Text = Lang.F("String.Update.RuntimeInfo", current, target);
    }

    private static bool IsHttps(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>已经拉起暂存程序：允许窗口随启动器一起关闭。</summary>
    public void MarkUpdateStarted()
    {
        _started = true;
        _allowClose = true;
    }

    private void Later_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _allowClose = true;
        Close();
    }

    private void Release_OnClick(object sender, RoutedEventArgs e)
    {
        if (!IsHttps(_result.Manifest.Github)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_result.Manifest.Github) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Update", "打开发布页失败：" + ex.Message); }
    }

    private async void Update_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        LineCombo.IsEnabled = false;
        PackageCombo.IsEnabled = false;
        ProgressTrack.Visibility = Visibility.Visible;

        var progress = new Progress<(long Received, long Total)>(p => SetProgress(p.Received, p.Total));
        try
        {
            await UpdateCoordinator.ApplyAsync(this, _result, LineCombo.SelectedIndex,
                PackageCombo.SelectedIndex, text => StatusText.Text = text, progress).ConfigureAwait(true);
            StatusText.Text = Lang.T("String.Update.Ready");
        }
        catch (Exception ex)
        {
            Log.Warn("Update", "应用更新失败：" + ex.Message);
            StatusText.Text = Lang.F("String.Update.ApplyFailed", ex.Message);
            _busy = false;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            LineCombo.IsEnabled = true;
            PackageCombo.IsEnabled = true;
            ProgressTrack.Visibility = Visibility.Collapsed;
        }
    }

    private void SetProgress(long received, long total)
    {
        StatusText.Text = Lang.F("String.Update.Progress",
            FileHelper.HumanSize(received),
            total > 0 ? FileHelper.HumanSize(total) : "?");
        var width = total > 0 && ProgressTrack.ActualWidth > 0
            ? ProgressTrack.ActualWidth * (double)received / total
            : 0;
        ProgressFill.Width = Math.Max(4, Math.Min(ProgressTrack.ActualWidth, width));
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_started || _allowClose) return;
        // 强制更新或下载进行中：没有关闭按钮，Esc / Alt+F4 也不放行
        if (_mandatory || _busy) e.Cancel = true;
    }
}
