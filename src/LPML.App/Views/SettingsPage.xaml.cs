using LPML.App.Services;
using LPML.Core;
using LPML.Core.Auth;
using LPML.Core.Config;
using LPML.Core.Utils;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using ThemeMode = LPML.Core.Config.ThemeMode;
using LPML.Core.Java;
using LPML.Core.Download;
using LPML.App.Windows;

namespace LPML.App.Views;

public partial class SettingsPage : UserControl, IParameterizedPage, IRefreshablePage
{
    /// <summary>XAML 解析完成标志：初始化期间控件未必全部创建，事件需跳过。</summary>
    private bool _ready;
    public LauncherContext Context { get; }
    private readonly MainWindow? _mainWindow;
    private bool _loaded;

    private static readonly Dictionary<ResourceCategory, string> CategoryNames = new()
    {
        [ResourceCategory.VersionManifest] = Lang.T("String.Msg.Settings.Cat.VersionManifest"),
        [ResourceCategory.VersionJson] = Lang.T("String.Msg.Settings.Cat.VersionJson"),
        [ResourceCategory.ClientJar] = Lang.T("String.Msg.Settings.Cat.ClientJar"),
        [ResourceCategory.Libraries] = Lang.T("String.Msg.Settings.Cat.Libraries"),
        [ResourceCategory.AssetsIndex] = Lang.T("String.Msg.Settings.Cat.AssetsIndex"),
        [ResourceCategory.Assets] = Lang.T("String.Msg.Settings.Cat.Assets"),
        [ResourceCategory.Forge] = "Forge",
        [ResourceCategory.NeoForge] = "NeoForge",
        [ResourceCategory.FabricMeta] = "Fabric Meta",
        [ResourceCategory.FabricMaven] = "Fabric Maven",
        [ResourceCategory.QuiltMeta] = "Quilt Meta",
        [ResourceCategory.OptiFine] = "OptiFine",
        [ResourceCategory.JavaRuntime] = Lang.T("String.Msg.Settings.Cat.JavaRuntime"),
        [ResourceCategory.AuthlibInjector] = "authlib-injector",
        [ResourceCategory.Modrinth] = "Modrinth",
        [ResourceCategory.CurseForge] = "CurseForge",
        [ResourceCategory.Skin] = Lang.T("String.Msg.Settings.Cat.Skin")
    };

    public SettingsPage(LauncherContext context, MainWindow? mainWindow = null)
    {
        Context = context;
        _mainWindow = mainWindow;
        InitializeComponent();
        Loaded += (_, _) => { _ready = true; Refresh(); };
    }

    public void ApplyArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return;
        var parts = arguments.Split('=', 2);
        if (parts.Length != 2 || parts[0] != "tab") return;

        // 按 Tag 找分组。以前用写死的索引，中间插入一个分组后全部错位
        // ——表现就是点"Java"却展开"账户"。用 Tag 就不会受顺序变动影响。
        var expanders = FindExpanders();
        foreach (var expander in expanders) expander.IsExpanded = false;

        var target = expanders.FirstOrDefault(e => string.Equals(e.Tag as string, parts[1], StringComparison.OrdinalIgnoreCase));
        if (target is null) return;

