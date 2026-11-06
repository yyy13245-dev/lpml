using LPML.App.Guard;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Config;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LPML.App.Windows;

namespace LPML.App;

public partial class App : Application
{
    /// <summary>全局唯一的启动器上下文。</summary>
    public static LauncherContext Context { get; private set; } = null!;

    /// <summary>true 表示命令行模式：不创建窗口，日志打到控制台。</summary>
    public static bool IsCliMode { get; private set; }

    /// <summary>
    /// 命令行指定的性能模式（--smooth / --standard）。
    /// 设置加载后覆盖当前的 PerformanceMode 并立刻写盘，所以下一次启动也保持。
    /// </summary>
    public static PerformanceMode? PendingPerformanceMode { get; set; }

    public static ThemeService Theme { get; private set; } = null!;

    /// <summary>界面语言服务（词条加载与运行时切换）。</summary>
    public static Services.LanguageService Language { get; private set; } = null!;

    /// <summary>统一游戏启动服务（补全文件 / 日志监听 / 崩溃与端口提示）。</summary>
    public static Services.GameLaunchService GameLauncher { get; private set; } = null!;

    /// <summary>
    /// 全局游戏会话（仅在图形界面模式创建）：同一时刻只允许一个游戏进程，
    /// 并提供"关闭游戏"入口。命令行模式为 null，不参与防重复判断。
    /// </summary>
    public static Services.GameSessionService? GameSession { get; private set; }

    /// <summary>
    /// 命令行模式下启动器日志的转发目标。REPL 运行时指向它的日志区，
    /// 避免日志直接 Console.WriteLine 打乱上下分区；null 表示直接打印到控制台。
    /// </summary>
    public static Action<LogEntry>? CliLogHook { get; set; }

    public new static App Current => (App)Application.Current;

    /// <summary>语言包切换后，重建那些由代码生成、无法靠 DynamicResource 自动刷新的界面。</summary>
    public static void RefreshLanguageDependentUi()
    {
        if (Current.MainWindow is MainWindow window) window.RebuildForLanguage();
    }

    /// <summary>
    /// 启动是否走完了 window.Show()。
    /// 未走完就出异常说明主窗口没建起来，此时必须强制结束进程 ——
    /// 否则没有任何窗口可关，进程会一直挂在后台占着文件锁，用户连文件都删不掉。
    /// </summary>
    private static bool _startupCompleted;

    /// <summary>驱动守护进程心跳。必须由 UI 线程触发，UI 卡死时它自然停摆。</summary>
    private static DispatcherTimer? _heartbeatTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 关掉主窗口就整体退出。默认的 OnLastWindowClose 会被独立的日志窗口拖住：
        // 主窗口关了、日志窗口还开着，进程就不退出，文件一直被占用。
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("App", Lang.T("String.Msg.App.UnhandledUi"), args.Exception);

            // 启动阶段没走完：界面都没建起来，继续跑只会空转占着文件
            if (!_startupCompleted)
            {
                FatalExit(args.Exception);
                return;
            }

