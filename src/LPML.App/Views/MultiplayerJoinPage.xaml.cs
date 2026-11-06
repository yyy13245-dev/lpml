using LPML.App.Controls;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Multiplayer;
using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using LPML.App.Windows;

namespace LPML.App.Views;

public sealed partial class MultiplayerJoinPage : UserControl, IDisposable
{
    /// <summary>XAML 解析完成标志：初始化期间控件未必全部创建，事件需跳过。</summary>
    private bool _ready;
    private readonly MainWindow _main;
    private readonly LauncherContext _ctx;
    private readonly TerracottaCore _core;
    private bool _leaving;
    private string? _resolvedAddress;

    public MultiplayerJoinPage(MainWindow main, LauncherContext ctx, TerracottaCore core)
    {
        _ctx = ctx;
        _core = core;
        _main = main;
        InitializeComponent();
        _ready = true;
        Picker.Refresh();
        Picker.SetAutoJoin(true, null); // 地址在解析邀请码后回填
        Picker.Initialize(_ctx, OnInstanceLaunched);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 回填上一次的邀请码，便于“退出到上一级再回来”时不丢输入
        if (!string.IsNullOrWhiteSpace(_ctx.Settings.Terracotta.LastRoomCode) && string.IsNullOrWhiteSpace(CodeBox.Text))
            CodeBox.Text = _ctx.Settings.Terracotta.LastRoomCode;
        UpdateNotice();
    }

    private void CodeBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (JoinStatusText is null || Picker is null) return;
        _resolvedAddress = null;
        JoinStatusText.Text = Lang.T("String.Multi.CodeNeedResolve");
        Picker.SetAutoJoin(false, null);
        UpdateNotice();
    }

    private async void Join_OnClick(object? sender, RoutedEventArgs e)
    {
        var code = NormalizeCode(CodeBox.Text);
        if (code.Length != 8)
        {
            Notice.Warn(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.CodeInvalid"));
            return;
        }

        JoinButton.IsEnabled = false;
        JoinStatusText.Text = Lang.T("String.Multi.Resolving");
        _ctx.Settings.Terracotta.LastRoomCode = code;
        _ctx.Settings.Terracotta.LastMode = TerracottaSessionMode.Join;
        _ctx.Save();

        try
        {
            var address = await ResolveAddressAsync(code).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(address))
            {
                JoinStatusText.Text = Lang.T("String.Multi.Err.ResolveFailed");
                return;
            }
            _resolvedAddress = address;
            JoinStatusText.Text = Lang.F("String.Multi.Resolved", address);
            Picker.SetAutoJoin(true, address);
            UpdateNotice();
        }
        catch (OperationCanceledException)
        {
            JoinStatusText.Text = Lang.T("String.Multi.Err.Canceled");
        }
        catch (Exception ex)
        {
            JoinStatusText.Text = Lang.F("String.Multi.Err.ResolveDetail", ex.Message);
        }
        finally
        {
            JoinButton.IsEnabled = true;
        }
    }

    /// <summary>邀请码 → 房主共享地址：先查本地保存的映射，再查启动器自行提供的发现服务。</summary>
    private async System.Threading.Tasks.Task<string?> ResolveAddressAsync(string code)
    {
        var map = _ctx.Settings.Terracotta.KnownRooms;
        if (map.TryGetValue(code, out var local) && !string.IsNullOrWhiteSpace(local)) return local;

        var url = _ctx.Settings.Terracotta.DiscoveryUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return null;

        var built = url.Contains("{code}", StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(url, @"\{code\}", code, RegexOptions.IgnoreCase)
            : url.TrimEnd('/') + "/" + Uri.EscapeDataString(code);
        var text = await _ctx.Http.GetStringAsync(built, CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim(' ', '\n', '\r', '"');
        if (!IsAddressValid(trimmed)) return null;
        _ctx.Settings.Terracotta.KnownRooms[code] = trimmed;
        _ctx.Save();
        return trimmed;
    }

    private void OnInstanceLaunched(string instanceName)
        => JoinStatusText.Text = Lang.F("String.Multi.JoinLaunched", instanceName);

    private void UpdateNotice()
    {
        JoinNoticeText.Text = string.IsNullOrWhiteSpace(_resolvedAddress)
            ? Lang.T("String.Multi.OneClickNoticePending")
            : Lang.F("String.Multi.OneClickNoticeReady", _resolvedAddress);
    }

    private static string NormalizeCode(string? text)
        => (text ?? "").Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");

    private static bool IsAddressValid(string address)
    {
        if (!System.Uri.TryCreate("http://" + address, System.UriKind.Absolute, out var uri)) return false;
        return uri.Port >= 0 && uri.Port <= 65535;
    }

    private void GoBack()
    {
        if (_core.IsRunning && !_leaving)
        {
            if (!Notice.Ask(Lang.F("String.Multi.ConfirmLeave", Lang.T("String.Multi.JoinRoom")))) return;
            _core.Stop();
        }
        PendingSessionStore.RequestedAddress = null;
        if (Application.Current.MainWindow is MainWindow window) window.Navigate("multiplayer");
        _leaving = false;
    }

    public void Dispose() => _core.Dispose();
}
