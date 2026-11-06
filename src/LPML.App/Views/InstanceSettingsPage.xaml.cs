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
        var selected = Context.Instances.Find(Context.Settings.SelectedInstanceId)
                       ?? Context.Instances.Instances.FirstOrDefault();
        ApplyNoInstanceState(selected is null);
        if (selected is null) return;

        _instance = selected;
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

        if (DirOverrideBox is not null) DirOverrideBox.Text = instance.DirectoryOverride ?? "";
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

    // ── 实例文件夹 ──────────────────────────────────────────

    /// <summary>刷新文件夹章节：下拉、当前目录、该文件夹里的实例。</summary>
    private void LoadFolderSection()
    {
        if (_instance is null) return;
        try
        {
            var folders = new List<InstanceFolder>
            {
                new() { Id = "", Name = L.T("String.InstanceSettings.FolderDefault") }
            };
            folders.AddRange(Context.Folders.Folders);

            FolderCombo.ItemsSource = folders;
            var currentId = _instance.FolderId ?? "";
            FolderCombo.SelectedItem = folders.FirstOrDefault(f => f.Id == currentId) ?? folders[0];

            CurrentDirText.Text = Context.WorkingDirectoryFor(_instance);

            // 该文件夹里的实例
            var inFolder = Context.Instances.Instances
                .Where(i => string.Equals(i.FolderId ?? "", currentId, StringComparison.Ordinal))
                .ToList();
            FolderInstanceList.ItemsSource = inFolder;
            if (inFolder.Count > 0)
                FolderInstanceList.SelectedItem = inFolder.FirstOrDefault(i => i.Id == _instance.Id);
        }
        catch (Exception ex)
        {
            Log.Warn("Instance", "Load folder section failed: " + ex.Message);
        }
    }

    private void FolderCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (_instance is null) return;
        if (FolderCombo.SelectedItem is not InstanceFolder folder) return;

        _instance.FolderId = string.IsNullOrEmpty(folder.Id) ? null : folder.Id;
        Context.Instances.Save();
        CurrentDirText.Text = Context.WorkingDirectoryFor(_instance);
        LoadFolderInstances();
    }

    private void LoadFolderInstances()
    {
        if (_instance is null) return;
        var currentId = _instance.FolderId ?? "";
        FolderInstanceList.ItemsSource = Context.Instances.Instances
            .Where(i => string.Equals(i.FolderId ?? "", currentId, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>直接指定目录（相对或绝对），保存后立刻生效。</summary>
    private void DirOverride_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (_instance is null) return;
        var text = DirOverrideBox.Text.Trim();
        _instance.DirectoryOverride = string.IsNullOrEmpty(text) ? null : text;
        Context.Instances.Save();
        CurrentDirText.Text = Context.WorkingDirectoryFor(_instance);
    }

    private void BrowseDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = L.T("String.InstanceSettings.DirBrowseHint"),
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            DirOverrideBox.Text = dialog.SelectedPath;
            DirOverride_OnChanged(sender, e);
        }
        catch (Exception ex)
        {
            Log.Warn("Instance", "Browse dir failed: " + ex.Message);
        }
    }

    /// <summary>新建一个实例文件夹（名称 + 路径，路径支持相对 / 绝对）。</summary>
    private void NewFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = L.T("String.InstanceSettings.FolderNew"),
            Width = 520, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this),
            Background = (System.Windows.Media.Brush)FindResource("Brush.Window")
        };

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = L.T("String.InstanceSettings.FolderNewHint"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            Foreground = (System.Windows.Media.Brush)FindResource("Brush.ForegroundSecondary")
        });

        root.Children.Add(new TextBlock { Text = L.T("String.InstanceSettings.FolderName"), Margin = new Thickness(0, 0, 0, 6) });
        var nameBox = new TextBox { Padding = new Thickness(8, 6, 8, 6) };
        root.Children.Add(nameBox);

        root.Children.Add(new TextBlock { Text = L.T("String.InstanceSettings.FolderPath"), Margin = new Thickness(0, 14, 0, 6) });
        var pathGrid = new Grid();
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pathBox = new TextBox { Padding = new Thickness(8, 6, 8, 6) };
        var browse = new Button
        {
            Content = L.T("String.InstanceSettings.DirBrowse"),
            Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("Button.Base")
        };
        browse.Click += (_, _) =>
        {
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog();
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) pathBox.Text = dlg.SelectedPath;
            }
            catch (Exception ex) { Log.Warn("Instance", "Browse failed: " + ex.Message); }
        };
        pathGrid.Children.Add(pathBox);
        Grid.SetColumn(pathBox, 0);
        pathGrid.Children.Add(browse);
        Grid.SetColumn(browse, 1);
        root.Children.Add(pathGrid);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = new Button { Content = L.T("String.Msg.Common.OK"), Style = (Style)FindResource("Button.Primary"), Padding = new Thickness(18, 7, 18, 7), IsDefault = true };
        var cancel = new Button { Content = L.T("String.Msg.Common.Cancel"), Style = (Style)FindResource("Button.Base"), Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var path = pathBox.Text.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
            {
                Notice.Info(L.T("String.InstanceSettings.FolderNeedBoth"));
                return;
            }
            var folder = Context.Folders.Create(name, path);
            Context.Folders.EnsureLayout(folder);
            window.DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        window.Content = root;

        if (window.ShowDialog() == true && _instance is not null)
        {
            LoadFolderSection();
            if (FolderCombo.Items.Count > 1) FolderCombo.SelectedIndex = FolderCombo.Items.Count - 1;
        }
    }

    /// <summary>把该版本的完整文件下载安装到当前实例目录（实例文件夹）里。</summary>
    private async void InstallToFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (_instance is null) return;
        var target = Context.WorkingDirectoryFor(_instance);
        try
        {
            InstallToFolderButton.IsEnabled = false;
            FolderInstallStatus.Text = L.F("String.InstanceSettings.InstallingTo", target);
            var progress = new Progress<DownloadAggregate>(agg =>
                FolderInstallStatus.Text = L.F("String.InstanceSettings.CompletingProgress", agg.Completed, agg.Total));

            var count = await App.GameLauncher.CompleteFilesAsync(_instance, progress, CancellationToken.None, target);
            FolderInstallStatus.Text = count == 0
                ? L.T("String.InstanceSettings.InstallUpToDate")
                : L.F("String.InstanceSettings.InstallDone", count, target);
        }
        catch (Exception ex)
        {
            Log.Error("Instance", "Install to folder failed", ex);
            FolderInstallStatus.Text = L.T("String.Msg.Common.Failed") + ex.Message;
        }
        finally
        {
            InstallToFolderButton.IsEnabled = true;
        }
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
            "folder" => PaneFolder,
            "quick" => PaneQuick,
            "mods" => PaneMods,
            "isolation" => PaneIsolation,
            "advanced" => PaneAdvanced,
            "diagnostics" => PaneDiagnostics,
            _ => PaneOverview
        };
        target.Visibility = Visibility.Visible;

        if (tag == "mods") LoadMods();
        if (tag == "folder") LoadFolderSection();
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
        PaneOverview, PaneGame, PaneFolder, PaneQuick, PaneMods, PaneIsolation, PaneAdvanced, PaneDiagnostics
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

            var count = await App.GameLauncher.CompleteFilesAsync(_instance, progress);
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