            var ex = args.Exception;
            Notice.Error(Lang.T("String.Msg.App.Unexpected")
                + $"[{ex.GetType().Name}] {ex.Message}\n\n"
                + (ex.InnerException is { } inner ? $"\n[{inner.GetType().Name}] {inner.Message}\n" : "")
                + Lang.F("String.Msg.App.ErrorHint", ex.TargetSite?.DeclaringType?.Name ?? "-", ex.TargetSite?.Name ?? "-"));
            args.Handled = true;
        };

        try
        {
            Initialize(withUi: true);
            _startupCompleted = true;
        }
        catch (Exception ex)
        {
            // Initialize 是在 OnStartup 里同步抛的，DispatcherUnhandledException
            // 不一定能覆盖到，这里必须自己兜住并结束进程
            FatalExit(ex);
        }
    }

    /// <summary>启动失败：记日志 → 弹窗 → 落盘 → 强制结束进程。</summary>
    private static void FatalExit(Exception ex)
    {
        try
        {
            Log.Error("App", Lang.T("String.Msg.App.StartupFailed"), ex);
            Log.Service.Flush();
        }
        catch { /* 记日志本身失败也不能阻止退出 */ }

        // 把启动失败的日志也交给守护进程，然后断开（进程随即结束）
        try { GuardClient.Current?.Stop(GuardExitReason.StartupFailed); } catch { }

        try
        {
            var inner = ex.InnerException ?? ex;
            Notice.Error(
                Lang.T("String.Msg.App.StartupFailedBody")
                + $"\n[{inner.GetType().Name}] {inner.Message}\n\n"
                + (ex != inner ? $"[{ex.GetType().Name}] {ex.Message}\n\n" : "")
                + Lang.T("String.Msg.App.StartupFailedHint"));
        }
        catch { /* 无界面环境（如远程桌面）直接跳过 */ }

        try { Context?.Shutdown(); } catch { }

        // Shutdown 靠消息循环，启动失败时消息循环未必正常；双保险直接结束进程
        try { System.Windows.Application.Current?.Shutdown(1); } catch { }
        Environment.Exit(1);
    }

    /// <summary>
    /// 公共初始化：语言 → 上下文 → 主题。
    /// 命令行模式走 withUi:false，不建窗口、不装主题，日志改打控制台。
    /// </summary>
    public static void Initialize(bool withUi)
    {
        IsCliMode = !withUi;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log.Error("App", Lang.T("String.Msg.App.Unhandled"), ex);
        };

        // 必须在建任何界面之前：Core 与界面文本都要能取到词条
        Language = new Services.LanguageService();
        Language.Apply(Core.Config.LauncherLanguage.ZhHans);
        L.Provider = Services.LanguageService.Get;

        Context = new LauncherContext();
        Context.Initialize();

        // 设置文件读出来后，按用户选择的语言再挂一次
        Language.Apply(Context.Settings.Language);

        // 命令行指定的性能模式：这次生效，并写进配置让以后也保持
        if (PendingPerformanceMode is { } forced)
        {
            var previous = Context.Settings.Appearance.PerformanceMode;
            if (previous != forced)
            {
                Context.Settings.Appearance.PerformanceMode = forced;
                Context.Save();
            }
            Log.Info("App", Lang.F("String.Msg.App.PerformanceModeApplied",
                forced == PerformanceMode.Smooth
                    ? Lang.T("String.Settings.PerformanceSmooth")
                    : Lang.T("String.Settings.PerformanceStandard")));
        }

        // 命令行模式：日志同步打到 stdout；REPL 运行时改投它的日志区，不破坏分区布局
        if (IsCliMode)
            Log.Service.AttachConsole(entry =>
            {
                var hook = CliLogHook;
                if (hook is not null) hook(entry);
                else Console.WriteLine($"[{entry.Time:HH:mm:ss}] [{entry.Level,-5}] [{entry.Source}] {entry.Message}");
            });

        GameLauncher = new Services.GameLaunchService(Context);

        if (!withUi) return;

        // 必须在建窗口之前：这样首次刷新按钮时就能显示"关闭游戏"（若上次的游戏还开着）
        GameSession = new Services.GameSessionService(Context);
        GameSession.Restore();

        // 关键：App.xaml 已降级为普通 Page 编译（为了支持自定义入口），
        // 这种情况下 InitializeComponent 不保证填充 Application.Resources。
        // 若主题 / 控件样式没进来，任何 {StaticResource Card} 都会在解析时抛异常。
        EnsureApplicationResources();

        Theme = new ThemeService();
        _appliedPerformanceMode = Context.Settings.Appearance.PerformanceMode;
        ApplyPerformanceStyles(_appliedPerformanceMode);
        Theme.Apply(Context.Settings.Appearance);

        // 首次启动（或协议版本更新后）先显示用户协议。拒绝时收尾后直接结束进程，
        // 不能回到 OnStartup 继续空转 —— 没有窗口时进程会一直占着文件锁。
        if (!AgreementWindow.EnsureAccepted(Context))
        {
            try { Context.Shutdown(); } catch { }
            try { GuardClient.Current?.Stop(GuardExitReason.UserClosedWindow); } catch { }
            Environment.Exit(0);
            return;
        }

        var window = new MainWindow(Context);
        System.Windows.Application.Current.MainWindow = window;
        window.Show();

        // 守护进程的心跳改由 UI 线程驱动：界面卡死 → 心跳停 → 守护进程能发现并处理
        if (GuardClient.Current is not null)
        {
            GuardClient.Current.EnableUiHeartbeat();
            _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _heartbeatTimer.Tick += (_, _) => GuardClient.Current?.Pulse();
            _heartbeatTimer.Start();
        }

        if (Context.Settings.Advanced.AutoRefreshAccount)
            _ = Task.Run(() => Context.Accounts.RefreshAllAsync());
    }

    /// <summary>命令行模式下的收尾（OnExit 不会触发）。</summary>
    public static void ShutdownCli()
    {
        try { Context?.Shutdown(); }
        catch { }
        try { GuardClient.Current?.Stop(GuardExitReason.CliFinished); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 关掉所有还开着的窗口。日志窗口这类独立窗口如果不关，
            // 进程可能退不干净，文件一直被占用（表现为删不掉、覆盖失败）
            foreach (var window in System.Windows.Application.Current.Windows.Cast<Window>().ToList())
            {
                try { window.Close(); } catch { }
            }

            Context?.Shutdown();
            _heartbeatTimer?.Stop();
            GuardClient.Current?.Stop(GuardExitReason.UserClosedWindow);
        }
        finally
        {
            base.OnExit(e);
        }
    }

    /// <summary>上次已应用的性能模式，用于判断是否需要重建界面。</summary>
    private static PerformanceMode _appliedPerformanceMode = PerformanceMode.Standard;

    /// <summary>流畅模式样式字典（延迟加载，只加载一次）。</summary>
    private static System.Windows.ResourceDictionary? _smoothStyles;

    /// <summary>设置变更后统一刷新（UI + 服务）。</summary>
    public static void ApplySettings()
    {
        Context.ApplySettings();

        var mode = Context.Settings.Appearance.PerformanceMode;
        var changed = _appliedPerformanceMode != mode;
        if (changed) ApplyPerformanceStyles(mode);
        _appliedPerformanceMode = mode;

        Theme.Apply(Context.Settings.Appearance);

        // 性能模式可能刚变：毛玻璃、背景模糊、动效都要跟着重画
        if (Current.MainWindow is MainWindow window)
        {
            window.ApplyPerformanceMode();
            // 样式字典换了，已经建好的页面要重建才能拿到新样式
            if (changed) window.RebuildForLanguage();
        }
    }

    /// <summary>
    /// 流畅模式：把 Styles/Controls.Smooth.xaml 插到 Controls.xaml 之后。
    /// 后者合并更晚，同名键优先命中，于是带 Storyboard 的样式被不带动画的版本覆盖。
    /// 切回标准模式时移除即可。
    /// </summary>
    public static void ApplyPerformanceStyles(PerformanceMode mode)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        try
        {
            _smoothStyles ??= new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/LPML;component/Styles/Controls.Smooth.xaml", UriKind.Absolute)
            };

            var has = app.Resources.MergedDictionaries.Contains(_smoothStyles);
            var want = mode == PerformanceMode.Smooth;

            if (want && !has)
            {
                // 插到 Controls.xaml 后面，保证覆盖关系正确
                var index = -1;
                for (var i = 0; i < app.Resources.MergedDictionaries.Count; i++)
                {
                    var source = app.Resources.MergedDictionaries[i].Source?.OriginalString ?? "";
                    if (source.Contains("Controls.xaml", StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                }
                app.Resources.MergedDictionaries.Insert(index + 1, _smoothStyles);
            }
            else if (!want && has)
            {
                app.Resources.MergedDictionaries.Remove(_smoothStyles);
            }
        }
        catch (Exception ex)
        {
            // 样式是装饰性的，失败就退回标准样式，绝不能阻断启动
            Log.Warn("App", "Smooth styles unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// 确保主题与控件样式已合并进 Application.Resources。
    /// 已存在则不重复加载；加载失败只记日志，界面退化为无样式但不崩。
    /// </summary>
    public static void EnsureApplicationResources()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        try
        {
            // 已合并过就跳过（避免重复键）
            foreach (var dict in app.Resources.MergedDictionaries)
            {
                if (dict.Source is not null && dict.Source.OriginalString.Contains("Theme.xaml", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            foreach (var relative in new[] { "Themes/Theme.xaml", "Styles/Controls.xaml" })
            {
                try
                {
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/LPML;component/{relative}", UriKind.Absolute)
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("Resources", $"Failed to load {relative}: {ex.Message}", ex);
                }
            }

            Log.Info("Resources", $"Merged dictionaries: {app.Resources.MergedDictionaries.Count}");
        }
        catch (Exception ex)
        {
            Log.Error("Resources", "EnsureApplicationResources failed: " + ex.Message, ex);
        }
    }

}
