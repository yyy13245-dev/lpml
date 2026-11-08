using LPML.App.Controls;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Multiplayer;
using System;
using System.Windows;
using System.Windows.Controls;
using LPML.App.Windows;

namespace LPML.App.Views;

/// <summary>
/// 加入房间：把 HMCL / PCL CE / LPML 的 U/XXXX-XXXX-XXXX-XXXX 邀请码交给陶瓦核心，
/// 由它完成 P2P 组网。加入成功后在游戏「多人游戏」列表里会出现陶瓦联机大厅。
/// </summary>
public sealed partial class MultiplayerJoinPage : UserControl, IDisposable
{
    private bool _ready;
    private readonly MainWindow _main;
    private readonly LauncherContext _ctx;
    private readonly TerracottaCore _core;

    public MultiplayerJoinPage(MainWindow main, LauncherContext ctx, TerracottaCore core)
    {
        _ctx = ctx;
        _core = core;
        _main = main;
        InitializeComponent();
        _ready = true;
        Picker.Refresh();
        Picker.SetAutoJoin(false, null);
        Picker.Initialize(_ctx, OnInstanceLaunched);
        Loaded += OnLoaded;
        _core.StateUpdated += (_, state) => Dispatcher.InvokeAsync(() => OnState(state));
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_ctx.Settings.Terracotta.LastRoomCode) &&
            string.IsNullOrWhiteSpace(CodeBox.Text))
            CodeBox.Text = _ctx.Settings.Terracotta.LastRoomCode;
        UpdateNotice();
    }

    private void CodeBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (JoinStatusText is null || Picker is null) return;
        JoinStatusText.Text = Lang.T("String.Multi.CodeNeedResolve");
        UpdateNotice();
    }

    private async void Join_OnClick(object? sender, RoutedEventArgs e)
    {
        var code = TerracottaCore.NormalizeRoomCode(CodeBox.Text);
        if (code.Length == 0)
        {
            Notice.Warn(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.CodeInvalid"));
            return;
        }

        JoinButton.IsEnabled = false;
        _ctx.Settings.Terracotta.LastRoomCode = code;
        _ctx.Settings.Terracotta.LastMode = TerracottaSessionMode.Join;
        _ctx.Save();

        try
        {
            // 校验位只作提示，不作为拒绝依据：最终由陶瓦核心判定，避免误拒第三方启动器生成的码。
            JoinStatusText.Text = TerracottaRoomCode.HasValidChecksum(code)
                ? Lang.T("String.Multi.Joining")
                : Lang.T("String.Multi.ChecksumWarning");

            var player = _ctx.Settings.Terracotta.PlayerName?.Trim();
            if (string.IsNullOrWhiteSpace(player)) player = Environment.UserName;

            await _core.JoinRoomAsync(code, player).ConfigureAwait(true);
            JoinStatusText.Text = Lang.T("String.Multi.StateGuestConnecting");
            UpdateNotice();
            _main.NavigateToContent(new MultiplayerRoomPage(_main, _ctx, _core, TerracottaSessionMode.Join));
        }
        catch (OperationCanceledException)
        {
            JoinStatusText.Text = Lang.T("String.Multi.Err.Canceled");
        }
        catch (Exception ex)
        {
            LPML.Core.Log.Warn("Multi", $"加入房间失败：{ex.GetType().Name}: {ex.Message}");
            JoinStatusText.Text = Lang.F("String.Multi.Err.ResolveDetail", ex.Message);
            if (MultiplayerCoreRetry.NeedsRestart(_core, _core.IsInstalled))
                ShowRestartFailure(ex.Message);
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }

    private void OnState(TerracottaStateSnapshot state)
    {
        JoinStatusText.Text = StateText(state.Kind, state.Error);
        if (state.Kind != TerracottaStateKind.GuestOk) return;

        // 官方用法：房主在游戏里「对局域网开放」后，加入方的多人游戏列表会出现大厅。
        var url = _core.GuestUrl;
        JoinNoticeText.Text = string.IsNullOrWhiteSpace(url)
            ? Lang.T("String.Multi.JoinOkNoUrl")
            : Lang.F("String.Multi.JoinOk", url);
    }

    private static string StateText(TerracottaStateKind kind, string? error) => kind switch
    {
        TerracottaStateKind.CoreStarting => Lang.T("String.Multi.CoreStarting"),
        TerracottaStateKind.HostScanning => Lang.T("String.Multi.StateHostScanning"),
        TerracottaStateKind.HostStarting => Lang.T("String.Multi.StateHostStarting"),
        TerracottaStateKind.HostOk => Lang.T("String.Multi.StateHostOk"),
        TerracottaStateKind.GuestConnecting => Lang.T("String.Multi.StateGuestConnecting"),
        TerracottaStateKind.GuestStarting => Lang.T("String.Multi.StateGuestStarting"),
        TerracottaStateKind.GuestOk => Lang.T("String.Multi.StateGuestOk"),
        TerracottaStateKind.Exception => Lang.F("String.Multi.StateException", error ?? ""),
        _ => Lang.T("String.Multi.StateWaiting")
    };

    private void OnInstanceLaunched(string instanceName)
        => JoinStatusText.Text = Lang.F("String.Multi.JoinLaunched", instanceName);

    private void UpdateNotice()
    {
        JoinNoticeText.Text = string.IsNullOrWhiteSpace(CodeBox.Text)
            ? Lang.T("String.Multi.OneClickNoticePending")
            : Lang.T("String.Multi.RoomCodeFormat");
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

    private void Back_OnClick(object? sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (_core.IsRunning && _core.HasActiveRoom &&
            !Notice.Ask(Lang.F("String.Multi.ConfirmLeave", Lang.T("String.Multi.JoinRoom")))) return;
        _ = _core.ExitRoomAsync();
        PendingSessionStore.RequestedAddress = null;
        _main.ShowMultiplayer();
    }

    // 核心实例由联机入口页创建并共享给子页，这里不释放，避免返回后失效。
    public void Dispose() { }
}
