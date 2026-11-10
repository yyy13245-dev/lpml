using System.ComponentModel;
using LPML.App.Services;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Auth;
using LPML.Core.Config;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Media.Effects;

namespace LPML.App;

/// <summary>导航项：支持无限层级的分级菜单。</summary>
public sealed class NavItem
{
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string? PageKey { get; set; }
    /// <summary>非空时该项不切换页面，而是弹出对应的账户登录小窗口。</summary>
    public string? DialogKind { get; set; }
    public List<NavItem> Children { get; set; } = new();
}

public partial class MainWindow : Window
{
    /// <summary>XAML 解析完成标志：初始化期间控件未必全部创建，事件需跳过。</summary>
    private bool _ready;
    public LauncherContext Context { get; }

    private readonly Dictionary<string, Func<UserControl>> _pageFactories = new();
    private List<NavItem>? _sections;
    private NavItem? _currentSection;
    private readonly Dictionary<string, UserControl> _pageCache = new();

    public MainWindow(LauncherContext context)
    {
        Context = context;
        InitializeComponent();
        _ready = true;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        if (App.GameSession is not null)
            App.GameSession.RunningChanged += OnGameRunningChanged;
    }

    /// <summary>有实例启动或退出时，刷新「结束游戏」按钮与展开板里的列表。</summary>
    private void OnGameRunningChanged(object? sender, bool running)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnGameRunningChanged(sender, running)); return; }
        UpdateStartButton();
        RefreshSessionList();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyPerformanceMode();

        ApplyLauncherTitle();
        ApplyBackground();
        BuildNavigation();
        RefreshAccount();
        Navigate("play");
        Context.Accounts.AccountsChanged += (_, _) => Dispatcher.Invoke(RefreshAccount);
    }

    /// <summary>
    /// 应用性能模式。流畅模式关掉毛玻璃与背景模糊（两者都是逐像素开销），
    /// 动图背景也停在第一帧。切换模式下会立刻重画，不用重启。
    /// </summary>
    public void ApplyPerformanceMode()
    {
        var appearance = Context.Settings.Appearance;
        var smooth = appearance.PerformanceMode == PerformanceMode.Smooth;

        try
        {
            if (smooth) AcrylicHelper.Disable(this);
            else AcrylicHelper.Enable(this, appearance.Backdrop);
        }
        catch (Exception ex) { Log.Warn("Acrylic", "Backdrop unavailable: " + ex.Message); }

        ApplyBackground();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        Context.Save();
    }

    public void ApplyBackgroundPublic() => ApplyBackground();

    /// <summary>应用启动器窗口标题（设置里改了立刻生效）。</summary>
    public void ApplyLauncherTitle()
    {
        var custom = Context.Settings.Appearance.LauncherTitle;
        Title = string.IsNullOrWhiteSpace(custom) ? "LPML" : custom.Trim();
    }

    private void ApplyBackground()
    {
        var path = Context.Settings.Appearance.BackgroundImage;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                // GIF 交给专门的播放器（WPF 原生只显示第一帧）
                bitmap.EndInit();

                // GIF 用播放器逐帧播，静态图直接显示
                // 流畅模式下动图停在第一帧：逐帧解码是持续的 CPU / GPU 开销
                var smooth = Context.Settings.Appearance.PerformanceMode == PerformanceMode.Smooth;
                if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) && !smooth)
                {
                    _gif ??= new Helpers.GifAnimator(BackgroundImage);
                    if (_gif.Play(path))
                    {
                        BackgroundImage.Visibility = Visibility.Visible;
                        ApplyBackgroundStyle();
                        return;
                    }
                }
                else
                {
                    _gif?.Stop();
                }

                BackgroundImage.Source = bitmap;
                BackgroundImage.Visibility = Visibility.Visible;
                ApplyBackgroundStyle();
            }
            catch (Exception)
            {
                BackgroundImage.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            _gif?.Stop();
            BackgroundImage.Visibility = Visibility.Collapsed;
        }

        ApplyBackgroundBlur();
    }

    /// <summary>背景 GIF 播放器（静态图时为 null 或已停止）。</summary>
    private Helpers.GifAnimator? _gif;

    /// <summary>背景模糊：流畅模式强制为 0（模糊是最贵的一项逐像素开销）。</summary>
    private void ApplyBackgroundBlur()
    {
        if (BackgroundImage.Effect is not BlurEffect effect) return;
        var smooth = Context.Settings.Appearance.PerformanceMode == PerformanceMode.Smooth;
        effect.Radius = smooth ? 0 : Context.Settings.Appearance.BackgroundBlurRadius;
    }

    /// <summary>应用背景图的透明度 / 填充方式。</summary>
    private void ApplyBackgroundStyle()
    {
        BackgroundImage.Opacity = Math.Clamp(Context.Settings.Appearance.BackgroundOpacity, 0.05, 1.0);
        BackgroundImage.Stretch = Context.Settings.Appearance.BackgroundStretch switch
        {
            "Uniform" => Stretch.Uniform,
            "None" => Stretch.None,
            _ => Stretch.UniformToFill
        };

        // 模糊放这里：GIF 分支会提前 return，之前会漏掉
        ApplyBackgroundBlur();
    }

    /// <summary>当前展示的页面 key，切换语言后用它重建。</summary>
    private string _currentPageKey = "play";

    /// <summary>
    /// 语言包换掉后：导航树是代码生成的、页面里有纯文本绑定之外的动态文本，
    /// 所以清空页面缓存重建一次；DynamicResource 部分会自动刷新。
    /// </summary>
    public void RebuildForLanguage()
    {
        var key = _currentPageKey;
        var inSection = Level2.Visibility == Visibility.Visible && _currentSection is not null;

        BuildNavigation();
        RefreshAccount();
        _pageCache.Clear();

        if (inSection && _currentSection is not null)
        {
            // 二级界面：重建子项（标题已本地化）再回到原来那一页
            SubNavList.ItemsSource = _currentSection.Children;
            var target = _currentSection.Children.FirstOrDefault(c => c.PageKey == key)
                         ?? _currentSection.Children.FirstOrDefault();
            SubNavList.SelectedItem = target;
            ShowSubPage(target?.PageKey ?? _currentSection.PageKey ?? "play");
        }
        else
        {
            ShowLevel1();
            Navigate(string.IsNullOrEmpty(key) ? "play" : key);
        }
    }

    private void BuildNavigation()
    {
        _sections = new List<NavItem>
        {
            // 开始游戏：无子项，直接打开（也是右下角常驻按钮的目标）
            new() { Title = Lang.T("String.Play.Title"), Icon = "▶", PageKey = "play" },

            new()
            {
                Title = Lang.T("String.Instance.Title"), Icon = "🧩", PageKey = "instances",
                Children =
                {
                    new() { Title = Lang.T("String.Instance.Title"), Icon = "🧩", PageKey = "instances" },
                    new() { Title = Lang.T("String.Nav.Folders"), Icon = "📁", PageKey = "folders" },
                    new() { Title = Lang.T("String.Nav.InstanceSettings"), Icon = "⚙", PageKey = "instancesettings" },
                    new() { Title = Lang.T("String.Nav.Mods"), Icon = "🧰", PageKey = "mods" },
                    new() { Title = Lang.T("String.Content.Title"), Icon = "🌐", PageKey = "content" },
                    new() { Title = Lang.T("String.Multi.Title"), Icon = "🔗", PageKey = "multiplayer" }
                }
            },
            new()
            {
                Title = Lang.T("String.Download.Title"), Icon = "⬇", PageKey = "download",
                Children =
                {
                    new() { Title = Lang.T("String.Download.TabInstall"), Icon = "🎮", PageKey = "download" },
                    new() { Title = Lang.T("String.Download.TabModpack"), Icon = "📦", PageKey = "download?tab=pack" },
                    new() { Title = Lang.T("String.Download.TabModDownload"), Icon = "🧩", PageKey = "moddownload" }
                }
            },
            new()
            {
                Title = Lang.T("String.Settings.Title"), Icon = "⚙", PageKey = "settings",
                Children =
                {
                    new() { Title = Lang.T("String.Nav.Mirror"), Icon = "🌍", PageKey = "settings?tab=mirror" },
                    new() { Title = Lang.T("String.Nav.DownloadSettings"), Icon = "⚡", PageKey = "settings?tab=download" },
                    new() { Title = Lang.T("String.Nav.Temp"), Icon = "🧹", PageKey = "settings?tab=temp" },
                    new() { Title = Lang.T("String.Nav.JavaMemory"), Icon = "☕", PageKey = "settings?tab=java" },
                    new() { Title = Lang.T("String.Nav.LaunchSettings"), Icon = "🚀", PageKey = "settings?tab=launch" },
                    new() { Title = Lang.T("String.Nav.Appearance"), Icon = "🎨", PageKey = "settings?tab=appearance" },
                    new() { Title = Lang.T("String.Nav.Advanced"), Icon = "🛠", PageKey = "settings?tab=advanced" },
                    new() { Title = Lang.T("String.Log.Title"), Icon = "📜", PageKey = "logs" },
                    new() { Title = Lang.T("String.About.Title"), Icon = "ℹ", PageKey = "about" }
                }
            },

            // 文档：放在顶层，点开就是文档列表，不用先进设置再找
            new() { Title = Lang.T("String.Help.Nav"), Icon = "📖", PageKey = "help" }
        };

        NavList.ItemsSource = _sections;

        // 下载任务监听：有任务才显示「任务」按钮
        Context.Downloads.ItemChanged += OnDownloadChanged;
        Context.Downloads.ProgressChanged += OnDownloadAggregate;
        Context.Downloads.Finished += OnDownloadAggregate;

        _pageFactories["play"] = () => new Views.PlayPage(Context) { MainWindow = this };
        _pageFactories["instances"] = () => new Views.InstancesPage(Context);
        _pageFactories["folders"] = () => new Views.FoldersPage(Context);
        _pageFactories["instancesettings"] = () => new Views.InstanceSettingsPage();
        _pageFactories["download"] = () => new Views.DownloadPage(Context);
        _pageFactories["moddownload"] = () => new Views.ModDownloadPage(Context);
        _pageFactories["content"] = () => new Views.ContentPage();
        _pageFactories["mods"] = () => new Views.ModsPage(Context);
        _pageFactories["accounts"] = () => new Views.AccountsPage(Context);
        _pageFactories["settings"] = () => new Views.SettingsPage(Context, this);
        _pageFactories["logs"] = () => new Views.LogPage(Context);
        _pageFactories["multiplayer"] = () => new Views.MultiplayerPage(Context);
        _pageFactories["about"] = () => new Views.AboutPage(Context);
        _pageFactories["help"] = () => new Views.HelpPage();
    }

    /// <summary>账户页：作为二级界面从左下角账户入口进入。</summary>
    private static List<NavItem> AccountSections() => new()
    {
        new() { Title = Lang.T("String.Account.TabMicrosoft"), Icon = "🪟", PageKey = "accounts", DialogKind = "microsoft" },
        new() { Title = Lang.T("String.Account.TabOffline"), Icon = "🔌", PageKey = "accounts", DialogKind = "offline" },
        new() { Title = Lang.T("String.Account.TabYggdrasil"), Icon = "🌐", PageKey = "accounts", DialogKind = "yggdrasil" },
        new() { Title = Lang.T("String.Account.LittleSkinTitle"), Icon = "🎀", PageKey = "accounts", DialogKind = "littleskin" }
    };

    private void NavList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNav || !_ready) return;
        if (NavList.SelectedItem is not NavItem item) return;
        if (!CanNavigateAway()) { RestoreNav(); return; }

        // 无子项（如"开始游戏"）：直接在一级页显示
        if (item.Children.Count == 0)
        {
            ShowLevel1();
            Navigate(item.PageKey!);
            UpdateStartButton();
            return;
        }

        // 有子项：进入二级界面（整页覆盖），默认打开第一个子项
        OpenSection(item);
    }

    /// <summary>打开二级分区：整页覆盖，左侧是子项，右上角有返回。</summary>
    private void OpenSection(NavItem section)
    {
        _currentSection = section;
        BackText.Text = Lang.T("String.Main.Back");
        SubNavList.ItemsSource = section.Children;
        Level1.Visibility = Visibility.Collapsed;
        Level2.Visibility = Visibility.Visible;
        if (section.Children.Count > 0)
        {
            SubNavList.SelectedIndex = 0;
            ShowSubPage(section.Children[0].PageKey!);
        }
    }

    private void SubNavList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNav) return;
        if (SubNavList.SelectedItem is not NavItem item) return;
        if (!string.IsNullOrEmpty(item.DialogKind))
        {
            ShowAccountDialog(item.DialogKind!);
            return;
        }
        if (string.IsNullOrEmpty(item.PageKey)) return;
        if (!CanNavigateAway()) { RestoreNav(); return; }
        ShowSubPage(item.PageKey!);
    }

    /// <summary>账户侧栏四项各自弹一个小窗口，页面本身始终是完整的账户列表。</summary>
    private void ShowAccountDialog(string kind)
    {
        var dialog = new Views.AccountLoginWindow(kind) { Owner = this };
        dialog.ShowDialog();
        // 弹窗里添加 / 删除账户后，回到刷新过的列表
        ShowSubPage("accounts");
        _suppressNav = true;
        try { SubNavList.SelectedItem = null; }
        finally { _suppressNav = false; }
    }

    /// <summary>账户弹窗改动账户后，让主界面的账户列表跟上。</summary>
    public void RefreshAccountsPage()
    {
        if (_pageCache.TryGetValue("accounts", out var page) && page is Views.AccountsPage accounts)
            accounts.Refresh();
    }

    // 用户取消「离开房间」时，把导航选中项还原回去，避免界面显示与房间状态不一致。
    private bool _suppressNav;

    private void RestoreNav()
    {
        _suppressNav = true;
        try
        {
            if (Level2.Visibility == Visibility.Visible)
                SubNavList.SelectedItem = _currentSection?.Children.FirstOrDefault(c => c.PageKey == "multiplayer");
            else
                NavList.SelectedItem = _currentSection;
        }
        finally { _suppressNav = false; }
    }

    /// <summary>在二级界面里显示某个子页面。</summary>
    private void ShowSubPage(string pageKey)
    {
        var page = GetOrCreatePage(pageKey);
        if (page is null) return;
        SubPageHost.Content = page;
        _currentPageKey = pageKey;
        AnimationHelper.PlayPageEnter(page);
        UpdateStartButton();
    }

    private void Back_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateAway()) return;
        ShowLevel1();
        NavList.SelectedItem = null;
    }

    /// <summary>回到一级界面。</summary>
    private void ShowLevel1()
    {
        Level2.Visibility = Visibility.Collapsed;
        Level1.Visibility = Visibility.Visible;
        StartGameButton.Visibility = Visibility.Visible;
        _currentSection = null;
    }

    /// <summary>右下角常驻"开始游戏"：跳到开始游戏页并启动当前选中的实例。</summary>
    private async void StartGame_OnClick(object sender, RoutedEventArgs e)
    {
        // 允许多开：这个按钮始终用来启动，结束游戏走旁边的「结束游戏」按钮
        if (!CanNavigateAway()) return;

        // 已经在开始游戏页：直接启动，省掉一次点击
        if (Level2.Visibility != Visibility.Visible && _currentPageKey == "play")
        {
            if (FindPage("play") is Views.PlayPage play) await play.LaunchAsync();
            return;
        }

        if (Level2.Visibility == Visibility.Visible) ShowLevel1();
        NavList.SelectedItem = _sections?.FirstOrDefault();
        Navigate("play");
    }

    /// <summary>左下角账户入口：以二级界面打开账户页。</summary>
    private void AccountEntry_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateAway()) return;
        var root = new NavItem
        {
            Title = Lang.T("String.Account.Title"),
            Icon = "👤",
            PageKey = "accounts",
            Children = AccountSections()
        };
        BackText.Text = Lang.T("String.Main.Back");
        _currentSection = root;
        SubNavList.ItemsSource = root.Children;
        Level1.Visibility = Visibility.Collapsed;
        Level2.Visibility = Visibility.Visible;
        _suppressNav = true;
        try
        {
            SubNavList.ItemsSource = root.Children;
            // 不预先选中：否则点击已选中的「微软登录」不会触发事件，弹窗出不来
            SubNavList.SelectedIndex = -1;
        }
        finally { _suppressNav = false; }
        ShowSubPage("accounts");
    }

    /// <summary>按 key 取（或创建）页面；带 ?tab= 参数的会先传参。</summary>
    private UserControl? GetOrCreatePage(string pageKey)
    {
        var key = pageKey.Split('?')[0];
        var argument = pageKey.Contains('?') ? pageKey.Split('?')[1] : "";

        if (!_pageFactories.TryGetValue(key, out var factory)) return null;
        if (!_pageCache.TryGetValue(key, out var page))
        {
            page = factory();
            if (page is null) return null;
            _pageCache[key] = page;
        }

        if (page is IParameterizedPage parameterized) parameterized.ApplyArguments(argument);
        if (page is IRefreshablePage refreshable) refreshable.Refresh();
        return page;
    }

    /// <summary>切换页面（带淡入 + 上浮 + 拉伸动效）。</summary>
    public bool CanNavigateAway()
    {
        // 房间页是二级页里的 detail 内容，侧边栏 / 关闭窗口离开时也要结束房间。
        var host = Level2.Visibility == Visibility.Visible ? SubPageHost : PageHost;
        if (host.Content is Views.MultiplayerRoomPage room) return room.ConfirmLeave();
        if (_pageCache.TryGetValue("multiplayer", out var mp) && mp is Views.MultiplayerPage page && !page.ConfirmLeave()) return false;
        return true;
    }

    /// <summary>
    /// 实例设置的唯一入口：实例页双击和侧栏「实例设置」都走这里，
    /// 显示同一个页面、同一个实例，不再出现「一处单实例、一处全局」两套界面。
    /// </summary>
    public void ShowInstanceSettings(LPML.Core.Instances.GameInstance? instance = null)
    {
        var item = _currentSection?.Children.FirstOrDefault(c => c.PageKey == "instancesettings");
        if (item is not null)
        {
            _suppressNav = true;
            try { SubNavList.SelectedItem = item; }
            finally { _suppressNav = false; }
            ShowSubPage("instancesettings");
        }
        else
        {
            ShowLevel1();
            Navigate("instancesettings");
        }
        if (instance is not null && FindPage("instancesettings") is Views.InstanceSettingsPage page)
            page.SetInstance(instance);
    }

    /// <summary>从房间页 / 创建页 / 加入页回到联机入口（联机在「实例」分区的二级页里）。</summary>
    public void ShowMultiplayer()
    {
        var item = _currentSection?.Children.FirstOrDefault(c => c.PageKey == "multiplayer");
        if (item is not null)
        {
            SubNavList.SelectedItem = item;
            ShowSubPage("multiplayer");
            return;
        }
        ShowLevel1();
        Navigate("multiplayer");
    }

    public void NavigateToContent(FrameworkElement content)
    {
        // 关键：判断当前停在哪一层。
        // 下载中心/实例都在二级（Level2），若塞进一级容器会被盖住看不见——
        // 表现就是"点了没反应，要点返回才出来"。
        var inLevel2 = Level2.Visibility == Visibility.Visible;
        if (inLevel2) SubPageHost.Content = content;
        else PageHost.Content = content;

        _currentPageKey = "detail";
        AnimationHelper.PlayPageEnter(content);
        UpdateStartButton();
    }

    public void Navigate(string pageKey, NavItem? item = null)
    {
        var page = GetOrCreatePage(pageKey);
        if (page is null) return;
        PageHost.Content = page;
        _currentPageKey = pageKey;
        AnimationHelper.PlayPageEnter(page);
        UpdateStartButton();
    }

    // ── 任务中心 ────────────────────────────────────────────

    // 下载事件来自后台线程，必须封送回 UI 线程再碰控件
    private void OnDownloadChanged(object? sender, LPML.Core.Download.DownloadItem e) => UpdateTaskButton();

    private void OnDownloadAggregate(object? sender, LPML.Core.Download.DownloadAggregate e) => UpdateTaskButton();

    /// <summary>有任务显示按钮（带进行中数量），全部结束且清空后隐藏。</summary>
    private void UpdateTaskButton()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateTaskButton); return; }
        try
        {
            var items = Context.Downloads.Items;
            var active = items.Count(i => i.IsActive);

            // 关键：以"任务组"为准。以前看 items，而准备阶段 items 还是空的，
            // 于是按钮不显示，用户以为没点上。
            var has = Context.Downloads.Tasks.Count > 0 || items.Count > 0;

            TaskButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (has)
                TaskButtonText.Text = active > 0
                    ? Lang.F("String.Task.Running", active)
                    : Lang.T("String.Task.Title");

            if (TaskPanel.Visibility == Visibility.Visible && has) TaskCenterView.Refresh();
            if (!has && TaskPanel.Visibility == Visibility.Visible) TaskPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn("UI", "Update task button failed: " + ex.Message);
        }
    }

    private void TaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (TaskPanel.Visibility == Visibility.Visible)
        {
            HideTaskCenter();
            return;
        }
        TaskPanel.Visibility = Visibility.Visible;
        TaskCenterView.Refresh();
    }

    /// <summary>关闭任务面板。</summary>
    public void HideTaskCenter() => TaskPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 版本文件丢失后：跳到下载中心，选中这个实例的版本，并按它原来的加载器
    /// 预选类型，用户只需要点一下「下载」。找不到清单条目时只停在下载中心。
    /// </summary>
    public void OpenVersionInstallFor(LPML.Core.Instances.GameInstance instance)
    {
        Navigate("download");
        var page = FindPage("download") as LPML.App.Views.DownloadPage;
        var entry = page?.SelectVersion(instance.VersionId);
        if (entry is null) return;

        var text = instance.LoadersText ?? "";
        string kind = "";
        if (text.Contains("NeoForge", StringComparison.OrdinalIgnoreCase)) kind = "NeoForge";
        else if (text.Contains("Forge", StringComparison.OrdinalIgnoreCase)) kind = "Forge";
        else if (text.Contains("Fabric", StringComparison.OrdinalIgnoreCase)) kind = "Fabric";
        else if (text.Contains("Quilt", StringComparison.OrdinalIgnoreCase)) kind = "Quilt";

        var install = new LPML.App.Views.VersionInstallPage(this, entry);
        if (!string.IsNullOrEmpty(kind)) install.PreselectLoader(kind);
        NavigateToContent(install);
    }

    /// <summary>
    /// 悬浮按钮文案：始终用来"启动游戏"（允许多个实例并存）。
    /// 有实例在跑时额外显示「结束游戏」按钮，点开是可选实例的小展开板。
    /// </summary>
    private void UpdateStartButton()
    {
        StartGameButton.IsEnabled = true;
        var onPlay = Level2.Visibility != Visibility.Visible
                     && string.Equals(_currentPageKey, "play", StringComparison.Ordinal);
        StartGameButton.Content = onPlay
            ? Lang.T("String.Main.LaunchGame")
            : Lang.T("String.Main.StartGame");

        RefreshSessionList();
    }

    /// <summary>刷新「结束游戏」按钮与展开板里的运行实例列表。</summary>
    private void RefreshSessionList()
    {
        if (SessionList is null || CloseGameButton is null || SessionPanel is null) return;
        var sessions = App.GameSession?.Snapshot() ?? new List<Services.GameSessionInfo>();
        CloseGameButton.Visibility = sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (sessions.Count == 0) SessionPanel.Visibility = Visibility.Collapsed;
        SessionList.ItemsSource = sessions;
        if (SessionList.SelectedIndex < 0 && sessions.Count > 0) SessionList.SelectedIndex = 0;
    }

    /// <summary>从模组页跳进下载模组页，并预选那个实例（进页面后仍可改选）。</summary>
    public void OpenModDownloadFor(LPML.Core.Instances.GameInstance? instance)
    {
        if (instance is not null) Views.ModDownloadPage.PendingInstanceId = instance.Id;
        var section = _sections?.FirstOrDefault(s => s.PageKey == "download");
        if (section is null) return;
        OpenSection(section);
        var target = section.Children.FirstOrDefault(c => c.PageKey == "moddownload");
        if (target is null) return;
        SubNavList.SelectedItem = target;
        ShowSubPage("moddownload");
    }

    private void CloseGameButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshSessionList();
        if (App.GameSession?.RunningCount > 0)
            SessionPanel.Visibility = SessionPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void EndGameCancel_OnClick(object sender, RoutedEventArgs e)
        => SessionPanel.Visibility = Visibility.Collapsed;

    private void EndSelectedGame_OnClick(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not Services.GameSessionInfo picked)
        {
            Notice.Info(Lang.T("String.Msg.Game.SelectSession"));
            return;
        }
        App.GameSession?.Kill(picked.Id);
        SessionPanel.Visibility = Visibility.Collapsed;
        RefreshSessionList();
    }

    /// <summary>供各页面之间跳转与协作（例如：修复缺失文件时跳到下载页）。</summary>
    public UserControl? FindPage(string key)
    {
        var target = GetOrCreatePage(key);
        return target;
    }

    private static bool SelectNavItem(IEnumerable<NavItem> items, NavItem target)
    {
        foreach (var item in items)
        {
            if (ReferenceEquals(item, target)) return true;
            if (SelectNavItem(item.Children, target)) return true;
        }
        return false;
    }

    public void RefreshAccount()
    {
        var account = Context.Accounts.Current(Context.Settings.SelectedAccountId);
        if (account is null)
        {
            AccountName.Text = Lang.T("String.Main.NoAccount");
            AccountType.Text = Lang.T("String.Main.AccountHint");
            return;
        }
        AccountName.Text = account.DisplayName;
        AccountType.Text = account.TypeName;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        DragMove();
    }

    private void Minimize_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>重新启动启动器：清掉内存缓存与页面状态，重新走一次初始化。</summary>
    private void Restart_OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanNavigateAway()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warn("App", "Restart failed: " + ex.Message);
            Notice.Warn(Lang.T("String.Main.Restart") + "： " + ex.Message);
        }
    }

    private void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!CanNavigateAway()) e.Cancel = true;
    }
}

/// <summary>支持参数（如 settings?tab=download）的页面。</summary>
public interface IParameterizedPage
{
    void ApplyArguments(string arguments);
}

/// <summary>每次显示都会刷新的页面。</summary>
public interface IRefreshablePage
{
    void Refresh();
}