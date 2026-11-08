using LPML.App.Controls;
using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Instances;
using LPML.Core.Launch;
using LPML.Core.Multiplayer;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LPML.App.Views;

/// <summary>
/// 创建房间：端口从游戏日志里识别（语言无关），识别错了可以直接改；
/// 成功之后跳转到独立的房间页，本页不再显示创建按钮。
/// </summary>
public sealed partial class MultiplayerCreatePage : UserControl, IDisposable
{
    private readonly LauncherContext _ctx;
    private readonly MainWindow _main;
    private readonly TerracottaCore _core;
    private readonly EventHandler<string> _onGameLine;

    public MultiplayerCreatePage(MainWindow main, LauncherContext ctx, TerracottaCore core)
    {
        _ctx = ctx;
        _core = core;
        _main = main;
        InitializeComponent();
        Picker.Initialize(_ctx, OnInstanceLaunched);

        _onGameLine = (_, line) =>
        {
            // 实时自动填入只认高置信度的方括号规则；兜底规则交给「填入检测到的」按钮。
            if (!GamePortDetector.TryDetectBracket(line, out var port)) return;
            Dispatcher.InvokeAsync(() => FillPort(port));
        };

        Loaded += (_, _) =>
        {
            var last = !string.IsNullOrWhiteSpace(_ctx.Settings.SelectedInstanceId)
                ? _ctx.Instances.Find(_ctx.Settings.SelectedInstanceId)
                : null;
            if (last is not null) Picker.SetSelected(last);
            GameOutputHub.LinePushed += _onGameLine;
            ScanExisting();
        };
        Unloaded += (_, _) => GameOutputHub.LinePushed -= _onGameLine;

        _core.StateUpdated += (_, state) => Dispatcher.InvokeAsync(() =>
            LaunchStatusDetail.Text = StateText(state.Kind));
    }

    private static string StateText(TerracottaStateKind kind) => kind switch
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

    /// <summary>实例已启动：开始监听游戏日志，并先扫一遍已有日志。</summary>
    private void OnInstanceLaunched(string instanceName)
    {
        LaunchStatusBorder.Visibility = Visibility.Visible;
        LaunchStatusText.Text = Lang.F("String.Multi.LaunchSuccess", instanceName);
        LaunchStatusDetail.Text = Lang.T("String.Multi.DetectingPort");
        ScanExisting();
    }

    private void FillPort(int port)
    {
        PortBox.Text = port.ToString(CultureInfo.InvariantCulture);
        LaunchStatusBorder.Visibility = Visibility.Visible;
        LaunchStatusDetail.Text = Lang.F("String.Multi.PortDetected", PortBox.Text);
        LaunchStatusDetail.Foreground =
            Application.Current.TryFindResource("Brush.Accent") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.DodgerBlue;
    }

    private void ScanExisting()
    {
        var port = GamePortDetector.Scan(GameOutputHub.Recent());
        if (port.HasValue) FillPort(port.Value);
    }

    private void FillDetected_OnClick(object? sender, RoutedEventArgs e)
    {
        var port = GamePortDetector.Scan(GameOutputHub.Recent());
        if (port.HasValue) FillPort(port.Value);
        else Notice.Info(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.NoDetectionYet"));
    }

    private async void Generate_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!GenerateButton.IsEnabled) return;
        GenerateButton.IsEnabled = false;
        try
        {
            var raw = PortBox.Text.Trim();
            var port = 0;
            if (raw.Length > 0)
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                    port is <= 0 or > 65535)
                {
                    Notice.Warn(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.PortInvalid"));
                    return;
                }
            }

            var fixedCode = _ctx.Settings.Terracotta.FixedRoomCode?.Trim();
            var player = _ctx.Settings.Terracotta.PlayerName?.Trim();
            if (string.IsNullOrWhiteSpace(player)) player = Environment.UserName;

            await _core.CreateRoomAsync(string.IsNullOrWhiteSpace(fixedCode) ? null : fixedCode, player, port)
                .ConfigureAwait(true);

            _ctx.Settings.Terracotta.LastMode = TerracottaSessionMode.Create;
            _ctx.Save();
            _main.NavigateToContent(new MultiplayerRoomPage(_main, _ctx, _core, TerracottaSessionMode.Create));
        }
        catch (OperationCanceledException)
        {
            Notice.Info(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.Canceled"));
        }
        catch (Exception ex)
        {
            Log.Warn("Multi", $"创建房间失败：{ex.GetType().Name}: {ex.Message}");
            Notice.Warn(Lang.T("String.Multi.Title"), ex.Message);
            if (MultiplayerCoreRetry.NeedsRestart(_core, _core.IsInstalled))
                ShowRestartFailure(ex.Message);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private void ShowRestartFailure(string message)
    {
        RestartPanel.Visibility = Visibility.Visible;
        RestartButton.Visibility = Visibility.Visible;
        RestartStatusText.Text = message;
    }

    private async void RestartCore_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!RestartButton.IsEnabled) return;
        RestartStatusText.Text = Lang.T("String.Multi.RestartingCore");
        await MultiplayerCoreRetry.RunAsync(
            _core,
            busy => { RestartButton.IsEnabled = !busy; },
            () =>
            {
                RestartStatusText.Text = Lang.T("String.Multi.RestartCoreOk");
                RestartButton.Visibility = Visibility.Collapsed;
            },
            message => ShowRestartFailure(message)).ConfigureAwait(true);
    }

    private void Back_OnClick(object? sender, RoutedEventArgs e)
    {
        PendingSessionStore.RequestedAddress = null;
        _main.ShowMultiplayer();
    }

    // 核心实例由联机入口页创建并共享给子页，这里不释放，避免返回后失效。
    public void Dispose() => GameOutputHub.LinePushed -= _onGameLine;
}
