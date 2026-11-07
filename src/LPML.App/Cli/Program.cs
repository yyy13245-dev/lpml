using System.IO;
using System.Reflection;
using System.Text;
using LPML.App.Guard;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Config;
using System.Runtime.InteropServices;
using System.Windows;
using LPML.App.Cli;

namespace LPML.App;

/// <summary>
/// 统一入口。
///   --guard            守护进程模式（由主进程启动，不创建任何界面）
///   --cli              命令行模式
///   其它                正常 WPF 界面
/// 三种模式都会在最早处先挂上本地日志文件，再由守护进程接管落盘。
/// </summary>
public static class Program
{
    private static readonly string[] CliSwitches = { "--cli", "--console", "-c", "--nogui" };

    /// <summary>流畅模式：本次与后续启动都生效（写进配置）。</summary>
    private static readonly string[] SmoothSwitches = { "--smooth", "--perf-smooth" };

    /// <summary>标准模式：本次与后续启动都生效（写进配置）。</summary>
    private static readonly string[] StandardSwitches = { "--standard", "--perf-standard" };

    /// <summary>只给守护进程 / 入口用的参数，不要透传给命令行解析器。</summary>
    private static readonly string[] GuardPrefixes =
    {
        "--session=", "--pid=", "--log=", "--timeout=", "--autokill=",
        "--guard-lang=", "--guard-timeout="
    };

    /// <summary>一次性查询：输出留在父 cmd 里，方便复制和重定向。</summary>
    private static readonly string[] OneShotSwitches =
    {
        "--help", "-h", "/?", "--list", "--list-instances", "ls",
        "--list-versions", "--list-accounts", "--list-java"
    };

