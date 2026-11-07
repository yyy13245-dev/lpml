using LPML.App.Services;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Multiplayer;
using LPML.Core.Utils;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LPML.App.Windows;

namespace LPML.App.Views;

/// <summary>
/// 联机入口页：下载联机核心 → 选「创建房间」或「加入房间」。
/// 只做入口选择，创建与加入各自是独立页面，不堆在一起。
/// </summary>
public sealed partial class MultiplayerPage : UserControl, IRefreshablePage
{
    private readonly LauncherContext _ctx;
    private readonly TerracottaCore _core;
    private readonly Progress<DownloadAggregate> _installProgress;
    private bool _busy;

    public MultiplayerPage(LauncherContext ctx)
    {
        _ctx = ctx;
        _core = new TerracottaCore(_ctx.Paths, _ctx.Http, _ctx.Settings.Terracotta);
        _installProgress = new Progress<DownloadAggregate>(OnInstallProgress);
        InitializeComponent();
        Loaded += OnLoaded;
        _core.StateUpdated += (_, _) => Dispatcher.InvokeAsync(Refresh);
    }

    private MainWindow? Host => Application.Current.MainWindow as MainWindow;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_ctx is not null) Refresh();
    }

    public void Refresh()
    {
        if (_ctx is null) return;
        var installed = _core.IsInstalled;
        CoreDot.Background = installed ? AccentBrush() : MutedBrush();
        CoreStatusText.Text = installed ? Lang.T("String.Multi.Installed") : Lang.T("String.Multi.NotInstalled");
        CoreVersionText.Text = installed && !string.IsNullOrEmpty(_core.InstalledVersion)
            ? Lang.F("String.Multi.VersionFormat", _core.InstalledVersion) +
              (_core.IsRunning ? " · " + StateText() : "")
            : "";
        CoreActionButton.Content = installed
            ? Lang.T("String.Multi.UninstallCore")
            : Lang.T("String.Multi.DownloadCore");

        var version = string.IsNullOrWhiteSpace(_ctx.Settings.Terracotta.VersionOverride)
            ? TerracottaDefaults.Version
            : _ctx.Settings.Terracotta.VersionOverride.Trim();
        DefaultRouteText.Text = Lang.F("String.Multi.DefaultRoute", version, TerracottaPlatform.Classifier());
    }

    private string StateText() => _core.StateKind switch
    {
        TerracottaStateKind.HostScanning => Lang.T("String.Multi.StateHostScanning"),
        TerracottaStateKind.HostStarting => Lang.T("String.Multi.StateHostStarting"),
        TerracottaStateKind.HostOk => Lang.T("String.Multi.StateHostOk"),
        TerracottaStateKind.GuestConnecting => Lang.T("String.Multi.StateGuestConnecting"),
        TerracottaStateKind.GuestStarting => Lang.T("String.Multi.StateGuestStarting"),
        TerracottaStateKind.GuestOk => Lang.T("String.Multi.StateGuestOk"),
        TerracottaStateKind.Exception => Lang.F("String.Multi.StateException", ""),
        _ => Lang.T("String.Multi.StateWaiting")
    };

    private static Brush AccentBrush()
        => Application.Current.TryFindResource("Brush.Accent") as Brush ?? Brushes.DodgerBlue;

    private static Brush MutedBrush()
        => Application.Current.TryFindResource("Brush.ForegroundMuted") as Brush ?? Brushes.Gray;

    /// <summary>
    /// 关闭窗口 / 离开本页前的确认：房间还开着就先问一次，确定才关。
    /// 与 MainWindow.CanNavigateAway 配套。
    /// </summary>
    public bool ConfirmLeave()
    {
        if (!_core.IsRunning) return true;
        if (!Notice.Ask(Lang.F("String.Multi.ConfirmLeave", Lang.T("String.Multi.CreateRoom")))) return false;
        _core.Stop();
        return true;
    }

    private void CoreConfigure_OnClick(object? sender, RoutedEventArgs e)
        => new MultiplayerCoreDialog(_ctx).ShowDialog();

    private async void CoreAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_core.IsInstalled)
        {
            if (!Notice.Ask(Lang.T("String.Multi.ConfirmUninstall"))) return;
            if (_core.IsRunning) _core.Stop();
            _core.Uninstall();
            Refresh();
            return;
        }

        // 默认路线：没填地址也能装（国内镜像优先，元数据失败会回退内置模板）。
        _busy = true;
        CoreActionButton.IsEnabled = false;
        CoreProgressBorder.Visibility = Visibility.Visible;
        CoreErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await _core.InstallAsync(
                expectedChecksum: _ctx.Settings.Terracotta.ExpectedChecksum,
                progress: _installProgress,
                token: CancellationToken.None).ConfigureAwait(true);
            Refresh();
        }
        catch (OperationCanceledException)
        {
            ShowError(Lang.T("String.Multi.Err.Canceled"));
        }
        catch (Exception ex)
        {
            ShowError(Lang.F("String.Multi.Err.InstallFailed", ex.Message));
        }
        finally
        {
            _busy = false;
            CoreActionButton.IsEnabled = true;
        }
    }

    private void ShowError(string text)
    {
        CoreErrorText.Text = text;
        CoreErrorText.Visibility = Visibility.Visible;
    }

    private void OnInstallProgress(DownloadAggregate aggregate)
    {
        CoreProgressBar.Value = aggregate.TotalBytes > 0
            ? aggregate.ReceivedBytes * 100.0 / aggregate.TotalBytes
            : 0;
        CoreProgressText.Text = aggregate.TotalBytes > 0
            ? $"{FileHelper.HumanSize(aggregate.ReceivedBytes)} / {FileHelper.HumanSize(aggregate.TotalBytes)}"
            : aggregate.Summary;
    }

    private void GoCreate_OnClick(object? sender, RoutedEventArgs e)
        => Host?.NavigateToContent(new MultiplayerCreatePage(Host, _ctx, _core));

    private void GoJoin_OnClick(object? sender, RoutedEventArgs e)
        => Host?.NavigateToContent(new MultiplayerJoinPage(Host, _ctx, _core));
}
