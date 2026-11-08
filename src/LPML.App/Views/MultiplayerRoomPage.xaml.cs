using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Multiplayer;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LPML.App.Views;

/// <summary>
/// 房间页：创建 / 加入成功后跳转到这里，创建与加入按钮不再留在页面上。
/// 「关闭房间 / 退出房间」与「返回」都会结束当前房间；区别只在文案。
/// </summary>
public sealed partial class MultiplayerRoomPage : UserControl, IDisposable
{
    private readonly MainWindow _main;
    private readonly TerracottaCore _core;
    private readonly TerracottaSessionMode _mode;
    private readonly EventHandler<TerracottaStateSnapshot> _onState;
    private readonly EventHandler<string> _onCode;
    private bool _closing;

    public MultiplayerRoomPage(MainWindow main, LauncherContext ctx, TerracottaCore core,
        TerracottaSessionMode mode)
    {
        _main = main;
        _core = core;
        _mode = mode;
        InitializeComponent();

        _onState = (_, _) => Dispatcher.InvokeAsync(Refresh);
        _onCode = (_, _) => Dispatcher.InvokeAsync(Refresh);
        _core.StateUpdated += _onState;
        _core.RoomCodeChanged += _onCode;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Detach();
    }

    private bool IsHost => _mode == TerracottaSessionMode.Create;

    private string ModeText()
        => IsHost ? Lang.T("String.Multi.CreateRoom") : Lang.T("String.Multi.JoinRoom");

    private void Refresh()
    {
        TitleText.Text = IsHost
            ? Lang.T("String.Multi.Room.TitleHost")
            : Lang.T("String.Multi.Room.TitleGuest");
        HintText.Text = IsHost
            ? Lang.T("String.Multi.Room.HintHost")
            : Lang.T("String.Multi.Room.HintGuest");
        ActionHint.Text = IsHost
            ? Lang.T("String.Multi.Room.ActionHost")
            : Lang.T("String.Multi.Room.ActionGuest");
        CloseButton.Content = IsHost
            ? Lang.T("String.Multi.Room.CloseRoom")
            : Lang.T("String.Multi.Room.LeaveRoom");

        StateText.Text = StateTextFor(_core.StateKind);
        CodeBorder.Visibility = IsHost ? Visibility.Visible : Visibility.Collapsed;
        RoomCodeText.Text = string.IsNullOrWhiteSpace(_core.RoomCode)
            ? Lang.T("String.Multi.Room.WaitingCode") : _core.RoomCode;

        UrlBorder.Visibility = !IsHost && !string.IsNullOrWhiteSpace(_core.GuestUrl)
            ? Visibility.Visible : Visibility.Collapsed;
        UrlText.Text = _core.GuestUrl ?? "";
        PeerList.ItemsSource = _core.Peers;
    }

    private static string StateTextFor(TerracottaStateKind kind) => kind switch
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

    /// <summary>侧边栏 / 关闭窗口导致离开时，也把房间结束掉。</summary>
    public bool ConfirmLeave()
    {
        if (_closing) return true;
        if (_core.HasActiveRoom &&
            !Notice.Ask(Lang.F("String.Multi.ConfirmLeave", ModeText()))) return false;
        _closing = true;
        _ = _core.ExitRoomAsync();
        return true;
    }

    private async void Close_OnClick(object? sender, RoutedEventArgs e) => await LeaveAsync();

    private async void Back_OnClick(object? sender, RoutedEventArgs e) => await LeaveAsync();

    private async Task LeaveAsync()
    {
        if (_closing) return;
        if (_core.HasActiveRoom &&
            !Notice.Ask(Lang.F("String.Multi.ConfirmLeave", ModeText()))) return;
        _closing = true;
        try { await _core.ExitRoomAsync().ConfigureAwait(true); }
        catch (Exception ex)
        { Log.Warn("Multi", $"关闭房间失败：{ex.GetType().Name}: {ex.Message}"); }
        _main.ShowMultiplayer();
    }

    private void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(RoomCodeText.Text);
            Notice.Info(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Copied"));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        { Log.Warn("Multi", $"复制邀请码失败：{ex.Message}"); }
    }

    private void Detach()
    {
        _core.StateUpdated -= _onState;
        _core.RoomCodeChanged -= _onCode;
    }

    public void Dispose() => Detach();
}