    private static bool IsOneShot(string[] args) =>
        args.Any(a => OneShotSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 程序版本号，取自 csproj 的 &lt;Version&gt;（程序集信息版本），不硬编码。
    /// 唯一来源为 Core 的 BuildInfo，日志、启动参数与游戏配置文件都从那里读取。
    /// </summary>
    private static string AppVersion() => BuildInfo.Version;

    [STAThread]
    public static int Main(string[] args)
    {
        // 必须放在创建 WPF Application 之前：守护进程不能依赖任何界面资源，
        // 否则界面崩了（如样式解析失败）它也起不来。
        if (args.Any(a => a.Equals("--guard", StringComparison.OrdinalIgnoreCase)))
            return GuardProcess.Run(args);

        // 先挂本地日志并打印版本号，再尽早启动守护进程。
        // 守护进程还没起来的这段日志由主进程自己记录，一条不丢；
        // --no-guard 时也不会只留在内存缓冲里而没落盘。
        var paths = new AppPaths();
        Log.Service.AttachFile(paths.LauncherLogFile);
        Log.Info("App", $"LPML {AppVersion()} 启动");
        var options = GuardOptions.Load(paths.SettingsFile, args);
        var exe = ExecutablePath();
        if (!string.IsNullOrEmpty(exe))
            GuardClient.Start(paths.LauncherLogFile, options, exe);

        var isCli = args.Any(a => CliSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));

        // 性能模式参数：两种模式下都认，且会持久化
        if (args.Any(a => SmoothSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)))
            App.PendingPerformanceMode = PerformanceMode.Smooth;
        else if (args.Any(a => StandardSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)))
            App.PendingPerformanceMode = PerformanceMode.Standard;

        if (!isCli) return RunGui(args);

        // 交互模式独占一个控制台窗口：stdin 不会被父 cmd 抢走，输入行才真的可用。
        // 一次性查询（--list / --help 等）仍挂到父 cmd，输出可以复制、重定向。
        EnsureConsole(own: !IsOneShot(args));

        // 控制台模式下也会走 WPF 类型（语言包是 ResourceDictionary），
        // 需要一个 Application 实例来承载资源，但不创建任何窗口。
        var app = new App();
        app.InitializeComponent();
        App.Initialize(withUi: false);

        // 仅日志模式下 Ctrl+C 用于结束转发（REPL 自己接管了 Ctrl+C，不会走到这里）
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine();
            Console.WriteLine(Lang.T("String.Cli.Interrupted"));
            GuardClient.Current?.Stop(GuardExitReason.CliInterrupted);
            // Environment.Exit 不会走 finally，这里必须自己收尾，别把守护进程留在后台
            try { App.Context.Shutdown(); } catch { }
            RestoreConsole();
            Environment.Exit(130);
        };

        try
        {
            // 命令行模式第一次使用同样要先同意用户协议。
            // 拒绝时仍走下面的 finally：收尾守护进程、还原控制台代码页，不留后台进程。
            if (!CliAgreement.EnsureAccepted(App.Context)) return CliAgreement.DeclinedExitCode;

            var runner = new CliRunner(App.Context);
            return runner.RunAsync(FilterCliSwitch(args), CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            // ShutdownCli 里已经带原因调过 Stop，这里不再重复（第二次会被 _stopped 挡掉）
            App.ShutdownCli();
            RestoreConsole();
        }
    }

    private static int RunGui(string[] args)
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }

    /// <summary>进程自身的可执行文件路径。用于以子进程方式再启动一份 --guard。</summary>
    private static string ExecutablePath()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;

            var loc = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(loc)) return "";
            if (loc.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                loc = Path.ChangeExtension(loc, ".exe");
            return File.Exists(loc) ? loc : "";
        }
        catch { return ""; }
    }

    /// <summary>去掉模式开关本身，剩下的是真正的功能参数。</summary>
    private static string[] FilterCliSwitch(string[] args) =>
        args.Where(a => !CliSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)
                        && !SmoothSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)
                        && !StandardSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)
                        && !GuardOptions.DisableSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)
                        && !a.Equals("--guard-auto-kill", StringComparison.OrdinalIgnoreCase)
                        && !GuardPrefixes.Any(p => a.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToArray();

    // ── 控制台分配与编码 ───────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetConsoleTitleW(string title);

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint codePage);

    private const int AttachParentProcess = -1;
    private const uint Utf8CodePage = 65001;

    private static bool _ownConsole;
    private static bool _cpChanged;
    private static uint _savedOutputCp;
    private static uint _savedInputCp;

    /// <summary>
    /// own=true 时自己开一个控制台窗口（交互模式独占 stdin）；
    /// own=false 时挂到父 cmd（一次性查询，输出可复制/重定向）。
    ///
    /// 乱码的原因：StreamWriter 默认 UTF-8 无 BOM，而 cmd 按系统 OEM 代码页
    /// （简体 936 / 繁体 950 / 英文 437）解读字节，中文就整篇变乱码。
    /// 这里把控制台代码页统一设成 65001 并用 UTF-8 写，两者对齐。
    /// 挂到父 cmd 时记下原代码页，退出时还原，不给用户的 cmd 留副作用。
    /// </summary>
    private static void EnsureConsole(bool own)
    {
        if (!OperatingSystem.IsWindows()) return;

        if (own)
        {
            if (!AllocConsole())
            {
                if (!AttachConsole(AttachParentProcess)) AllocConsole();
                own = false;
            }
        }
        else if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
            own = true;
        }

        _ownConsole = own;
        if (own)
        {
            try { SetConsoleTitleW("LPML 命令行"); } catch { }
        }

        try
        {
            if (!own)
            {
                _savedOutputCp = GetConsoleOutputCP();
                _savedInputCp = GetConsoleCP();
            }
            _cpChanged = SetConsoleOutputCP(Utf8CodePage);
            SetConsoleCP(Utf8CodePage);

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
        }
        catch (IOException) { }
    }

    /// <summary>退出时还原父 cmd 的代码页（自己开的窗口随进程退出自动关闭）。</summary>
    private static void RestoreConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (_cpChanged && !_ownConsole)
            {
                SetConsoleOutputCP(_savedOutputCp);
                SetConsoleCP(_savedInputCp);
            }
        }
        catch { }
    }
}
