using LPML.App.Services;
using LPML.Core;
using LPML.Core.Instances;
using LPML.Core.Launch;
using LPML.Core.Mods;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using LPML.Core.Download;
using System.Threading;
using LPML.App.Windows;

namespace LPML.App.Views;

/// <summary>
/// 侧边栏风格的实例设置：左侧分区导航（概览 / 游戏设置 / 模组管理 / 版本隔离 / 高级参数 / 诊断与文件），
/// 侧边栏底部提供打开目录与调试启动（日志窗口）。
/// </summary>
public partial class InstanceSettingsPage : UserControl, IRefreshablePage, IParameterizedPage
{
    /// <summary>XAML 解析完成标志：初始化期间控件未必全部创建，事件需跳过。</summary>
    private bool _ready;
    private LauncherContext Context => App.Context;
    private GameInstance? _instance;
    private bool _loaded;
    private readonly List<ModInfo> _mods = new();

    public InstanceSettingsPage()
    {
        InitializeComponent();
        _ready = true;
        ShowSelectedPane();          // 初始化期的选中事件被 _ready 跳过，这里补一次
        Loaded += (_, _) => Refresh();
    }

    public void ApplyArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return;
        foreach (var pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            if (parts[0] == "id")
            {
                var found = Context.Instances.Find(parts[1]);
                if (found is not null) SetInstance(found);
            }
            else if (parts[0] == "tab")
            {
                SelectSection(parts[1]);
            }
        }
    }

    public void SetInstance(GameInstance instance)
    {
        _instance = instance;
        Context.Settings.SelectedInstanceId = instance.Id;
        if (IsLoaded) Load();
    }

    public void Refresh()
    {
        // 两个入口（实例页双击 / 侧栏）都指向当前文件夹里的同一个实例
        if (_instance is not null && !Context.IsInCurrentFolder(_instance)) _instance = null;
        // 文件夹决定实例：被选中的实例若不属于当前文件夹，就回退到本文件夹里的第一个，
        // 不再把别的文件夹里的实例显示成「这个文件夹里也有」。
        var selected = Context.Instances.Find(Context.Settings.SelectedInstanceId);
        if (selected is null || !Context.IsInCurrentFolder(selected))
            selected = Context.InstancesInCurrentFolder().FirstOrDefault();
        ApplyNoInstanceState(selected is null);
        if (selected is null) return;

        _instance = selected;
        Context.Settings.SelectedInstanceId = selected.Id;
        Load();
    }

    // ── 载入 / 保存 ─────────────────────────────────────────

    private void Load()
    {
        _loaded = false;
        var instance = _instance;

        if (instance is null)
        {
            SidebarInstanceName.Text = L.T("String.Msg.InstanceSettings.NoInstance");
            foreach (var pane in AllPanes()) pane.Visibility = Visibility.Collapsed;
            _loaded = true;
            return;
        }

        SidebarInstanceName.Text = instance.Name;

        // 概览
        NameBox.Text = instance.Name;
        VersionBox.Text = instance.VersionId;
        LoaderBox.Text = instance.LoadersText;
        NotesBox.Text = instance.Notes ?? "";
        PlayCountText.Text = instance.PlayCount.ToString();
        PlayTimeText.Text = instance.PlayTimeText;
        LastPlayedText.Text = instance.LastPlayed.ToString("yyyy-MM-dd HH:mm");

        // 游戏设置
        OverrideJavaCheck.IsChecked = !string.IsNullOrWhiteSpace(instance.JavaPathOverride);
        JavaBox.Text = instance.JavaPathOverride ?? Context.Settings.Java.JavaPath ?? "";
        AutoMemoryCheck.IsChecked = instance.AutoMemoryOverride ?? Context.Settings.Java.AutoMemory;
        MinMemoryBox.Text = (instance.MinMemoryOverrideMb ?? Context.Settings.Java.MinMemoryMb).ToString();
        MaxMemoryBox.Text = (instance.MaxMemoryOverrideMb ?? Context.Settings.Java.MaxMemoryMb).ToString();
        WidthBox.Text = (instance.WindowWidthOverride ?? Context.Settings.Launch.WindowWidth).ToString();
        HeightBox.Text = (instance.WindowHeightOverride ?? Context.Settings.Launch.WindowHeight).ToString();
        FullscreenCheck.IsChecked = instance.FullscreenOverride ?? Context.Settings.Launch.Fullscreen;
        UseCustomTitleCheck.IsChecked = instance.UseCustomWindowTitleOverride ?? Context.Settings.Launch.UseCustomWindowTitle;
        TitleBox.Text = instance.WindowTitleOverride ?? Context.Settings.Launch.CustomWindowTitle;
        TitleBox.IsEnabled = UseCustomTitleCheck.IsChecked == true;
        ServerBox.Text = instance.ServerOverride ?? Context.Settings.Launch.QuickJoinServer;
        PriorityCombo.SelectedIndex = PriorityIndex(instance.ProcessPriorityOverride ?? Context.Settings.Java.ProcessPriority);
        // 0 = 跟随全局；1..3 对应 AfterLaunchAction 的 保持 / 隐藏 / 退出
        AfterLaunchCombo.SelectedIndex = instance.AfterLaunchOverride.HasValue
            ? Math.Clamp(instance.AfterLaunchOverride.Value + 1, 0, 3)
            : 0;

        // 版本隔离
        IsolationCheck.IsChecked = instance.Isolated;
        IsolationModeCombo.SelectedIndex = instance.IsolationModeOverride switch
        {
            LPML.Core.Instances.IsolationMode.UserData => 1,
            LPML.Core.Instances.IsolationMode.Full => 2,
            _ => 0   // 跟随全局
        };
        ShareModsCheck.IsChecked = instance.ShareMods;
        ShareSavesCheck.IsChecked = instance.ShareSaves;
        ShareConfigCheck.IsChecked = instance.ShareConfig;
        ShareResourcePacksCheck.IsChecked = instance.ShareResourcePacks;
        ShareShaderPacksCheck.IsChecked = instance.ShareShaderPacks;
        WorkingDirText.Text = Context.WorkingDirectoryFor(instance);

        // 高级
        JvmArgsBox.Text = instance.JvmArgumentOverride ?? Context.Settings.Java.JvmArguments;
        GameArgsBox.Text = instance.GameArgumentOverride ?? Context.Settings.Launch.GameArguments;
        EnvBox.Text = instance.EnvironmentVariablesOverride ?? Context.Settings.Java.EnvironmentVariables;
        PreLaunchBox.Text = instance.PreLaunchCommand ?? "";
        IsolatedNativesCheck.IsChecked = Context.Settings.Launch.UseIsolatedNativesDirectory;

        // 诊断
        InstanceLogMonitorCheck.IsChecked = instance.MonitorLog;
        VerifyStatusText.Text = "";
        // 上次启动：显示实际用到的 Java（含 JDK/JRE 与版本），没有记录则只显示时间
        var launchSummary = !string.IsNullOrWhiteSpace(instance.LastLaunchSummary)
            ? instance.LastLaunchSummary!
              + (instance.LastLaunchAt.HasValue ? " · " + instance.LastLaunchAt!.Value.ToString("MM-dd HH:mm") : "")
            : L.T("String.Msg.InstanceSettings.NoJavaRecord");
        LastLaunchText.Text = L.F("String.Msg.InstanceSettings.LastLaunch",
            instance.LastPlayed.ToString("yyyy-MM-dd HH:mm")) + "\n" + launchSummary;

        UpdatePrivacyHint();
        LoadMods();
        ShowSelectedPane();
        _loaded = true;
    }

    /// <summary>没有实例时：顶部提示 + 下方所有设置整组置灰，避免改了个寂寞。</summary>
    private void ApplyNoInstanceState(bool noInstance)
    {
        if (NoInstanceNotice is null) return;
        NoInstanceNotice.Visibility = noInstance ? Visibility.Visible : Visibility.Collapsed;

        foreach (var pane in AllPanes())
            pane.IsEnabled = !noInstance;

        // 侧边章节也一并禁掉（概览仍可看，但内容不可改）
        if (SectionList is not null) SectionList.IsEnabled = !noInstance;
    }

    private void GoDownload_OnClick(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this) as MainWindow;
        window?.Navigate("download");
    }

    private async void Repair_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        CompleteFiles_OnClick(sender, e);
    }

    private void ShowCommand_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        try
        {
            var account = Context.Accounts.Current(Context.Settings.SelectedAccountId);
            var preview = Context.Launcher.BuildPreview(_instance.VersionId, account, new LaunchOptions());
            Notice.Info(L.T("String.Msg.Play.CommandPreview"), preview);
        }
        catch (Exception ex)
        {
            Log.Warn("Instance", "Show command failed: " + ex.Message);
        }
    }

    private void Save()
    {
        if (_instance is null) return;
        var instance = _instance;

        instance.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? instance.Name : NameBox.Text.Trim();
        instance.Notes = NotesBox.Text.Trim();

        // Java
        instance.JavaPathOverride = OverrideJavaCheck.IsChecked == true
            ? (string.IsNullOrWhiteSpace(JavaBox.Text) ? null : JavaBox.Text.Trim())
            : null;
        if (OverrideJavaCheck.IsChecked == true) Context.Settings.Java.JavaPath = JavaBox.Text.Trim();

        // 内存
        instance.AutoMemoryOverride = AutoMemoryCheck.IsChecked;
        instance.MinMemoryOverrideMb = int.TryParse(MinMemoryBox.Text.Trim(), out var min) ? min : null;
        instance.MaxMemoryOverrideMb = int.TryParse(MaxMemoryBox.Text.Trim(), out var max) ? max : null;

        // 窗口
        instance.WindowWidthOverride = int.TryParse(WidthBox.Text.Trim(), out var width) ? width : null;
        instance.WindowHeightOverride = int.TryParse(HeightBox.Text.Trim(), out var height) ? height : null;
        instance.FullscreenOverride = FullscreenCheck.IsChecked;

        // 直连
        instance.ServerOverride = string.IsNullOrWhiteSpace(ServerBox.Text) ? null : ServerBox.Text.Trim();

        // 窗口标题（实例级）
        instance.UseCustomWindowTitleOverride = UseCustomTitleCheck.IsChecked;
        instance.WindowTitleOverride = UseCustomTitleCheck.IsChecked == true
            ? (string.IsNullOrWhiteSpace(TitleBox.Text) ? null : TitleBox.Text.Trim())
            : null;

        // 启动后行为：0 = 跟随全局
        instance.AfterLaunchOverride = AfterLaunchCombo.SelectedIndex <= 0
            ? null
            : AfterLaunchCombo.SelectedIndex - 1;

        // 优先级
        instance.ProcessPriorityOverride = PriorityName(PriorityCombo.SelectedIndex);

        // 隔离
        instance.Isolated = IsolationCheck.IsChecked == true;
        instance.IsolationModeOverride =
            (IsolationModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "UserData" => LPML.Core.Instances.IsolationMode.UserData,
                "Full" => LPML.Core.Instances.IsolationMode.Full,
                _ => null   // 跟随全局
            };
        instance.ShareMods = ShareModsCheck.IsChecked == true;
        instance.ShareSaves = ShareSavesCheck.IsChecked == true;
        instance.ShareConfig = ShareConfigCheck.IsChecked == true;
        instance.ShareResourcePacks = ShareResourcePacksCheck.IsChecked == true;
        instance.ShareShaderPacks = ShareShaderPacksCheck.IsChecked == true;

        // 高级
        instance.JvmArgumentOverride = string.IsNullOrWhiteSpace(JvmArgsBox.Text) ? null : JvmArgsBox.Text.Trim();
        instance.GameArgumentOverride = string.IsNullOrWhiteSpace(GameArgsBox.Text) ? null : GameArgsBox.Text.Trim();
        instance.EnvironmentVariablesOverride = string.IsNullOrWhiteSpace(EnvBox.Text) ? null : EnvBox.Text.Trim();
        instance.PreLaunchCommand = string.IsNullOrWhiteSpace(PreLaunchBox.Text) ? null : PreLaunchBox.Text.Trim();
        Context.Settings.Launch.UseIsolatedNativesDirectory = IsolatedNativesCheck.IsChecked == true;

        // 诊断
        instance.MonitorLog = InstanceLogMonitorCheck.IsChecked;

        Context.Instances.Save();
        Context.Save();
        WorkingDirText.Text = Context.WorkingDirectoryFor(instance);
        SidebarInstanceName.Text = instance.Name;
    }

    private void UseCustomTitle_OnChanged(object sender, RoutedEventArgs e)
    {
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (TitleBox is null) return;
        TitleBox.IsEnabled = UseCustomTitleCheck.IsChecked == true;
        Setting_OnChanged(sender, e);
    }

    /// <summary>优先级下拉索引 ↔ 名称（0..4 对应 低/低于正常/正常/高于正常/高）。</summary>
    private static string PriorityName(int index) => index switch
    {
        0 => "Idle",
        1 => "BelowNormal",
        3 => "AboveNormal",
        4 => "High",
        _ => "Normal"
    };

    /// <summary>把本实例的版本文件按它自己的隔离模式搬移一次。</summary>
    private async void MigrateIsolation_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            var mode = Context.IsolationModeFor(_instance);
            var shared = Context.Paths.GameDirectory;
            var moved = await Task.Run(() =>
                Context.Instances.MigrateIsolation(_instance, shared, mode)).ConfigureAwait(true);
            Context.Instances.Save();
            Notice.Info(Lang.F("String.Msg.Settings.MigrateDone", moved));
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void Setting_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        Save();
    }

    private void UpdatePrivacyHint()
    {
        var enabled = Context.Settings.Privacy.EnableLogMonitoring;
        PrivacyHint.Text = enabled
            ? L.T("String.Msg.InstanceSettings.MonitorOn")
            : L.T("String.Msg.InstanceSettings.MonitorOff");
        DebugLaunchButton.Style = enabled
            ? (Style)FindResource("Button.Primary")
            : (Style)FindResource("Button.Base");
    }

    // ── 分区切换 ────────────────────────────────────────────

    private void SectionList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (!IsLoaded) return;
        ShowSelectedPane();
    }

    private void ShowSelectedPane()
    {
        var tag = (SectionList.SelectedItem as ListBoxItem)?.Tag as string ?? "overview";
        foreach (var pane in AllPanes()) pane.Visibility = Visibility.Collapsed;

        var target = tag switch
        {
            "game" => PaneGame,
            "quick" => PaneQuick,
            "mods" => PaneMods,
            "isolation" => PaneIsolation,
            "advanced" => PaneAdvanced,
            "diagnostics" => PaneDiagnostics,
            _ => PaneOverview
        };
        target.Visibility = Visibility.Visible;

        if (tag == "mods") LoadMods();
    }

    private void SelectSection(string tag)
    {
        foreach (var item in SectionList.Items.OfType<ListBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                SectionList.SelectedItem = item;
                return;
            }
        }
    }

    private Border[] AllPanes() =>
    [
        PaneOverview, PaneGame, PaneQuick, PaneMods, PaneIsolation, PaneAdvanced, PaneDiagnostics
    ];

    private static int PriorityIndex(string priority) => priority switch
    {
        "Idle" => 0,
        "BelowNormal" => 1,
        "AboveNormal" => 3,
        "High" => 4,
        _ => 2
    };

    // ── 模组管理 ────────────────────────────────────────────

    private void LoadMods()
    {
        if (_instance is null) return;
        var directory = Context.ModsDirectoryFor(_instance);
        _mods.Clear();
        _mods.AddRange(ModManager.Scan(directory));
        ModList.ItemsSource = null;
        ModList.ItemsSource = _mods;
        ModSummaryText.Text = L.F("String.Msg.InstanceSettings.ModSummary",
            _mods.Count, _mods.Count(m => m.Enabled), _mods.Count(m => !m.Enabled));
    }

    private void RefreshMods_OnClick(object sender, RoutedEventArgs e) => LoadMods();

    private void ModList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ToggleMod_OnChecked(object sender, RoutedEventArgs e)
        => SetModEnabled(sender, true);

    private void ToggleMod_OnUnchecked(object sender, RoutedEventArgs e)
        => SetModEnabled(sender, false);

    private void SetModEnabled(object sender, bool enabled)
    {
        if (sender is not CheckBox { DataContext: ModInfo mod }) return;
        ModManager.SetEnabled(mod, enabled);
        LoadMods();
    }

    private void AddMods_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        var dialog = new OpenFileDialog
        {
            Filter = Lang.T("String.Msg.Mod.Filter"),
            Title = L.T("String.Msg.InstanceSettings.AddModsTitle"),
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;

        var directory = Context.ModsDirectoryFor(_instance);
        Directory.CreateDirectory(directory);
        foreach (var file in dialog.FileNames)
        {
            var target = Path.Combine(directory, Path.GetFileName(file));
            try { File.Copy(file, target, true); }
            catch (IOException ex) { Log.Warn("Instance", "Failed to copy mod: " + ex.Message); }
        }
        LoadMods();
    }

    private void DeleteMod_OnClick(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not ModInfo mod) return;
        if (!Notice.Ask(L.F("String.Msg.InstanceSettings.ConfirmDeleteMod", mod.DisplayName))) return;
        ModManager.Delete(mod);
        LoadMods();
    }

    // ── 目录 ────────────────────────────────────────────────

    private void OpenGameDir_OnClick(object sender, RoutedEventArgs e)
    {
        var directory = _instance is null
            ? Context.Paths.GameDirectory
            : Context.WorkingDirectoryFor(_instance);
        OpenFolder(directory);
    }

    private void OpenInstanceDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        OpenFolder(Context.Instances.InstanceDirectory(_instance));
    }

    private void OpenModsDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        OpenFolder(Context.ModsDirectoryFor(_instance));
    }

    /// <summary>跳到在线下载模组页，并预选当前编辑的这个实例。</summary>
    private void DownloadMods_OnClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenModDownloadFor(_instance);
    }

    private void OpenLogDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        OpenFolder(Context.Instances.InstanceLogDirectory(_instance));
    }

    private void OpenCrashDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        OpenFolder(Context.Instances.InstanceCrashDirectory(_instance));
    }

    private void OpenLauncherLog_OnClick(object sender, RoutedEventArgs e)
        => OpenFolder(Context.Paths.LogDirectory);

    private static void OpenFolder(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Win32Exception) { }
        catch (InvalidOperationException) { }
    }

    // ── 启动 ────────────────────────────────────────────────

    private async void LaunchDebug_OnClick(object sender, RoutedEventArgs e)
        => await LaunchAsync(true);

    private async void LaunchNormal_OnClick(object sender, RoutedEventArgs e)
        => await LaunchAsync(false);

    private async Task LaunchAsync(bool debugMode)
    {
        if (_instance is null)
        {
            Notice.Info(L.T("String.Msg.InstanceSettings.NoInstanceSelected"));
            return;
        }

        Save();
        try
        {
            var result = await App.GameLauncher.LaunchAsync(_instance, debugMode);
            if (!result.Success)
                Notice.Error(L.F("String.Msg.InstanceSettings.LaunchFailed", result.ErrorMessage));
        }
        catch (Exception ex)
        {
            Log.Error("Instance", "Launch failed", ex);
            Notice.Error(ex.Message);
        }
    }

    private void OpenSavesDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        var directory = Path.Combine(Context.WorkingDirectoryFor(_instance), "saves");
        if (!Directory.Exists(directory)) directory = Context.WorkingDirectoryFor(_instance);
        OpenFolder(directory);
    }

    private void CopyCommand_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        try
        {
            var account = Context.Accounts.Current(Context.Settings.SelectedAccountId);
            var preview = Context.Launcher.BuildPreview(_instance.VersionId, account, new LaunchOptions());
            Clipboard.SetText(preview);
            QuickLaunchStatus.Text = L.T("String.Msg.InstanceSettings.CommandCopied");
        }
        catch (Exception ex)
        {
            Log.Warn("Instance", "Copy command failed: " + ex.Message);
        }
    }

    private async void QuickLaunchNormal_OnClick(object sender, RoutedEventArgs e)
    {
        QuickLaunchStatus.Text = L.T("String.Msg.InstanceSettings.Launching");
        await LaunchAsync(false);
        QuickLaunchStatus.Text = L.T("String.Msg.InstanceSettings.LaunchDone");
    }

    private async void QuickLaunchDebug_OnClick(object sender, RoutedEventArgs e)
    {
        QuickLaunchStatus.Text = L.T("String.Msg.InstanceSettings.LaunchingDebug");
        await LaunchAsync(true);
        QuickLaunchStatus.Text = L.T("String.Msg.InstanceSettings.LaunchDone");
    }

    // ── 文件 ────────────────────────────────────────────────

    private async void CompleteFiles_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            VerifyStatusText.Text = L.T("String.Msg.InstanceSettings.Completing");
            var progress = new Progress<DownloadAggregate>(agg =>
                VerifyStatusText.Text = L.F("String.Msg.InstanceSettings.CompletingProgress",
                    agg.Completed, agg.Total));

            // 补到这个实例真正的工作目录：实例指向自己的文件夹时，
            // 不能把文件下到共享 .minecraft 里，否则补全完还是缺文件。
            var count = await App.GameLauncher.CompleteFilesAsync(
                _instance, progress, CancellationToken.None, Context.WorkingDirectoryFor(_instance));
            VerifyStatusText.Text = count == 0
                ? L.T("String.Msg.InstanceSettings.AllComplete")
                : L.F("String.Msg.InstanceSettings.CompleteDone", count);
        }
        catch (Exception ex)
        {
            VerifyStatusText.Text = L.F("String.Msg.InstanceSettings.CompleteFailed", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void CheckIntegrity_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        var missing = App.GameLauncher.FindMissing(_instance);
        VerifyStatusText.Text = missing.Count == 0
            ? L.T("String.Msg.InstanceSettings.AllComplete")
            : L.F("String.Msg.InstanceSettings.MissingCount", missing.Count);
    }

    private void ApplyIsolation_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        Save();
        Context.Instances.ApplyIsolationLayout(_instance, Context.Paths.GameDirectory);
        WorkingDirText.Text = Context.WorkingDirectoryFor(_instance);
        Notice.Info(L.T("String.Msg.InstanceSettings.IsolationApplied"));
    }

    private void ViewCommand_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        var preview = Context.Launcher.BuildPreview(_instance.VersionId,
            Context.Accounts.Current(Context.Settings.SelectedAccountId),
            App.GameLauncher.BuildOptions(_instance, Context.Accounts.Current(Context.Settings.SelectedAccountId)));
        Notice.Info(L.T("String.Msg.InstanceSettings.CommandTitle"),
            string.IsNullOrWhiteSpace(preview) ? L.T("String.Msg.InstanceSettings.NoPreview") : preview);
    }

    private void PickJava_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Lang.T("String.Msg.Common.JavaFilter"), Title = L.T("String.Msg.Common.PickJava") };
        if (dialog.ShowDialog() == true)
        {
            JavaBox.Text = dialog.FileName;
            OverrideJavaCheck.IsChecked = true;
            Setting_OnChanged(sender, e);
        }
    }
}
