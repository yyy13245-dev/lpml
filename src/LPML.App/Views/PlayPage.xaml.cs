using System.ComponentModel;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Auth;
using LPML.Core.Config;
using LPML.Core.Download;
using LPML.Core.Instances;
using LPML.Core.Java;
using LPML.Core.Launch;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using LPML.App.Windows;

namespace LPML.App.Views;

public partial class PlayPage : UserControl, IRefreshablePage
{
    public LauncherContext Context { get; }
    public MainWindow? MainWindow { get; set; }

    /// <summary>XAML 解析完成标志。</summary>
    private bool _ready;


    public PlayPage(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        _ready = true;
        Loaded += (_, _) => { Refresh(); ApplyLaunchButtonState(); SubscribeSession(); };
        Unloaded += (_, _) =>
        {
            UnsubscribeSession();
            Context.Instances.InstancesChanged -= OnInstancesChanged;
        };
        Context.Instances.InstancesChanged += OnInstancesChanged;
        SubscribeSession();
    }

    /// <summary>别处新建 / 删除实例后，首页下拉框立刻跟上，不用切走再切回来。</summary>
    private void OnInstancesChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnInstancesChanged(sender, e)); return; }
        if (_ready && IsLoaded) Refresh();
    }

    // 切换界面语言会重建页面，卸下时必须退订，否则旧页面会被事件一直挂着
    private bool _sessionSubscribed;

    private void SubscribeSession()
    {
        if (_sessionSubscribed || App.GameSession is null) return;
        App.GameSession.RunningChanged += OnGameRunningChanged;
        _sessionSubscribed = true;
    }

    private void UnsubscribeSession()
    {
        if (!_sessionSubscribed || App.GameSession is null) return;
        App.GameSession.RunningChanged -= OnGameRunningChanged;
        _sessionSubscribed = false;
    }

    private void OnGameRunningChanged(object? sender, bool running)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnGameRunningChanged(sender, running)); return; }
        ApplyLaunchButtonState();
    }

    /// <summary>允许多个实例同时运行，这个按钮始终用来启动，不再切成"关闭游戏"。</summary>
    private void ApplyLaunchButtonState()
    {
        if (LaunchButton is null) return;
        LaunchButton.Content = Lang.T("String.Play.Launch");
        LaunchButton.IsEnabled = true;
    }

    public void Refresh()
    {
        // ItemsSource 指向同一个 List 引用时 WPF 不会刷新，新装好的实例就一直看不到：
        // 先置 null 再给一份新列表，强制重建下拉项。
        InstanceCombo.ItemsSource = null;
        var instances = Context.InstancesInCurrentFolder();
        InstanceCombo.ItemsSource = instances;
        var selected = Context.Instances.Find(Context.Settings.SelectedInstanceId) ?? instances.FirstOrDefault();
        if (selected is not null && !Context.IsInCurrentFolder(selected)) selected = instances.FirstOrDefault();
        if (selected is not null) InstanceCombo.SelectedItem = selected;

        GameDirText.Text = Context.Paths.GameDirectory;
        MirrorText.Text = Context.Settings.Mirror.Default switch
        {
            MirrorKind.Official => Lang.T("String.Msg.Play.OfficialMirror"),
            MirrorKind.BMCLAPI or MirrorKind.MCBBS => "BMCLAPI",
            MirrorKind.FastMCMirror => Lang.T("String.Mirror.FastMCMirror"),
            _ => Lang.T("String.Msg.Common.Unknown")
        };
        UpdateInstanceInfo();
        _ = DetectJavaAsync();
    }

    private int ResolveMaxMemory()
    {
        if (!Context.Settings.Java.AutoMemory) return Context.Settings.Java.MaxMemoryMb;
        var totalMb = (long)Math.Round(GetTotalPhysicalMemory() / 1024.0 / 1024.0);
        var value = (int)(totalMb * Context.Settings.Java.AutoMemoryRatio);
        return Math.Clamp(value, 1024, 32768);
    }

    private static long GetTotalPhysicalMemory()
    {
        try
        {
            var info = new NativeMethods.MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref info)) return (long)info.TotalPhys;
        }
        catch { }
        return 8L * 1024 * 1024 * 1024;
    }

    private async Task DetectJavaAsync()
    {
        JavaText.Text = Lang.T("String.Msg.Play.Detecting");
        var result = await Task.Run(() =>
        {
            var pool = Context.Java.Detect();
            var recommended = pool.FirstOrDefault();
            return recommended is null ? Lang.T("String.Msg.Play.NoJava") : recommended.DisplayName;
        });
        JavaText.Text = result;
    }

    private void UpdateInstanceInfo()
    {
        if (InstanceCombo.SelectedItem is GameInstance instance)
        {
            InstanceNameText.Text = instance.Name;
            InstanceDetailText.Text = Lang.F("String.Msg.Play.InstanceSummary", instance.VersionId, instance.LoadersText, instance.PlayCount);
        }
        else
        {
            InstanceNameText.Text = Lang.T("String.Msg.Play.NoInstance");
            InstanceDetailText.Text = Lang.T("String.Msg.Play.CreateInstanceHint");
        }
    }

    private void InstanceCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || MuteButton is null) return;
        if (InstanceCombo.SelectedItem is GameInstance instance)
        {
            Context.Settings.SelectedInstanceId = instance.Id;
            Context.Save();
            UpdateMuteButtonState(instance);
        }
        UpdateInstanceInfo();
    }

    /// <summary>一键静音：把该实例 options.txt 里的音量写成 0。</summary>
    private void MuteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (InstanceCombo.SelectedItem is not GameInstance instance)
        {
            Notice.Info(Lang.T("String.Msg.Play.SelectInstanceFirst"));
            return;
        }

        try
        {
            var changed = MuteModeService.MuteNow(instance, Context.Instances);
            UpdateMuteButtonState(instance);
            StatusText.Text = changed > 0
                ? Lang.T("String.Play.Muted")
                : Lang.T("String.Play.AlreadyMuted");
        }
        catch (Exception ex)
        {
            Log.Error("Mute", Lang.T("String.Play.MuteFailed"), ex);
            Notice.Warn(Lang.T("String.Play.MuteFailed") + ex.Message);
        }
    }

    /// <summary>按当前是否已静音更新按钮文案。</summary>
    private void UpdateMuteButtonState(GameInstance instance)
    {
        if (MuteButton is null) return;
        var muted = MuteModeService.IsMuted(instance, Context.Instances);
        MuteButton.Content = muted ? Lang.T("String.Play.Muted") : Lang.T("String.Play.MuteAction");
        MuteButton.IsEnabled = !muted;
    }

    /// <summary>启动期间把实例选择、静音按钮、启动按钮一起置灰。</summary>
    private void SetLaunching(bool launching)
    {
        if (LaunchButton is not null) LaunchButton.IsEnabled = !launching;
        if (InstanceCombo is not null) InstanceCombo.IsEnabled = !launching;
        if (MuteButton is not null) MuteButton.IsEnabled = !launching;
        if (launching)
        {
            if (LaunchButton is not null) LaunchButton.Content = Lang.T("String.Msg.Game.Launching");
        }
        else ApplyLaunchButtonState();
    }

    private async void LaunchButton_OnClick(object sender, RoutedEventArgs e)
        => await LaunchAsync();

    /// <summary>启动当前实例（页面内按钮与全局悬浮按钮共用）。</summary>
    public async Task LaunchAsync()
    {
        if (InstanceCombo.SelectedItem is not GameInstance instance)
        {
            Notice.Info(Lang.T("String.Msg.Play.SelectInstanceFirst"));
            return;
        }

        var account = Context.Accounts.Current(Context.Settings.SelectedAccountId);
        if (account is null)
        {
            Notice.Info(Lang.T("String.Msg.Play.AddAccountFirst"));
            return;
        }

        // 允许多开：不再做全局占位拦截，每个实例各自登记成一个会话
        var session = App.GameSession;
        if (session is not null)
            Log.Info("Launch", Lang.F("String.Msg.Game.LaunchLog", instance.Name));

        // 启动期间锁住操作：改实例 / 改静音都会在写文件时打架
        SetLaunching(true);
        LaunchProgress.Visibility = Visibility.Visible;
        StatusText.Text = Lang.T("String.Msg.Play.CheckingFiles");

        try
        {
            var missing = Context.Launcher.FindMissingFiles(instance.VersionId);
            if (missing.Count > 0 && Context.Settings.Launch.AutoDownloadMissingFiles)
            {
                StatusText.Text = Lang.F("String.Msg.Play.MissingFiles", missing.Count);
                var items = await Context.Launcher.BuildRepairItemsAsync(instance.VersionId);
                if (items.Count > 0)
                {
                    var repairTask = Context.Downloads.BeginTask(instance.Name, "game");
                    Context.Downloads.Enqueue(items, repairTask);
                    var aggregate = Context.Downloads.BuildAggregate();
                    LaunchProgress.Value = 0;
                    var progressHandler = new EventHandler<DownloadAggregate>((_, agg) =>
                        Dispatcher.Invoke(() =>
                        {
                            LaunchProgress.Value = agg.Progress;
                            StatusText.Text = Lang.F("String.Msg.Play.RepairProgress", agg.Completed, agg.Total, LPML.Core.Utils.FileHelper.HumanSpeed(agg.BytesPerSecond));
                        }));
                    Context.Downloads.ProgressChanged += progressHandler;
                    await Context.Downloads.RunAsync();
                    Context.Downloads.ProgressChanged -= progressHandler;
                }
            }
            else if (missing.Count > 0 && Context.Settings.Launch.CheckGameCompleteBeforeLaunch)
            {
                if (!Notice.Ask(Lang.F("String.Msg.Play.MissingWarn", missing.Count)))
                {
                    LaunchButton.IsEnabled = true;
                    LaunchProgress.Visibility = Visibility.Collapsed;
                    StatusText.Text = Lang.T("String.Msg.Common.Cancelled");
                    return;
                }
            }

            StatusText.Text = Lang.T("String.Msg.Play.Launching");
            var options = BuildOptions(instance, account);
            var launch = await Task.Run(() => Context.Launcher.LaunchAsync(instance.VersionId, account, options).GetAwaiter().GetResult());

            if (!launch.Success)
            {
                StatusText.Text = Lang.T("String.Msg.Play.LaunchFailedPrefix") + launch.ErrorMessage;
                Notice.Error(Lang.T("String.Msg.Play.LaunchFailedTitle"), launch.ErrorMessage);
                return;
            }

            StatusText.Text = Lang.F("String.Msg.Play.Running", launch.ProcessId);
            var process = Context.Launcher.RunningProcess;
            if (process is not null)
            {
                var sessionId = session?.Register(process, instance);
                // 日志带实例名：几个游戏一起跑时不会把输出混成一条流
                process.OutputReceived += (_, line) => Log.Info("Game", $"[{instance.Name}] {line}");
                process.Exited += (_, code) => Dispatcher.Invoke(() =>
                {
                    var killed = App.GameSession?.WasKilledByUser(sessionId) == true;
                    Log.Info("Launch", Lang.F("String.Msg.Game.ExitLogMulti",
                        instance.Name, process.ProcessId ?? 0, code));
                    StatusText.Text = killed
                        ? Lang.T("String.Msg.Game.ClosedByUser")
                        : code == 0 ? Lang.T("String.Msg.Play.Exited") : Lang.F("String.Msg.Play.Crashed", code);
                    LaunchButton.IsEnabled = true;
                    LaunchProgress.Visibility = Visibility.Collapsed;
                    Context.Instances.MarkPlayed(instance, 0);
                    ApplyLaunchButtonState();
                });
            }

            switch ((AfterLaunchAction)(instance.AfterLaunchOverride ?? (int)Context.Settings.Launch.AfterLaunch))
            {
                case AfterLaunchAction.Hide:
                    MainWindow?.Hide();
                    break;
                case AfterLaunchAction.Exit:
                    MainWindow?.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Play", Lang.T("String.Msg.Play.LaunchErrorTitle"), ex);
            StatusText.Text = Lang.T("String.Msg.Play.LaunchFailedPrefix") + ex.Message;
        }
        finally
        {
            // 启动失败/取消时释放占位；已经登记成功的运行不受影响
            session?.CancelLaunch();
            SetLaunching(false);
            if (InstanceCombo.SelectedItem is GameInstance finished) UpdateMuteButtonState(finished);
        }
 }

    private LaunchOptions BuildOptions(GameInstance instance, Account account)
    {
        var java = Context.Settings.Java;
        var launch = Context.Settings.Launch;
        var max = instance.MaxMemoryOverrideMb ?? Context.Settings.Java.MaxMemoryMb;
        var min = instance.MinMemoryOverrideMb ?? java.MinMemoryMb;

        var options = new LaunchOptions
        {
            VersionId = instance.VersionId,
            WorkingDirectory = Context.WorkingDirectoryFor(instance),
            JavaPath = instance.JavaPathOverride ?? (string.IsNullOrWhiteSpace(java.JavaPath) ? null : java.JavaPath),
            MinMemoryMb = Math.Min(min, max),
            MaxMemoryMb = max,
            ExtraJvmArguments = instance.JvmArgumentOverride ?? java.JvmArguments,
            ExtraGameArguments = instance.GameArgumentOverride ?? launch.GameArguments,
            WindowWidth = instance.WindowWidthOverride ?? launch.WindowWidth,
            WindowHeight = instance.WindowHeightOverride ?? launch.WindowHeight,
            Fullscreen = instance.FullscreenOverride ?? launch.Fullscreen,
            ServerAddress = string.IsNullOrWhiteSpace(instance.ServerOverride) ? NullIfEmpty(launch.QuickJoinServer) : instance.ServerOverride,
            WorldName = NullIfEmpty(launch.QuickJoinWorld),
            EnableCheats = launch.EnableCheats,
            CustomWindowTitle = (instance.UseCustomWindowTitleOverride ?? launch.UseCustomWindowTitle)
                ? (instance.WindowTitleOverride ?? launch.CustomWindowTitle)
                : null,
            ProcessPriority = instance.ProcessPriorityOverride ?? java.ProcessPriority,
            UseIsolatedNativesDirectory = launch.UseIsolatedNativesDirectory,
            EnvironmentVariables = ParseEnvironment(instance.EnvironmentVariablesOverride ?? java.EnvironmentVariables)
        };

        if (account.RequiresAuthlibInjector && !string.IsNullOrEmpty(account.AuthServer))
        {
            options.AuthServerUrl = account.AuthServer;
            options.AuthlibInjectorPath = Context.Settings.Account.AuthlibInjectorPath;
            options.AuthPrefetched = YggdrasilService.BuildPrefetched(account);
        }

        return options;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Dictionary<string, string> ParseEnvironment(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0) continue;
            result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return result;
    }

    
    

    

    

    private static void OpenFolder(string path)
    {
        try
        {
            LPML.Core.Utils.FileHelper.EnsureDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("UI", Lang.T("String.Msg.Common.OpenDirFailed") + ex.Message);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    }
}
