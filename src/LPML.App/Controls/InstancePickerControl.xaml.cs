using LPML.App.Services;
using LPML.Core;
using LPML.Core.Instances;
using LPML.Core.Launch;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace LPML.App.Controls;

public sealed partial class InstancePickerControl : UserControl
{
    private LauncherContext? _ctx;
    private bool _autoJoin;
    private string? _joinAddress;
    private Action<string>? _onLaunched;
    private readonly List<GameInstance> _list = new();

    /// <summary>
    /// <paramref name="autoJoin"/>：启动后是否自动直连到 <paramref name="joinAddress"/>（房主共享的 IP:端口）。
    /// <paramref name="onLaunched"/>：游戏进程启动成功后回调，参数为实例名（用于房客页显示状态）。
    /// </summary>
    /// <summary>XAML 要求无参构造；真正的数据由 Initialize 注入。</summary>
    public InstancePickerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>注入上下文与启动回调（页面构造后调用）。</summary>
    public void Initialize(LauncherContext ctx, Action<string>? onLaunched)
    {
        _ctx = ctx;
        _onLaunched = onLaunched;
        if (IsLoaded) Refresh();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_ctx is not null) Refresh();
    }

    public void SetSelected(GameInstance? instance)
    {
        if (instance is not null) InstanceCombo.SelectedItem = instance;
    }

    public void SetAutoJoin(bool autoJoin, string? address)
    {
        _autoJoin = autoJoin;
        _joinAddress = address;
        UpdateHint(InstanceCombo.SelectedItem as GameInstance);
    }

    public void SetOnLaunched(Action<string>? onLaunched) => _onLaunched = onLaunched;

    public void Refresh()
    {
        if (_ctx is null) return;
        _list.Clear();
        InstanceCombo.ItemsSource = null;
        _list.AddRange(_ctx.InstancesInCurrentFolder());
        InstanceCombo.ItemsSource = _list;

        GameInstance? selected = null;
        if (!string.IsNullOrWhiteSpace(_ctx.Settings.SelectedInstanceId))
            selected = _ctx.Instances.Find(_ctx.Settings.SelectedInstanceId);
        selected ??= _list.Count > 0 ? _list[0] : null;
        InstanceCombo.SelectedItem = selected;
        UpdateHint(selected);
    }

    private void InstanceCombo_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => UpdateHint(InstanceCombo.SelectedItem as GameInstance);

    private void UpdateHint(GameInstance? instance)
    {
        if (instance is null)
        {
            HintText.Text = Lang.T("String.Multi.NoInstance");
            LaunchButton.IsEnabled = false;
            return;
        }
        LaunchButton.IsEnabled = true;
        var text = Lang.F("String.Multi.InstanceHint", instance.VersionId, instance.LoadersText);
        if (_autoJoin && !string.IsNullOrWhiteSpace(_joinAddress))
            text += "  " + Lang.F("String.Multi.WillAutoJoin", _joinAddress);
        HintText.Text = text;
    }

    private async void LaunchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var instance = InstanceCombo.SelectedItem as GameInstance;
        if (instance is null) return;

        // 记下“本次启动想连的房间”，启动参数里写入，并交给日志监听自动抓取端口
        _ctx.Settings.SelectedInstanceId = instance.Id;
        PendingSessionStore.RequestedAddress = _autoJoin ? _joinAddress : null;
        _ctx.Save();

        LaunchButton.IsEnabled = false;
        LaunchButton.Content = Lang.T("String.Multi.Launching");
        try
        {
            var service = new GameLaunchService(_ctx);
            service.StatusChanged += (_, text) =>
        Application.Current.Dispatcher.Invoke(() => HintText.Text = text);
            var result = await service.LaunchAsync(instance, debugMode: true, System.Threading.CancellationToken.None).ConfigureAwait(true);
            if (result.Success && result.ProcessId > 0)
            {
                _onLaunched?.Invoke(instance.Name);
                return;
            }
            if (!result.Success)
                HintText.Text = Lang.T("String.Multi.Err.LaunchFailed");
        }
        catch (Exception ex)
        {
            HintText.Text = Lang.F("String.Multi.Err.LaunchFailedDetail", ex.Message);
        }
        finally
        {
            LaunchButton.Content = Lang.T("String.Multi.LaunchGame");
            LaunchButton.IsEnabled = true;
        }
    }

    /// <summary>每次启动前清掉上一次的“自动连接”标记，避免重启后误连。</summary>
    internal static void ClearPending(LauncherContext ctx)
    {
        PendingSessionStore.RequestedAddress = null;
        ctx.Save();
    }
}

/// <summary>跨页面传递“本次启动想连的地址”（房间页 → 启动参数 / 日志监听）。</summary>
internal static class PendingSessionStore
{
    public static string? RequestedAddress { get; set; }
}
