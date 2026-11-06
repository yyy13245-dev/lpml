using LPML.App.Controls;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Instances;
using LPML.Core.Launch;
using LPML.Core.Multiplayer;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Net.NetworkInformation;
using LPML.App.Windows;

namespace LPML.App.Views;

public sealed partial class MultiplayerCreatePage : UserControl, IDisposable
{
    private readonly LauncherContext _ctx;
    private readonly MainWindow _main;
    private readonly TerracottaCore _core;
    private GameInstance? _lastInstance;
    private bool _detected;
    private bool _leaving;


    public MultiplayerCreatePage(MainWindow main, LauncherContext ctx, TerracottaCore core)
    {
        _ctx = ctx;
        _core = core;
        _main = main;
        InitializeComponent();
        Picker.Initialize(_ctx, OnInstanceLaunched);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 若上次启动过实例，回填它探测到的端口
        var last = !string.IsNullOrWhiteSpace(_ctx.Settings.SelectedInstanceId) ? _ctx.Instances.Find(_ctx.Settings.SelectedInstanceId) : null;
        if (last is not null) Picker.SetSelected(last);
        var (ip, port) = ReadLastDetected();
        if (!string.IsNullOrWhiteSpace(ip) && port > 0 && string.IsNullOrWhiteSpace(AddressBox.Text))
            AddressBox.Text = $"{ip}:{port}";
        else if (string.IsNullOrWhiteSpace(AddressBox.Text) && !string.IsNullOrWhiteSpace(ip))
            AddressBox.Text = ip;
    }

    /// <summary>实例已启动：挂日志监听，自动抓取“本地开放局域网端口”的日志行。</summary>
    private void OnInstanceLaunched(string instanceName)
    {
        _detected = false;
        LaunchStatusBorder.Visibility = Visibility.Visible;
        LaunchStatusText.Text = Lang.F("String.Multi.LaunchSuccess", instanceName);
        LaunchStatusDetail.Text = Lang.T("String.Multi.DetectingPort");

        var monitor = new GameLogMonitor { DetectPort = true, DetectCrash = false, MaxLines = 2000 };
        monitor.PortOpened += (_, args) =>
        {
            _detected = true;
            var address = $"{args.Port}"; // 先只有端口，等下面补齐本机局域网 IP
            // 在 UI 线程补齐：取本机私有 IP
            var ip = LocalIp();
            AddressBox.Text = string.IsNullOrWhiteSpace(ip) ? args.Port.ToString() : $"{ip}:{args.Port}";
            LaunchStatusDetail.Text = Lang.F("String.Multi.PortDetected", AddressBox.Text);
            LaunchStatusDetail.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Brush.Accent"];
        };
        // 订阅当前游戏进程的输出
        AttachMonitor(monitor);
    }

    private void AttachMonitor(GameLogMonitor monitor)
    {
        var process = _ctx.Launcher.RunningProcess;
        if (process is null) return;
        process.OutputReceived += (_, line) => monitor.Feed(line);
        process.Exited += (_, _) => monitor.Reset();
    }

    private static string? LocalIp()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var bytes = ip.Address.GetAddressBytes();
                    if (bytes[0] == 127) continue;
                    if (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168))
                        return ip.Address.ToString();
                }
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return null;
    }

    private (string? Ip, int Port) ReadLastDetected()
    {
        var raw = _ctx.Settings.Terracotta.LastRoomCode; // 复用：不存地址，这里只作占位；实际走日志监听
        return (null, 0);
    }

    private void AddressBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (ResultBorder is null) return;
        ResultBorder.Visibility = Visibility.Collapsed;
        if (_detected) _detected = false;
    }

    private void ManualCheck_OnChecked(object? sender, RoutedEventArgs e)
        => PortBox.IsEnabled = ManualCheck.IsChecked ?? false;

    private void FillDetected_OnClick(object? sender, RoutedEventArgs e)
    {
        var (ip, port) = ReadLastDetected();
        if (!string.IsNullOrWhiteSpace(ip)) AddressBox.Text = ip;
        if (port > 0) PortBox.Text = port.ToString();
        if (string.IsNullOrWhiteSpace(AddressBox.Text) && string.IsNullOrWhiteSpace(PortBox.Text))
            Notice.Info(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.NoDetectionYet"));
    }

    private void Generate_OnClick(object? sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            Notice.Warn(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.AddressEmpty"));
            return;
        }
        if (!IsAddressValid(address))
        {
            Notice.Warn(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Err.AddressInvalid"));
            return;
        }

        var code = TerracottaCore.GenerateRoomCode();
        _ctx.Settings.Terracotta.LastRoomCode = code;
        _ctx.Settings.Terracotta.LastMode = TerracottaSessionMode.Create;
        _ctx.Save();

        RoomCodeText.Text = code;
        ResultAddressText.Text = Lang.F("String.Multi.RoomAddress", address);
        ResultBorder.Visibility = Visibility.Visible;
    }

    private static bool IsAddressValid(string address)
    {
        var hostPort = address.Split(':');
        if (hostPort.Length is < 1 or > 2) return false;
        if (!System.Uri.TryCreate("http://" + address, System.UriKind.Absolute, out var uri)) return false;
        if (uri.Port < 0 || uri.Port > 65535) return false;
        return true;
    }

    private void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(RoomCodeText.Text);
            Notice.Info(Lang.T("String.Multi.Title"), Lang.T("String.Multi.Copied"));
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void Back_OnClick(object? sender, RoutedEventArgs e)
        => GoBack();

    private void GoBack()
    {
        if (_core.IsRunning && !_leaving)
        {
            if (!Notice.Ask(Lang.F("String.Multi.ConfirmLeave", Lang.T("String.Multi.CreateRoom")))) return;
            _core.Stop();
        }
        PendingSessionStore.RequestedAddress = null;
        if (Application.Current.MainWindow is MainWindow window) window.Navigate("multiplayer");
        _leaving = false;
    }

    public void Dispose() => _core.Dispose();
}