        target.IsExpanded = true;
        // 等布局生效后再滚动，否则 BringIntoView 会滚到旧位置
        Dispatcher.BeginInvoke(new Action(() => target.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private List<Expander> FindExpanders()
    {
        var result = new List<Expander>();
        var root = Content as ScrollViewer;
        var stack = root?.Content as StackPanel;
        if (stack is null) return result;
        foreach (var child in stack.Children)
            if (child is Expander expander) result.Add(expander);
        return result;
    }

    /// <summary>
    /// 按 Tag 选中下拉项。MCBBS 停运后下拉项不再是 0/1/2/3 连续排列，
    /// 继续用 SelectedIndex 当枚举值会让「光速镜像」错位成别的源。
    /// </summary>
    private static void SelectComboByTag(ComboBox combo, int tag)
    {
        var text = tag.ToString();
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), text, StringComparison.Ordinal))
            { combo.SelectedIndex = i; return; }
        }
        combo.SelectedIndex = 0;
    }

    /// <summary>读取当前选中项的 Tag；-1 表示「跟随全局」。</summary>
    private static int? SelectedComboTag(ComboBox combo)
    {
        if (combo.SelectedItem is not ComboBoxItem item) return null;
        return int.TryParse(item.Tag?.ToString(), out var tag) ? tag : null;
    }

    public void Refresh()
    {
        _loaded = false;
        var s = Context.Settings;

        // 读全局默认值本身，不能读某个分类的 Resolve 结果（那会被分类覆盖带偏）
        var defaultKind = s.Mirror.Default == MirrorKind.MCBBS ? MirrorKind.BMCLAPI : s.Mirror.Default;
        SelectComboByTag(DefaultMirrorCombo, (int)defaultKind);
        AutoFastestCheck.IsChecked = s.Mirror.AutoSelectFastest;
        BmclapiBaseBox.Text = s.Mirror.CustomBaseUrls.TryGetValue(MirrorKind.BMCLAPI, out var bmclapi) ? bmclapi : "";
        FastMCMirrorBaseBox.Text = s.Mirror.CustomBaseUrls.TryGetValue(MirrorKind.FastMCMirror, out var fastmc) ? fastmc : "";
        BuildMirrorCategoryRows();

        ConcurrentBox.Text = s.Download.MaxConcurrentTasks.ToString();
        ChunksBox.Text = s.Download.MaxChunksPerFile.ToString();
        ChunkThresholdBox.Text = s.Download.ChunkThresholdMb.ToString();
        RetryBox.Text = s.Download.RetryCount.ToString();
        RampUpCheck.IsChecked = s.Download.EnableRampUp;
        RampUpStartBox.Text = s.Download.RampUpStartCount.ToString();
        RampUpIntervalBox.Text = s.Download.RampUpIntervalMs.ToString();
        SizeAwareCheck.IsChecked = s.Download.EnableSizeAwareConcurrency;
        SmallFileConcurrencyBox.Text = s.Download.SmallFileConcurrency.ToString();
        LargeFileConcurrencyBox.Text = s.Download.LargeFileConcurrency.ToString();
        LargeFileThresholdBox.Text = s.Download.LargeFileThresholdMb.ToString();
        TimeoutBox.Text = s.Download.TimeoutSeconds.ToString();
        SpeedLimitBox.Text = s.Download.SpeedLimitKb.ToString();
        VerifySha1Check.IsChecked = s.Download.VerifySha1;
        SkipExistingCheck.IsChecked = s.Download.SkipExistingFiles;
        RangeFallbackCheck.IsChecked = s.Download.FallbackWhenRangeUnsupported;
        UseProxyCheck.IsChecked = s.Download.UseProxy;
        ProxyBox.Text = s.Download.ProxyAddress;

        TempModeCombo.SelectedIndex = (int)s.Temp.Mode;
        TempLimitBox.Text = s.Temp.MaxTempSizeMb.ToString();
        TempFileLimitBox.Text = s.Temp.SingleFileTempLimitMb.ToString();
        ClearTempCheck.IsChecked = s.Temp.ClearOnExit;
        ClearLocalTempCheck.IsChecked = s.Temp.ClearLocalTempOnExit;
        CacheLimitBox.Text = s.Temp.MaxCacheSizeMb.ToString();
        UpdateTempStatus();

        GameDirBox.Text = s.Paths.GameDirectory;
        CacheDirBox.Text = s.Paths.CacheDirectory;
        IsolationCheck.IsChecked = s.Paths.InstanceIsolation;
        IsolationModeCombo.SelectedIndex = s.Paths.IsolationMode == LPML.Core.Instances.IsolationMode.Full ? 1 : 0;
        OfficialProfilesCheck.IsChecked = s.Paths.WriteOfficialProfiles;
        RootDirText.Text = Lang.T("String.Msg.Settings.RootDirPrefix") + Context.Paths.RootDirectory + Lang.T("String.Msg.Settings.RootDirSuffix");

        LanguageCombo.SelectedIndex = (int)s.Language;
        LogMonitorCheck.IsChecked = s.Privacy.EnableLogMonitoring;
        LogWindowCheck.IsChecked = s.Privacy.LaunchWithLogWindow;
        NotifyPortCheck.IsChecked = s.Privacy.NotifyServerPort;
        NotifyCrashCheck.IsChecked = s.Privacy.NotifyOnCrash;
        ShowLogOnCrashCheck.IsChecked = s.Privacy.ShowLogWindowOnCrash;
        SaveGameLogCheck.IsChecked = s.Privacy.SaveGameLogToFile;

        AccountStorageCombo.SelectedIndex = (int)s.Account.StorageLocation;
        AccountCustomDirBox.Text = s.Account.StorageCustomDirectory;
        AutoOpenBrowserCheck.IsChecked = s.Account.AutoOpenBrowser;
        ImportedDirList.ItemsSource = null;
        ImportedDirList.ItemsSource = Context.Accounts.ImportedDirectories;
        UpdateAccountStorageText();

        JavaPathBox.Text = s.Java.JavaPath ?? "";
        JavaModeCombo.SelectedIndex = (int)s.Java.SelectionMode;
        JavaPreferenceCombo.SelectedIndex = (int)s.Java.RuntimePreference;
        JavaFailureCombo.SelectedIndex = (int)s.Java.OnFailure;
        JavaRetriesBox.Text = s.Java.MaxJavaRetries.ToString();
        RefreshJavaList();
        UpdateJavaModeHint();
        AutoJavaCheck.IsChecked = s.Java.AutoDetectJava;
        AutoRuntimeCheck.IsChecked = s.Java.AutoDownloadJavaRuntime;
        AutoMemoryCheck.IsChecked = s.Java.AutoMemory;
        MinMemoryBox.Text = s.Java.MinMemoryMb.ToString();
        MaxMemoryBox.Text = s.Java.MaxMemoryMb.ToString();
        MemoryRatioSlider.Value = s.Java.AutoMemoryRatio;
        MemoryRatioText.Text = $"{s.Java.AutoMemoryRatio * 100:0}%";
        JvmArgsBox.Text = s.Java.JvmArguments;
        EnvBox.Text = s.Java.EnvironmentVariables;
        PriorityCombo.SelectedIndex = PriorityIndex(s.Java.ProcessPriority);

        WidthBox.Text = s.Launch.WindowWidth.ToString();
        HeightBox.Text = s.Launch.WindowHeight.ToString();
        FullscreenCheck.IsChecked = s.Launch.Fullscreen;
        AfterLaunchCombo.SelectedIndex = (int)s.Launch.AfterLaunch;
        GameArgsBox.Text = s.Launch.GameArguments;
        ServerBox.Text = s.Launch.QuickJoinServer;
        WorldBox.Text = s.Launch.QuickJoinWorld;
        CheckBeforeLaunchCheck.IsChecked = s.Launch.CheckGameCompleteBeforeLaunch;
        AutoDownloadMissingCheck.IsChecked = s.Launch.AutoDownloadMissingFiles;
        CustomTitleCheck.IsChecked = s.Launch.UseCustomWindowTitle;
        CustomTitleBox.Text = s.Launch.CustomWindowTitle;

        PerformanceCombo.SelectedIndex = (int)s.Appearance.PerformanceMode;
        ThemeCombo.SelectedIndex = (int)s.Appearance.Theme;
        BackdropCombo.SelectedIndex = (int)s.Appearance.Backdrop;
        OpacitySlider.Value = s.Appearance.BackdropOpacity;
        LauncherTitleBox.Text = s.Appearance.LauncherTitle;

        BackgroundBox.Text = s.Appearance.BackgroundImage;

        // 背景透明度
        BackgroundOpacitySlider.Value = Math.Clamp(s.Appearance.BackgroundOpacity, 0.05, 1.0);
        BackgroundOpacityText.Text = $"{Math.Clamp(s.Appearance.BackgroundOpacity, 0.05, 1.0) * 100:0}%";

        // 填充方式
        BackgroundStretchCombo.ItemsSource = new List<StretchOption>
        {
            new() { Value = "UniformToFill", Text = Lang.T("String.Settings.BackgroundStretchFill") },
            new() { Value = "Uniform", Text = Lang.T("String.Settings.BackgroundStretchFit") },
            new() { Value = "None", Text = Lang.T("String.Settings.BackgroundStretchNone") }
        };
        BackgroundStretchCombo.SelectedItem =
            ((List<StretchOption>)BackgroundStretchCombo.ItemsSource)
            .FirstOrDefault(x => x.Value == s.Appearance.BackgroundStretch)
            ?? ((List<StretchOption>)BackgroundStretchCombo.ItemsSource)[0];
        BlurSlider.Value = s.Appearance.BackgroundBlurRadius;
        AccentBox.Text = s.Appearance.AccentColor;
        AnimationCheck.IsChecked = s.Appearance.EnableAnimations;
        StretchCheck.IsChecked = s.Appearance.EnableStretchAnimation;
        AnimationDurationBox.Text = s.Appearance.AnimationDurationMs.ToString();
        CornerSlider.Value = s.Appearance.CornerRadius;

        LogLevelCombo.SelectedIndex = (int)s.Advanced.LogLevel;
        CheckUpdateCheck.IsChecked = s.Advanced.CheckUpdateOnStartup;
        DevModeCheck.IsChecked = s.Advanced.DeveloperMode;
        ShowAdvancedCheck.IsChecked = s.Advanced.ShowAdvancedOptions;
        AutoRefreshAccountCheck.IsChecked = s.Advanced.AutoRefreshAccount;
        GuardEnableCheck.IsChecked = s.Advanced.EnableGuardProcess;
        GuardTimeoutBox.Text = s.Advanced.GuardTimeoutSeconds.ToString();
        GuardAutoKillCheck.IsChecked = s.Advanced.GuardAutoKill;

        _loaded = true;
    }

    /// <summary>
    /// 性能模式切换。会立刻重画：主题画刷（半透明→不透明）、毛玻璃、背景模糊、动效。
    /// </summary>
    private void PerformanceMode_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !_loaded) return;
        if (PerformanceCombo.SelectedIndex < 0) return;

        Context.Settings.Appearance.PerformanceMode =
            PerformanceCombo.SelectedIndex == 1 ? PerformanceMode.Smooth : PerformanceMode.Standard;

        Save();
        App.ApplySettings();
    }

    private static int PriorityIndex(string priority) => priority?.ToLowerInvariant() switch
    {
        "idle" => 0,
        "belownormal" => 1,
        "normal" => 2,
        "abovenormal" => 3,
        "high" => 4,
        _ => 2
    };

    private void BuildMirrorCategoryRows()
    {
        MirrorCategoryPanel.Children.Clear();
        foreach (var (category, name) in CategoryNames)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var combo = new ComboBox
            {
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = category,
                Style = (Style)FindResource("ComboBox.Glass")
            };
            combo.Items.Add(new ComboBoxItem { Content = Lang.T("String.Msg.Settings.FollowGlobal"), Tag = "-1" });
            combo.Items.Add(new ComboBoxItem { Content = Lang.T("String.Msg.Settings.OfficialMirror"), Tag = "0" });
            combo.Items.Add(new ComboBoxItem { Content = "BMCLAPI", Tag = "1" });
            combo.Items.Add(new ComboBoxItem { Content = Lang.T("String.Mirror.FastMCMirror"), Tag = "3" });

            var current = Context.Settings.Mirror.Overrides.TryGetValue(category, out var kind) ? (int)kind : -1;
            if (current == (int)MirrorKind.MCBBS) current = (int)MirrorKind.BMCLAPI;
            SelectComboByTag(combo, current);
            combo.SelectionChanged += MirrorCategory_OnSelectionChanged;

            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
            MirrorCategoryPanel.Children.Add(grid);
        }
    }

    private void MirrorCategory_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || sender is not ComboBox combo) return;
        if (combo.Tag is not ResourceCategory category) return;
        var tag = SelectedComboTag(combo);
        if (tag is null) return;
        if (tag < 0)
            Context.Settings.Mirror.Overrides.Remove(category);
        else
            Context.Settings.Mirror.Overrides[category] = (MirrorKind)tag.Value;
        Context.ApplySettings();
        Context.Save();
    }

    private void UpdateTempStatus()
    {
        var mode = Context.Settings.Temp.Mode;
        var usage = Context.Temp.SystemTempEnabled
            ? Core.Utils.FileHelper.HumanSize(Context.Temp.SystemTempUsage)
            : "0 B";
        var cache = Core.Utils.FileHelper.HumanSize(Core.Utils.FileHelper.GetDirectorySize(Context.Paths.CacheDirectory));
        TempStatusText.Text = Lang.F("String.Msg.Settings.TempStatus", usage, ModeName(mode), cache);
    }

    private static string ModeName(TempWriteMode mode) => mode switch
    {
        TempWriteMode.Never => Lang.T("String.Msg.Settings.TempNever"),
        TempWriteMode.SmallFilesOnly => Lang.T("String.Msg.Settings.TempSmallOnly"),
        _ => Lang.T("String.Msg.Settings.TempAlways")
    };

    private void LanguageCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;

        var language = (LauncherLanguage)Math.Max(0, LanguageCombo.SelectedIndex);
        Context.Settings.Language = language;
        Save();

        // 换语言包后，所有 {DynamicResource String.*} 自动刷新；导航树是代码生成的，需要重建
        App.Language.Apply(language);
        App.RefreshLanguageDependentUi();
    }

    /// <summary>一键应用"装得更快"的下载参数（并发 16 / 单文件 8 段 / 2MB 起分段）。</summary>
    private void ApplyFastDownload_OnClick(object sender, RoutedEventArgs e)
    {
        var d = Context.Settings.Download;
        d.MaxConcurrentTasks = 32;
        d.MaxChunksPerFile = 8;
        d.ChunkThresholdMb = 2;
        Context.Settings.Download.SpeedLimitKb = 0;
        // 并发开高但平滑放行，兼顾速度和不被限流
        d.EnableRampUp = true;
        d.RampUpStartCount = 4;
        d.RampUpIntervalMs = 60;
        // 小文件多开、大文件收窄，配合平滑放行
        d.EnableSizeAwareConcurrency = true;
        d.SmallFileConcurrency = 48;
        d.LargeFileConcurrency = 8;
        d.LargeFileThresholdMb = 5;
        Context.Save();
        Refresh();
        Notice.Info(Lang.T("String.Msg.Settings.FastApplied"));
    }

    /// <summary>切换全局隔离模式：改设置本身，不搬运已有文件（搬运要点下面的按钮）。</summary>
    private void IsolationMode_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        Save();
    }

    /// <summary>
    /// 在所有实例之间按当前模式迁移隔离数据。
    /// 完整隔离：版本文件搬进实例目录；用户数据隔离：搬回 .minecraft。
    /// </summary>
    private async void MigrateIsolation_OnClick(object sender, RoutedEventArgs e)
    {
        var target = Context.Settings.Paths.IsolationMode;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            var total = 0;
            foreach (var instance in Context.Instances.Instances)
            {
                var mode = Context.IsolationModeFor(instance);
                var shared = Context.Paths.GameDirectory;
                total += await Task.Run(() =>
                    Context.Instances.MigrateIsolation(instance, shared, mode)).ConfigureAwait(true);
            }
            Notice.Info(Lang.F("String.Msg.Settings.MigrateDone", total));
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

    private void Setting_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // 初始化保护：XAML 解析期间控件尚未全部创建，此时事件可能提前触发（如 Slider 设 Minimum/Maximum 会引发 ValueChanged）
        if (AccountCustomDirBox is null || AccountStorageCombo is null) return;
        if (!_loaded) return;
        Save();
        App.ApplySettings();
        // 账户存放位置可能刚变，刷新实际路径提示
        if (ReferenceEquals(sender, AccountStorageCombo) || ReferenceEquals(sender, AccountCustomDirBox))
            UpdateAccountStorageText();
    }

    private void Save()
    {
        var s = Context.Settings;

        s.Mirror.Default = (MirrorKind)(SelectedComboTag(DefaultMirrorCombo) ?? (int)MirrorKind.BMCLAPI);
        s.Mirror.AutoSelectFastest = AutoFastestCheck.IsChecked == true;
        SetCustomBaseUrl(MirrorKind.BMCLAPI, BmclapiBaseBox.Text);
        SetCustomBaseUrl(MirrorKind.FastMCMirror, FastMCMirrorBaseBox.Text);

        s.Download.MaxConcurrentTasks = Int(ConcurrentBox, 6);
        s.Download.MaxChunksPerFile = Int(ChunksBox, 8);
        s.Download.ChunkThresholdMb = Int(ChunkThresholdBox, 8);
        s.Download.RetryCount = Int(RetryBox, 3);
        s.Download.EnableRampUp = RampUpCheck.IsChecked == true;
        if (int.TryParse(RampUpStartBox.Text, out var rampStart)) s.Download.RampUpStartCount = Math.Clamp(rampStart, 1, 128);
        if (int.TryParse(RampUpIntervalBox.Text, out var rampMs)) s.Download.RampUpIntervalMs = Math.Clamp(rampMs, 0, 2000);
        s.Download.EnableSizeAwareConcurrency = SizeAwareCheck.IsChecked == true;
        if (int.TryParse(SmallFileConcurrencyBox.Text, out var smallC)) s.Download.SmallFileConcurrency = Math.Clamp(smallC, 1, 128);
        if (int.TryParse(LargeFileConcurrencyBox.Text, out var largeC)) s.Download.LargeFileConcurrency = Math.Clamp(largeC, 1, 128);
        if (int.TryParse(LargeFileThresholdBox.Text, out var largeMb)) s.Download.LargeFileThresholdMb = Math.Clamp(largeMb, 1, 512);
        s.Download.TimeoutSeconds = Int(TimeoutBox, 30);
        s.Download.SpeedLimitKb = Int(SpeedLimitBox, 0);
        s.Download.VerifySha1 = VerifySha1Check.IsChecked == true;
        s.Download.SkipExistingFiles = SkipExistingCheck.IsChecked == true;
        s.Download.FallbackWhenRangeUnsupported = RangeFallbackCheck.IsChecked == true;
        s.Download.UseProxy = UseProxyCheck.IsChecked == true;
        s.Download.ProxyAddress = ProxyBox.Text.Trim();

        s.Temp.Mode = (TempWriteMode)Math.Max(0, TempModeCombo.SelectedIndex);
        s.Temp.MaxTempSizeMb = Long(TempLimitBox, 512);
        s.Temp.SingleFileTempLimitMb = Long(TempFileLimitBox, 64);
        s.Temp.ClearOnExit = ClearTempCheck.IsChecked == true;
        s.Temp.ClearLocalTempOnExit = ClearLocalTempCheck.IsChecked == true;
        s.Temp.MaxCacheSizeMb = Long(CacheLimitBox, 0);

        s.Paths.GameDirectory = GameDirBox.Text.Trim();
        s.Paths.CacheDirectory = CacheDirBox.Text.Trim();
        s.Paths.InstanceIsolation = IsolationCheck.IsChecked == true;
        if (IsolationModeCombo.SelectedItem is ComboBoxItem isoItem && isoItem.Tag?.ToString() == "Full")
            s.Paths.IsolationMode = LPML.Core.Instances.IsolationMode.Full;
        else
            s.Paths.IsolationMode = LPML.Core.Instances.IsolationMode.UserData;
        s.Paths.WriteOfficialProfiles = OfficialProfilesCheck.IsChecked == true;

        s.Language = (LauncherLanguage)Math.Max(0, LanguageCombo.SelectedIndex);
        s.Privacy.EnableLogMonitoring = LogMonitorCheck.IsChecked == true;
        s.Privacy.LaunchWithLogWindow = LogWindowCheck.IsChecked == true;
        s.Privacy.NotifyServerPort = NotifyPortCheck.IsChecked == true;
        s.Privacy.NotifyOnCrash = NotifyCrashCheck.IsChecked == true;
        s.Privacy.ShowLogWindowOnCrash = ShowLogOnCrashCheck.IsChecked == true;
        s.Privacy.SaveGameLogToFile = SaveGameLogCheck.IsChecked == true;

        s.Account.StorageLocation = (AccountStorageLocation)Math.Max(0, AccountStorageCombo.SelectedIndex);
        s.Account.StorageCustomDirectory = AccountCustomDirBox.Text.Trim();
        s.Account.AutoOpenBrowser = AutoOpenBrowserCheck.IsChecked == true;
        s.Account.ImportedDirectories = Context.Accounts.ImportedDirectories.ToList();

        s.Java.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? null : JavaPathBox.Text.Trim();
        s.Java.RuntimePreference = (LPML.Core.Java.JavaRuntimePreference)Math.Max(0, JavaPreferenceCombo.SelectedIndex);
        s.Java.OnFailure = (LPML.Core.Java.JavaFailureAction)Math.Max(0, JavaFailureCombo.SelectedIndex);
        s.Java.MaxJavaRetries = Math.Clamp(Int(JavaRetriesBox, 3), 1, 5);
        s.Java.AutoDetectJava = AutoJavaCheck.IsChecked == true;
        s.Java.AutoDownloadJavaRuntime = AutoRuntimeCheck.IsChecked == true;
        s.Java.AutoMemory = AutoMemoryCheck.IsChecked == true;
        s.Java.MinMemoryMb = Int(MinMemoryBox, 512);
        s.Java.MaxMemoryMb = Int(MaxMemoryBox, 4096);
        s.Java.AutoMemoryRatio = MemoryRatioSlider.Value;
        s.Java.JvmArguments = JvmArgsBox.Text;
        s.Java.EnvironmentVariables = EnvBox.Text;
        s.Java.ProcessPriority = (PriorityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Normal";

        s.Launch.WindowWidth = Int(WidthBox, 1280);
        s.Launch.WindowHeight = Int(HeightBox, 720);
        s.Launch.Fullscreen = FullscreenCheck.IsChecked == true;
        s.Launch.AfterLaunch = (AfterLaunchAction)Math.Max(0, AfterLaunchCombo.SelectedIndex);
        s.Launch.GameArguments = GameArgsBox.Text;
        s.Launch.QuickJoinServer = ServerBox.Text.Trim();
        s.Launch.QuickJoinWorld = WorldBox.Text.Trim();
        s.Launch.CheckGameCompleteBeforeLaunch = CheckBeforeLaunchCheck.IsChecked == true;
        s.Launch.AutoDownloadMissingFiles = AutoDownloadMissingCheck.IsChecked == true;
        s.Launch.UseCustomWindowTitle = CustomTitleCheck.IsChecked == true;
        s.Launch.CustomWindowTitle = CustomTitleBox.Text;

        s.Appearance.Theme = (ThemeMode)Math.Max(0, ThemeCombo.SelectedIndex);
        s.Appearance.Backdrop = (BackdropMode)Math.Max(0, BackdropCombo.SelectedIndex);
        s.Appearance.BackdropOpacity = OpacitySlider.Value;
        s.Appearance.BackgroundImage = BackgroundBox.Text.Trim();
        s.Appearance.LauncherTitle = LauncherTitleBox.Text.Trim();
        s.Appearance.BackgroundBlurRadius = BlurSlider.Value;
        s.Appearance.AccentColor = AccentBox.Text.Trim();
        s.Appearance.EnableAnimations = AnimationCheck.IsChecked == true;
        s.Appearance.EnableStretchAnimation = StretchCheck.IsChecked == true;
        s.Appearance.AnimationDurationMs = Int(AnimationDurationBox, 260);
        s.Appearance.CornerRadius = CornerSlider.Value;

        s.Advanced.LogLevel = (LogLevel)Math.Max(0, LogLevelCombo.SelectedIndex);
        s.Advanced.CheckUpdateOnStartup = CheckUpdateCheck.IsChecked == true;
        s.Advanced.DeveloperMode = DevModeCheck.IsChecked == true;
        s.Advanced.ShowAdvancedOptions = ShowAdvancedCheck.IsChecked == true;
        s.Advanced.AutoRefreshAccount = AutoRefreshAccountCheck.IsChecked == true;
        s.Advanced.EnableGuardProcess = GuardEnableCheck.IsChecked == true;
        s.Advanced.GuardTimeoutSeconds = Math.Clamp(Int(GuardTimeoutBox, 20), 5, 300);
        s.Advanced.GuardAutoKill = GuardAutoKillCheck.IsChecked == true;

        Context.ApplySettings();
        Context.Save();
        UpdateTempStatus();
    }

    private void SetCustomBaseUrl(MirrorKind kind, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) Context.Settings.Mirror.CustomBaseUrls.Remove(kind);
        else Context.Settings.Mirror.CustomBaseUrls[kind] = value.Trim().TrimEnd('/');
    }

    private static int Int(TextBox box, int fallback)
        => int.TryParse(box.Text.Trim(), out var value) ? value : fallback;

    private static long Long(TextBox box, long fallback)
        => long.TryParse(box.Text.Trim(), out var value) ? value : fallback;

    private void BrowseGameDir_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialogCompat(Lang.T("String.Msg.Settings.PickGameDir"));
        if (dialog.Show(Context.Paths.GameDirectory) is string path) GameDirBox.Text = path;
        Setting_OnChanged(sender, e);
    }

    private void BrowseCacheDir_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialogCompat(Lang.T("String.Msg.Settings.PickCacheDir"));
        if (dialog.Show(Context.Paths.CacheDirectory) is string path) CacheDirBox.Text = path;
        Setting_OnChanged(sender, e);
    }

    private void BrowseAccountDir_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialogCompat(Lang.T("String.Msg.Settings.PickAccountDir"));
        if (dialog.Show(Context.Paths.AccountDirectory) is string path) AccountCustomDirBox.Text = path;
        Setting_OnChanged(sender, e);
    }

    private void OpenAccountDir_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Context.Paths.AccountDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Context.Paths.AccountDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", Lang.T("String.Msg.Common.OpenDirFailed") + ex.Message);
        }
    }

    /// <summary>显示账户数据实际落在哪里。</summary>
    private void UpdateAccountStorageText()
    {
        var location = (AccountStorageLocation)Math.Max(0, AccountStorageCombo.SelectedIndex);
        var resolved = location switch
        {
            AccountStorageLocation.UserProfile => AppPaths.UserProfileAccountDirectory,
            AccountStorageLocation.Custom when !string.IsNullOrWhiteSpace(AccountCustomDirBox.Text.Trim())
                => Context.Paths.Expand(AccountCustomDirBox.Text.Trim()),
            _ => Context.Accounts.PortableDirectory
        };
        AccountStorageText.Text =
            Lang.F("String.Msg.Settings.NewAccountTarget", resolved) +
            Lang.F("String.Msg.Settings.PortableLine", Context.Accounts.PortableDirectory) +
            Lang.F("String.Msg.Settings.UserProfileDir", Context.Accounts.UserProfileDirectory);
    }

    private void ImportAccountDir_OnClick(object sender, RoutedEventArgs e)
    {
        var directory = AccountCustomDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            Notice.Info(Lang.T("String.Msg.Settings.NeedImportPath"));
            return;
        }

        var count = Context.Accounts.ImportCustomDirectory(directory);
        Save();
        App.ApplySettings();
        RefreshImportedDirList();
        Notice.Info(Lang.F("String.Msg.Settings.Imported", directory, count));
    }

    private void UnimportAccountDir_OnClick(object sender, RoutedEventArgs e)
    {
        if (ImportedDirList.SelectedItem is not string directory) return;

        Context.Accounts.RemoveImportedDirectory(directory);
        Save();
        App.ApplySettings();
        RefreshImportedDirList();
        UpdateAccountStorageText();
    }

    private void ImportedDirList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ImportedDirList.SelectedItem is string directory) AccountCustomDirBox.Text = directory;
    }

    private void MoveAllAccounts_OnClick(object sender, RoutedEventArgs e)
    {
        var location = (AccountStorageLocation)Math.Max(0, AccountStorageCombo.SelectedIndex);
        if (location == AccountStorageLocation.Custom && string.IsNullOrWhiteSpace(AccountCustomDirBox.Text))
        {
            Notice.Info(Lang.T("String.Msg.Settings.NeedCustomDir"));
            return;
        }
        if (Context.Accounts.Accounts.Count == 0) return;

        if (!Notice.Ask(Lang.F("String.Msg.Settings.ConfirmMoveAll", Context.Accounts.Accounts.Count, AccountStorageCombo.Text))) return;

        var count = Context.Accounts.MoveAll(location, AccountCustomDirBox.Text.Trim());
        Save();
        App.ApplySettings();
        RefreshImportedDirList();
        UpdateAccountStorageText();
        Notice.Error(Lang.F("String.Msg.Settings.MovedAll", count));
    }

    private void RefreshImportedDirList()
    {
        ImportedDirList.ItemsSource = null;
        ImportedDirList.ItemsSource = Context.Accounts.ImportedDirectories;
    }

    // ── Java 列表管理 ──────────────────────────────────────

    private LPML.Core.Java.JavaInfo? SelectedJava => JavaList.SelectedItem as LPML.Core.Java.JavaInfo;

    private void RefreshJavaList()
    {
        JavaList.ItemsSource = null;
        JavaList.ItemsSource = Context.Javas.Javas;
        UpdateJavaSelectionLabel();
    }

    private void UpdateJavaSelectionLabel()
    {
        var java = SelectedJava;
        JavaSelectionLabel.Text = java is null
            ? Lang.T("String.Java.NoneSelected")
            : $"{java.Name} — Java {java.MajorVersion} · {java.SourceText}";
        _javaLoading = true;
        JavaNicknameBox.Text = java?.Nickname ?? "";
        _javaLoading = false;
    }

    private bool _javaLoading;

    private void JavaList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateJavaSelectionLabel();

    /// <summary>添加 Java：选中可执行文件后自动同步整个 JDK 与兄弟 JDK。</summary>
    private void AddJava_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Lang.T("String.Msg.Common.JavaFilter"),
            Title = Lang.T("String.Java.PickTitle")
        };
        if (dialog.ShowDialog() != true) return;

        var added = Context.Javas.Add(dialog.FileName);
        RefreshJavaList();
        if (SelectedJava is null && added.Count > 0) JavaList.SelectedItem = added[0];

        if (added.Count > 0) Notice.Info(Lang.F("String.Java.AddedCount", added.Count));
        else Notice.Warn(Lang.T("String.Java.AddFailed"));
    }

    /// <summary>全量扫描：PATH + 常见安装目录 + 游戏 runtime。</summary>
    private void ScanJava_OnClick(object sender, RoutedEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var added = Context.Javas.ScanSystem();
            RefreshJavaList();
            Notice.Info(added.Count > 0
                    ? Lang.F("String.Java.ScannedCount", added.Count)
                    : Lang.T("String.Java.NoNewJava"));
        }
        finally { Mouse.OverrideCursor = null; }
    }

    /// <summary>只同步 PATH 与 JAVA_HOME（快）。</summary>
    private void SyncPathJava_OnClick(object sender, RoutedEventArgs e)
    {
        var added = Context.Javas.SyncFromEnvironment();
        RefreshJavaList();
        Notice.Info(added.Count > 0
                ? Lang.F("String.Java.SyncedCount", added.Count)
                : Lang.T("String.Java.NoNewJava"));
    }

    /// <summary>逐条验证列表里的 Java 是否仍然可用。</summary>
    private void ValidateJava_OnClick(object sender, RoutedEventArgs e)
    {
        var broken = Context.Javas.Validate();
        RefreshJavaList();
        Notice.Info(broken == 0
                ? Lang.T("String.Java.AllValid")
                : Lang.F("String.Java.InvalidCount", broken));
    }

    private void RemoveJava_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedJava is not { } java) return;
        if (!Notice.Ask(Lang.F("String.Java.ConfirmRemove", java.Name))) return;
        Context.Javas.Remove(java);
        RefreshJavaList();
    }

    /// <summary>设为固定使用的 Java（同时把策略切到手动）。</summary>
    private void PinJava_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedJava is not { } java) return;
        Context.Settings.Java.PreferredJavaId = java.Id;
        Context.Settings.Java.SelectionMode = LPML.Core.Java.JavaSelectionMode.Manual;
        Context.Save();
        JavaModeCombo.SelectedIndex = (int)LPML.Core.Java.JavaSelectionMode.Manual;
        UpdateJavaModeHint();
        Notice.Info(Lang.F("String.Java.Pinned", java.Name));
    }

    private void JavaNickname_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_javaLoading || SelectedJava is not { } java) return;
        Context.Javas.SetNickname(java, JavaNicknameBox.Text);
        RefreshJavaList();
    }

    private void JavaMode_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loaded) return;
        var mode = (LPML.Core.Java.JavaSelectionMode)Math.Max(0, JavaModeCombo.SelectedIndex);
        Context.Settings.Java.SelectionMode = mode;

        // 切到手动但没指定过：把当前选中项作为默认，避免「手动模式却没选任何 Java」
        if (mode == LPML.Core.Java.JavaSelectionMode.Manual)
        {
            if (string.IsNullOrWhiteSpace(Context.Settings.Java.PreferredJavaId) && SelectedJava is { } java)
                Context.Settings.Java.PreferredJavaId = java.Id;
        }
        else
        {
            Context.Settings.Java.PreferredJavaId = null;
        }

        Context.Save();
        UpdateJavaModeHint();
    }

    private void UpdateJavaModeHint()
    {
        var mode = (LPML.Core.Java.JavaSelectionMode)Math.Max(0, JavaModeCombo.SelectedIndex);
        if (mode == LPML.Core.Java.JavaSelectionMode.Manual)
        {
            var pinned = Context.Javas.Javas.FirstOrDefault(j => j.Id == Context.Settings.Java.PreferredJavaId);
            JavaModeHint.Text = pinned is null
                ? Lang.T("String.Java.ModeManualNoPick")
                : Lang.F("String.Java.ModeManualHint", pinned.Name);
        }
        else
        {
            JavaModeHint.Text = Lang.T("String.Java.ModeAutoHint");
        }
    }

    private void BrowseJava_OnClick(object sender, RoutedEventArgs e)
    {
        var fileDialog = new OpenFileDialog { Filter = Lang.T("String.Msg.Common.JavaFilter"), Title = Lang.T("String.Msg.Common.PickJava") };
        if (fileDialog.ShowDialog() != true) return;

        JavaPathBox.Text = fileDialog.FileName;
        // 同时并入 Java 列表（会自动同步同目录的其他 Java）
        Context.Javas.Add(fileDialog.FileName);
        RefreshJavaList();
        Setting_OnChanged(sender, e);
    }

    private void BrowseBackground_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = Lang.T("String.Msg.Settings.ImageFilter"),
            Title = Lang.T("String.Msg.Settings.PickBackground")
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // 复制到 LPML 目录下，原图之后再删/移动也不影响
            var source = dialog.FileName;
            var extension = Path.GetExtension(source);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".png";

            var directory = Context.Paths.BackgroundDirectory;
            Directory.CreateDirectory(directory);

            // 清掉旧图，只保留当前这一张
            foreach (var old in Directory.GetFiles(directory))
            {
                try { File.Delete(old); } catch (IOException) { }
            }

            var target = Context.Paths.BackgroundFile("background" + extension);
            File.Copy(source, target, true);

            BackgroundBox.Text = target;
            Setting_OnChanged(sender, e);
            _mainWindow?.ApplyBackgroundPublic();
        }
        catch (Exception ex)
        {
            Log.Error("Settings", Lang.T("String.Settings.BackgroundCopyFailed"), ex);
            Notice.Warn(Lang.T("String.Settings.BackgroundCopyFailed") + ex.Message);
        }
    }

    /// <summary>改启动器标题：失焦即生效，窗口标题马上变。</summary>
    private void LauncherTitle_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_ready || LauncherTitleBox is null) return;
        Save();
        _mainWindow?.ApplyLauncherTitle();
    }

    /// <summary>背景透明度滑杆：实时改，同时更新百分比。</summary>
    private void BackgroundOpacity_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || BackgroundOpacityText is null) return;
        var value = Math.Clamp(BackgroundOpacitySlider.Value, 0.05, 1.0);
        Context.Settings.Appearance.BackgroundOpacity = value;
        BackgroundOpacityText.Text = $"{value * 100:0}%";
        Context.Save();
        _mainWindow?.ApplyBackgroundPublic();
    }

    /// <summary>填充方式：铺满 / 完整显示 / 原始尺寸。</summary>
    private void BackgroundStretch_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (BackgroundStretchCombo.SelectedItem is not StretchOption option) return;
        Context.Settings.Appearance.BackgroundStretch = option.Value;
        Context.Save();
        _mainWindow?.ApplyBackgroundPublic();
    }

    /// <summary>填充方式下拉项。</summary>
    private sealed class StretchOption
    {
        public string Value { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private void ClearBackground_OnClick(object sender, RoutedEventArgs e)
    {
        BackgroundBox.Text = "";
        Setting_OnChanged(sender, e);
        _mainWindow?.ApplyBackgroundPublic();
    }

    private void ClearCache_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Core.Utils.FileHelper.DeleteDirectory(Context.Paths.TempDirectory);
            Core.Utils.FileHelper.DeleteDirectory(Context.Temp.SystemTempRoot);
            UpdateTempStatus();
            Notice.Info(Lang.T("String.Msg.Settings.Cleaned"));
        }
        catch (Exception ex)
        {
            Notice.Error(Lang.T("String.Msg.Settings.CleanFailedTitle"), ex.Message);
        }
    }

    private void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "lpml-settings.json" };
        if (dialog.ShowDialog() != true) return;
        Core.Utils.FileHelper.WriteAllTextAsync(dialog.FileName, Json.Serialize(Context.Settings)).GetAwaiter().GetResult();
    }

    private void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        var loaded = Json.Load<LauncherSettings>(dialog.FileName);
        if (loaded is null) { Notice.Warn(Lang.T("String.Msg.Settings.ParseFailed")); return; }
        Context.SettingsService.Settings = loaded;
        Context.ApplySettings();
        App.ApplySettings();
        Refresh();
    }

    private void Reset_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Notice.Ask(Lang.T("String.Msg.Settings.ConfirmReset"))) return;
        Context.SettingsService.Settings = new LauncherSettings();
        Context.ApplySettings();
        App.ApplySettings();
        Refresh();
    }
    /// <summary>Java 运行时下载（原本在下载中心，现归到"Java 与内存"）。</summary>
    private async void DownloadJavaRuntime_OnClick(object sender, RoutedEventArgs e)
    {
        if (JavaComponentCombo?.SelectedItem is not ComboBoxItem item) return;
        var component = (item.Content?.ToString() ?? "java-runtime-gamma").Split(' ')[0];

        try
        {
            JavaRuntimeStatusText.Text = Lang.T("String.Msg.Download.FetchingRuntime");

            // 跨芯片架构不能假设本机架构，必须让用户确认
            var options = await Context.Java.RuntimeArchOptionsAsync();
            if (options.Count == 0)
            {
                Notice.Info(Lang.T("String.Cli.NoArchAvailable"));
                return;
            }

            var arch = options.Count == 1 ? options[0] : PromptArch(options);
            if (arch is null)
            {
                JavaRuntimeStatusText.Text = Lang.T("String.Java.Arch.Cancelled");
                return;
            }

            var items = await Context.Java.BuildRuntimeDownloadItemsAsync(component, arch.Arch);
            if (items.Count == 0)
            {
                JavaRuntimeStatusText.Text = Lang.T("String.Msg.Download.Failed") + Lang.T("String.Cli.NoArchAvailable");
                return;
            }

            JavaRuntimeStatusText.Text = Lang.F("String.Msg.Download.Downloading", component);
            var runtimeTask = Context.Downloads.BeginTask(component, "game");
            Context.Downloads.Enqueue(items, runtimeTask);
            await Context.Downloads.RunAsync();
            Context.Downloads.MarkFinished(runtimeTask);

            var exe = Context.Java.FindRuntimeJava(component, arch.Arch);
            if (exe is not null) Context.Javas.Add(exe);

            JavaRuntimeStatusText.Text = Lang.T("String.Msg.Download.Done") + Context.Java.RuntimeRoot;
        }
        catch (Exception ex)
        {
            JavaRuntimeStatusText.Text = Lang.T("String.Msg.Download.Failed") + ex.Message;
            Log.Error("Settings", Lang.T("String.Msg.Download.RuntimeFailedTitle"), ex);
        }
    }

    /// <summary>架构选择：列出远程索引里真实存在的架构，推荐依据已有 Java 自报的 os.arch。</summary>
    private JavaArchOption? PromptArch(List<JavaArchOption> options)
    {
        var dialog = new Window
        {
            Title = Lang.T("String.Java.Arch.Title"),
            Width = 460, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this),
            Background = (System.Windows.Media.Brush)FindResource("Brush.Window")
        };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = Lang.T("String.Java.Arch.Hint"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = (System.Windows.Media.Brush)FindResource("Brush.ForegroundSecondary")
        });
        var list = new ListBox { ItemsSource = options, DisplayMemberPath = "Display", Margin = new Thickness(0, 0, 0, 14) };
        var recommended = options.FirstOrDefault(o => o.IsSuggested) ?? options[0];
        list.SelectedItem = recommended;
        root.Children.Add(list);

        JavaArchOption? picked = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = Lang.T("String.Msg.Common.OK"), Style = (Style)FindResource("Button.Primary"), Padding = new Thickness(18, 7, 18, 7), IsDefault = true };
        var cancel = new Button { Content = Lang.T("String.Msg.Common.Cancel"), Style = (Style)FindResource("Button.Base"), Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => { picked = list.SelectedItem as JavaArchOption; dialog.DialogResult = true; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dialog.Content = root;

        return dialog.ShowDialog() == true ? picked : null;
    }


}
