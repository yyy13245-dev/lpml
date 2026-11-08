using LPML.App.Services;
using LPML.Core;
using LPML.Core.Download;
using LPML.Core.Multiplayer;
using LPML.Core.Utils;
using System;
using System.Threading;
using System.Threading.Tasks;
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
    private bool _coreStartFailed;
    private bool _restarting;
    // 本次启动启动器只预热一次；static 让反复进出联机页也不会重复唤醒陶瓦。
    private static readonly object WarmLock = new();
    private static bool _warmStarted;

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
        if (_ctx is null) return;
        Refresh();
        TryWarmCoreOnce();
    }

    /// <summary>
    /// 本次启动启动器后第一次打开联机功能时唤醒陶瓦：把等待提前到进页面时完成，
    /// 期间界面显示「核心启动中」，之后点创建/加入房间即可直接用。
    /// 核心未安装时什么都不做（不替用户自动下载）；预热失败只记日志与页面提示，不弹窗打断。
    /// </summary>
    private void TryWarmCoreOnce()
    {
        lock (WarmLock)
        {
            if (_warmStarted) return;
            if (!_core.IsInstalled) return;
            if (_core.IsRunning && _core.ApiPort > 0) return;
            _warmStarted = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                _core.EnsureDaemon();
                LPML.Core.Log.Info("Multi", "首次打开联机功能，核心预热完成");
                Dispatcher.InvokeAsync(() =>
                {
                    _coreStartFailed = false;
                    CoreErrorText.Visibility = Visibility.Collapsed;
                    Refresh();
                });
            }
            catch (Exception ex)
            {
                LPML.Core.Log.Warn("Multi", $"首次预热联机核心失败：{ex.GetType().Name}: {ex.Message}");
                Dispatcher.InvokeAsync(() =>
                {
                    // 期间用户可能已经点了「尝试重新启动」并成功，别把过期错误盖回去。
                    if (!MultiplayerCoreRetry.NeedsRestart(_core, _core.IsInstalled)) return;
                    ShowStartFailure(Lang.F("String.Multi.Err.WarmFailed", ex.Message));
                    Refresh();
                });
            }
        });
    }

    public void Refresh()
    {
        if (_ctx is null) return;
        var installed = _core.IsInstalled;
        var starting = _core.StateKind == TerracottaStateKind.CoreStarting;
        CoreDot.Background = (installed || starting) ? AccentBrush() : MutedBrush();
        CoreStatusText.Text = starting
            ? Lang.T("String.Multi.CoreStarting")
            : installed ? Lang.T("String.Multi.Installed") : Lang.T("String.Multi.NotInstalled");
        CoreVersionText.Text = starting
            ? Lang.T("String.Multi.CoreStartingHint")
            : installed && !string.IsNullOrEmpty(_core.InstalledVersion)
                ? Lang.F("String.Multi.VersionFormat", _core.InstalledVersion) +
                  (_core.IsRunning ? " · " + StateText() : "")
                : "";
        CoreActionButton.Content = installed
            ? Lang.T("String.Multi.UninstallCore")
            : Lang.T("String.Multi.DownloadCore");
        CoreActionButton.IsEnabled = !starting && !_busy;

        var version = string.IsNullOrWhiteSpace(_ctx.Settings.Terracotta.VersionOverride)
            ? TerracottaDefaults.Version
            : _ctx.Settings.Terracotta.VersionOverride.Trim();
        DefaultRouteText.Text = Lang.F("String.Multi.DefaultRoute", version, TerracottaPlatform.Classifier());

        // 启动失败、核心上报异常，或预热过但进程已退出 / 端口未就绪时，给出重试入口。
        var canRestart = installed && !starting && !_restarting
            && (_warmStarted || _coreStartFailed)
            && MultiplayerCoreRetry.NeedsRestart(_core, installed);
        CoreRestartRow.Visibility = canRestart ? Visibility.Visible : Visibility.Collapsed;
        if (canRestart) CoreRestartHint.Text = Lang.T("String.Multi.RestartCoreHint");
    }

    private string StateText() => _core.StateKind switch
    {
        TerracottaStateKind.CoreStarting => Lang.T("String.Multi.CoreStarting"),
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
        // 只有真正开着房间才拦一下；预热 / 待机直接放行，核心由官方空闲超时自行退出。
        if (!_core.IsRunning || !_core.HasActiveRoom) return true;
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
            // Stop 会等预热/启动走完，放到线程池里，别把界面冻住。
            if (_core.IsRunning) await Task.Run(() => _core.Stop()).ConfigureAwait(true);
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
            TryWarmCoreOnce();   // 装完立刻唤醒，状态栏显示「核心启动中」
        }
        catch (OperationCanceledException)
        {
            ShowError(Lang.T("String.Multi.Err.Canceled"));
        }
        catch (Exception ex)
        {
            LPML.Core.Log.Warn("Multi", $"联机核心安装失败：{ex.GetType().Name}: {ex.Message}");
            ShowError(Lang.F("String.Multi.Err.InstallFailed", ex.Message));
        }
        finally
        {
            _busy = false;
            CoreActionButton.IsEnabled = _core.StateKind != TerracottaStateKind.CoreStarting;
        }
    }

    /// <summary>安装 / 取消一类与核心启动无关的错误：不显示重启按钮。</summary>
    private void ShowError(string text)
    {
        _coreStartFailed = false;
        CoreErrorText.Text = text;
        CoreErrorText.Visibility = Visibility.Visible;
        CoreRestartRow.Visibility = Visibility.Collapsed;
    }

    /// <summary>核心启动失败：保留错误文字并显示「尝试重新启动联机核心」。</summary>
    private void ShowStartFailure(string text)
    {
        _coreStartFailed = true;
        CoreErrorText.Text = text;
        CoreErrorText.Visibility = Visibility.Visible;
    }

    private async void CoreRestart_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_restarting || _busy) return;
        CoreRestartRow.Visibility = Visibility.Collapsed;
        CoreErrorText.Text = Lang.T("String.Multi.RestartingCore");
        CoreErrorText.Visibility = Visibility.Visible;

        await MultiplayerCoreRetry.RunAsync(
            _core,
            busy => { _restarting = busy; CoreRestartButton.IsEnabled = !busy; },
            () =>
            {
                _coreStartFailed = false;
                CoreErrorText.Visibility = Visibility.Collapsed;
                Refresh();
            },
            message =>
            {
                ShowStartFailure(message);
                Refresh();
            }).ConfigureAwait(true);

        Refresh();
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
