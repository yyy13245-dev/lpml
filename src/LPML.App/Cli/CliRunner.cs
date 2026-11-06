using System.IO;
using LPML.App.Services;
using LPML.Core;
using LPML.Core.Auth;
using LPML.Core.Config;
using LPML.Core.Download;
using LPML.Core.Instances;
using LPML.Core.Java;
using LPML.Core.Launch;
using System.Globalization;

namespace LPML.App.Cli;

/// <summary>命令行的输出形态。</summary>
public enum CliMode
{
    /// <summary>交互式 REPL（默认）：上区滚日志、底部固定输入行。</summary>
    Repl = 0,

    /// <summary>仅日志：日志刷屏，不接受输入（Ctrl+C 退出）。</summary>
    LogOnly = 1,

    /// <summary>仅输入：日志只写文件，控制台只剩输入行。</summary>
    InputOnly = 2
}

/// <summary>
/// 命令行模式：不创建任何窗口，日志直接打到控制台，
/// 启动 MC 后把游戏进程的 stdout/stderr 实时转发到控制台。
/// </summary>
public sealed partial class CliRunner
{
    private readonly LauncherContext _ctx;

    /// <summary>当前游戏进程（供命令操作）。</summary>
    private GameProcess? _process;
    private GameInstance? _instance;
    private DateTimeOffset _startedAt;
    private string _javaSummary = "";

    public CliRunner(LauncherContext context) => _ctx = context;

    /// <summary>返回进程退出码。</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?")) { PrintHelp(); return 0; }

        // 输出形态：默认 REPL，可用 --log-only / --input-only 切换
        if (!TryParseMode(args, out var mode)) return 1;

        // 没给任何参数：不要直接打印一句"没有实例"就退出，
        // 而是进入启动器 shell，让用户能 ls / launch / quit 交互操作。
        if (args.Length == 0)
            return RunShellAsync(token);

        var instanceKey = Value(args, "--instance", "-i");
        var versionId = Value(args, "--version", "-v");
        var accountKey = Value(args, "--account", "-a");
        var archText = Value(args, "--arch");
        var afterText = Value(args, "--after");
        var nonInteractive = args.Any(a => a is "--yes" or "-y");

        if (args.Any(a => a is "--list" or "--list-instances" or "ls")) { ListInstances(); return 0; }
        if (args.Any(a => a is "--list-versions")) { ListVersions(); return 0; }
        if (args.Any(a => a is "--list-accounts")) { ListAccounts(); return 0; }
        if (args.Any(a => a is "--list-java")) { ListJavas(); return 0; }

        // 1) 选定实例
        var instance = ResolveInstance(instanceKey, versionId);
        if (instance is null)
        {
            _output(Lang.T("String.Cli.NoInstance"));
            _output(Lang.T("String.Cli.UseListHint"));
            return 2;
        }

        // 2) 选定账户
        var account = ResolveAccount(accountKey);
        if (account is null)
        {
            _output(Lang.T("String.Cli.NoAccount"));
            return 3;
        }

        // 3) 启动后行为（命令行下 keep/hide 等价，仅 exit 有意义）
        var after = ParseAfterLaunch(afterText, instance);

        _output(Lang.F("String.Cli.Launching", instance.Name, instance.VersionId));

        // 4) 确保有可用 Java；没有就让用户选架构下载
        var javaReady = await EnsureJavaAsync(instance, archText, nonInteractive, token).ConfigureAwait(false);
        if (!javaReady) return 4;

        // 5) 启动
        var result = await LaunchAsync(instance, account, token).ConfigureAwait(false);
        if (!result.Success)
        {
            _output(Lang.F("String.Cli.LaunchFailed", result.ErrorMessage ?? ""));
            if (result.FailedJavaPaths.Count > 0)
                _output(Lang.F("String.Cli.TriedJavas", string.Join(", ", result.FailedJavaPaths)));
            return 5;
        }

        if (result.UsedJava is not null)
            _javaSummary = result.UsedJava is not null
            ? $"{result.UsedJava.TypeText} {result.UsedJava.MajorVersion} · {result.UsedJava.Vendor}"
            : Lang.T("String.Msg.Common.Unknown");
        _instance = instance;
        _startedAt = DateTimeOffset.Now;

        if (result.JavaRetryCount > 0)
            _output(Lang.F("String.Cli.Retried", result.JavaRetryCount));

        // 6) 按模式分发
        var process = _ctx.Launcher.RunningProcess;
        if (process is null) return 0;
        _process = process;

        var header = Lang.F("String.Cli.ReplHeader", instance.Name, result.ProcessId, _javaSummary);
        var hint = Lang.T("String.Cli.ReplHint");

        switch (mode)
        {
            case CliMode.InputOnly:
                return await RunInputOnlyAsync(process, token).ConfigureAwait(false);

            case CliMode.LogOnly:
                return await RunLogOnlyAsync(process, result, token).ConfigureAwait(false);

            default:
                return await RunReplAsync(process, result, header, hint, token).ConfigureAwait(false);
        }
    }

    /// <summary>解析输出形态；两个参数同时给出时报错。</summary>
    private static bool TryParseMode(string[] args, out CliMode mode)
    {
        var logOnly = args.Any(a => a is "--log-only" or "--log");
        var inputOnly = args.Any(a => a is "--input-only" or "--input");
        mode = CliMode.Repl;

        if (logOnly && inputOnly)
        {
            Console.Error.WriteLine(Lang.T("String.Cli.ModeConflict"));
            return false;
        }

        if (logOnly) mode = CliMode.LogOnly;
        else if (inputOnly) mode = CliMode.InputOnly;
        return true;
    }

    // ── 模式一：仅日志（刷屏转发，不接受输入） ──────────────

    private async Task<int> RunLogOnlyAsync(GameProcess process, LaunchResult result, CancellationToken token)
    {
        Console.WriteLine(Lang.F("String.Cli.GameStarted", result.ProcessId));
        Console.WriteLine(Lang.T("String.Cli.LogOnlyHint"));
        Console.WriteLine(new string('-', 60));

        process.OutputReceived += (_, line) => Console.WriteLine(line);

        var exitCode = await WaitForExitAsync(process, token).ConfigureAwait(false);

        Console.WriteLine(new string('-', 60));
        Console.WriteLine(Lang.F("String.Cli.GameExited", exitCode));
        return exitCode;
    }

    // ── 模式二：仅输入（日志写文件，控制台只剩输入行） ───────

    private async Task<int> RunInputOnlyAsync(GameProcess process, CancellationToken token)
    {
        Console.WriteLine(Lang.T("String.Cli.InputOnlyHint"));
        Console.WriteLine(Lang.F("String.Cli.GameStarted", process.ProcessId ?? 0));

        // 日志进文件，不刷屏
        var logFile = OpenConsoleLogFile();
        process.OutputReceived += (_, line) => WriteToLogFile(logFile, line);

        // 后台等待退出
        var exitTask = WaitForExitAsync(process, token);

        // 前台简单输入循环（无日志干扰，用 ReadLine 即可）
        while (process.IsRunning)
        {
            Console.Write("> ");
            string? line;
            try { line = Console.ReadLine(); }
            catch (IOException) { break; }
            if (line is null) break;
            if (HandleCommand(line.Trim())) break;
        }

        var exitCode = await exitTask.ConfigureAwait(false);
        Console.WriteLine(Lang.F("String.Cli.GameExited", exitCode));
        return exitCode;
    }

    // ── 模式三：交互式 REPL（默认） ─────────────────────────

    private async Task<int> RunReplAsync(GameProcess process, LaunchResult result,
        string header, string hint, CancellationToken token)
    {
        // 输出被重定向（如 > log.txt）时无法交互，自动退化为纯日志
        if (!ReplConsole.CanInteractive)
        {
            Console.Error.WriteLine(Lang.T("String.Cli.RedirectedFallback"));
            return await RunLogOnlyAsync(process, result, token).ConfigureAwait(false);
        }

        var logFile = OpenConsoleLogFile();
        var repl = new ReplConsole(header, hint);

        // 命令回显也走日志区，避免直接写控制台破坏分区
        _output = repl.AppendLog;
        _clearScreen = repl.ClearLog;

        // 游戏日志 → REPL 上半区 + 落盘
        process.OutputReceived += (_, line) =>
        {
            repl.AppendLog(line);
            WriteToLogFile(logFile, line);
        };

        // 命令回调：返回 true 结束 REPL
        repl.OnCommand = cmd => HandleCommand(cmd);

        // 游戏自己退出 → 结束输入循环
        var finished = false;
        process.Exited += (_, code) =>
        {
            lock (_exitLock)
            {
                if (finished) return;
                finished = true;
            }
            repl.AppendLog(Lang.F("String.Cli.GameExited", code));
            repl.SetHint(Lang.T("String.Cli.ReplGameOver"));
            _gameExitCode = code;
            // 给一点时间让用户看到最后几行，然后结束
            Task.Delay(1500).ContinueWith(_ => repl.Stop());
        };

        // 后台等待退出，前台跑输入循环
        var exitTask = WaitForExitAsync(process, token);
        repl.Run();

        var exitCode = _gameExitCode ?? await exitTask.ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(Lang.F("String.Cli.GameExited", exitCode));
        return exitCode;
    }

    private readonly object _exitLock = new();
    private int? _gameExitCode;

    // ── 启动器 shell（游戏尚未启动时的交互界面） ───────────
    private bool _shellMode;
    private ReplConsole? _shellRepl;
    private CancellationToken _shellToken;

    /// <summary>
    /// 命令回显通道。REPL 模式下指向日志缓冲（避免直接写控制台破坏分区布局），
    /// 其余模式直接写控制台。
    /// </summary>
    private Action<string> _output = Console.WriteLine;

    /// <summary>清屏动作。REPL 下清的是日志缓冲，不是整个屏幕。</summary>
    private Action _clearScreen = () => { try { Console.Clear(); } catch (IOException) { } };

    // ── 选择 ───────────────────────────────────────────────

    private GameInstance? ResolveInstance(string? key, string? versionId)
    {
        var list = _ctx.Instances.Instances;
        if (list.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(versionId))
            return list.FirstOrDefault(i => string.Equals(i.VersionId, versionId, StringComparison.OrdinalIgnoreCase))
                   ?? new GameInstance { Name = versionId!, VersionId = versionId! };

        if (string.IsNullOrWhiteSpace(key))
            return list.OrderByDescending(i => i.LastPlayed).FirstOrDefault();

        var k = key!;
        return list.FirstOrDefault(i => string.Equals(i.Name, k, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(i => string.Equals(i.Id, k, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(i => i.Name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private Account? ResolveAccount(string? key)
    {
        var accounts = _ctx.Accounts.Accounts;
        if (accounts.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(key))
            return accounts.FirstOrDefault(a => a.IsValid) ?? accounts.FirstOrDefault();

        var k = key!;
        return accounts.FirstOrDefault(a => string.Equals(a.UserName, k, StringComparison.OrdinalIgnoreCase))
               ?? accounts.FirstOrDefault(a => a.UserName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static AfterLaunchAction ParseAfterLaunch(string? text, GameInstance instance)
    {
        if (instance.AfterLaunchOverride.HasValue)
            return (AfterLaunchAction)Math.Clamp(instance.AfterLaunchOverride.Value, 0, 2);

        return (text ?? "").Trim().ToLowerInvariant() switch
        {
            "exit" or "close" or "quit" => AfterLaunchAction.Exit,
            "hide" or "minimize" => AfterLaunchAction.Hide,
            "keep" or "none" => AfterLaunchAction.KeepOpen,
            _ => AfterLaunchAction.KeepOpen
        };
    }

    // ── Java 准备：让用户选架构 ─────────────────────────────

    /// <summary>
    /// 若列表里已有可用 Java 则直接返回 true；否则列出可选架构让用户挑，
    /// 下载后加入列表。绝不硬编码架构。
    /// </summary>
    private async Task<bool> EnsureJavaAsync(GameInstance instance, string? archText,
        bool nonInteractive, CancellationToken token)
    {
        var required = JavaService.RequiredMajorByGameId(instance.VersionId);
        var pool = _ctx.Javas.Javas.Where(j => j.IsValid && File.Exists(j.Path)).ToList();
        var picked = JavaManager.Recommend(required, pool, _ctx.Settings.Java.RuntimePreference);

        if (picked is not null)
        {
            _output(Lang.F("String.Cli.JavaReady", picked.TypeText, picked.MajorVersion, picked.Vendor));
            return true;
        }

        _output(Lang.F("String.Cli.NoJavaFound", required));

        if (!_ctx.Settings.Java.AutoDownloadJavaRuntime && string.IsNullOrWhiteSpace(archText))
        {
            _output(Lang.T("String.Cli.AutoDownloadOff"));
            return false;
        }

        // 拉取远程可用的架构
        var options = await _ctx.Java.RuntimeArchOptionsAsync(token).ConfigureAwait(false);
        if (options.Count == 0)
        {
            _output(Lang.T("String.Cli.NoArchAvailable"));
            return false;
        }

        JavaArchOption chosen;
        if (!string.IsNullOrWhiteSpace(archText))
        {
            chosen = options.FirstOrDefault(o =>
                         o.Name.Contains(archText!, StringComparison.OrdinalIgnoreCase) ||
                         o.PlatformKey.Contains(archText!, StringComparison.OrdinalIgnoreCase))
                     ?? throw new IOException(Lang.F("String.Cli.ArchNotAvailable", archText!));
        }
        else if (nonInteractive)
        {
            chosen = options.FirstOrDefault(o => o.IsSuggested) ?? options[0];
            _output(Lang.F("String.Cli.ArchAuto", chosen.Name));
        }
        else
        {
            chosen = PromptArch(options);
        }

        _output(Lang.F("String.Cli.DownloadingArch", chosen.Name));
        return await DownloadRuntimeAsync(chosen, token).ConfigureAwait(false);
    }

    private JavaArchOption PromptArch(List<JavaArchOption> options)
    {
        Console.WriteLine();
        Console.WriteLine(Lang.T("String.Cli.ArchPrompt"));
        for (var i = 0; i < options.Count; i++)
            Console.WriteLine($"  [{i + 1}] {options[i].Display}");

        while (true)
        {
            Console.Write(Lang.F("String.Cli.ArchInput", options.Count) + " ");
            var input = Console.ReadLine()?.Trim();
            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                n >= 1 && n <= options.Count)
                return options[n - 1];

            // 也允许直接输关键字，如 arm64 / x64
            var match = options.FirstOrDefault(o =>
                o.Name.Contains(input ?? "", StringComparison.OrdinalIgnoreCase) ||
                o.PlatformKey.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            Console.WriteLine(Lang.T("String.Cli.ArchInvalid"));
        }
    }

    private async Task<bool> DownloadRuntimeAsync(JavaArchOption option, CancellationToken token)
    {
        var component = "java-runtime-gamma";
        try
        {
            var items = await _ctx.Java.BuildRuntimeDownloadItemsAsync(component, option.Arch, token)
                .ConfigureAwait(false);
            if (items.Count == 0)
            {
                _output(Lang.T("String.Cli.RuntimeEmpty"));
                return false;
            }

            _output(Lang.F("String.Cli.RuntimeFiles", items.Count));

            var runtimeTask = _ctx.Downloads.BeginTask(component, "game");
            _ctx.Downloads.Enqueue(items, runtimeTask);
            _ctx.Downloads.ProgressChanged += (_, agg) =>
                _output(Lang.F("String.Cli.RuntimeProgress", agg.Completed, agg.Total));
            await _ctx.Downloads.RunAsync(token).ConfigureAwait(false);
            var failed = items.Count(i => i.Status == DownloadStatus.Failed);
            if (failed > 0)
            {
                _output(Lang.F("String.Cli.RuntimeFailed", failed));
                return false;
            }

            var exe = _ctx.Java.FindRuntimeJava(component, option.Arch);
            if (exe is null)
            {
                _output(Lang.T("String.Cli.RuntimeNotFound"));
                return false;
            }

            _ctx.Javas.Add(exe);
            _output(Lang.F("String.Cli.RuntimeReady", exe));
            return true;
        }
        catch (Exception ex)
        {
            _output(Lang.F("String.Cli.RuntimeError", ex.Message));
            return false;
        }
    }

    // ── 启动与等待 ─────────────────────────────────────────

    private Task<LaunchResult> LaunchAsync(GameInstance instance, Account account, CancellationToken token)
    {
        var service = new GameLaunchService(_ctx) { Headless = true, AccountOverride = account };
        service.StatusChanged += (_, status) => _output("[LPML] " + status);
        return service.LaunchAsync(instance, false, token);
    }

    private static async Task<int> WaitForExitAsync(GameProcess process, CancellationToken token)
    {
        try
        {
            return await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return -1;
        }
    }

    // ── 列表 ───────────────────────────────────────────────

    private void ListInstances()
    {
        var list = _ctx.Instances.Instances;
        if (list.Count == 0) { _output(Lang.T("String.Cli.EmptyInstances")); return; }

        _output(Lang.F("String.Cli.InstanceCount", list.Count));
        foreach (var i in list)
            _output($"  {i.Name,-28} {i.VersionId,-16} {(i.LoaderName ?? "vanilla")}");
    }

    private void ListVersions()
    {
        var dir = _ctx.Paths.VersionsDirectory;
        List<string> versions = new();
        if (Directory.Exists(dir))
        {
            versions = Directory.EnumerateDirectories(dir)
                .Select(d => Path.GetFileName(d) ?? "")
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (versions.Count == 0) { _output(Lang.T("String.Cli.EmptyVersions")); return; }

        _output(Lang.F("String.Cli.VersionCount", versions.Count));
        foreach (var v in versions) _output("  " + v);
    }

    private void ListAccounts()
    {
        var accounts = _ctx.Accounts.Accounts;
        if (accounts.Count == 0) { _output(Lang.T("String.Cli.EmptyAccounts")); return; }

        _output(Lang.F("String.Cli.AccountCount", accounts.Count));
        foreach (var a in accounts)
            _output($"  {a.UserName,-24} {a.TypeName}");
    }

    private void ListJavas()
    {
        var javas = _ctx.Javas.Javas;
        if (javas.Count == 0) { _output(Lang.T("String.Cli.EmptyJavas")); return; }

        _output(Lang.F("String.Cli.JavaCount", javas.Count));
        foreach (var j in javas)
            _output($"  {j.TypeText,-4} {j.MajorVersion,-3} {j.Vendor,-22} {j.Path}");
    }

    // ── 命令处理（REPL 与仅输入模式共用） ──────────────────

    /// <summary>处理一条命令。返回 true 表示要结束循环。</summary>
    private bool HandleCommand(string raw)
    {
        var cmd = (raw ?? "").Trim();
        if (cmd.Length == 0) return false;

        var parts = cmd.Split(' ', 2);
        var head = parts[0].ToLowerInvariant();
        var tail = parts.Length > 1 ? parts[1] : "";

        // 同一套命令表在游戏启动前后都可用；完整列表只在 help 里，
        // 底部常驻提示不再罗列命令。
        if (HandleCommonCommand(head, tail, out var shouldExit)) return shouldExit;

        // 游戏态的裸 java 仍是「显示本次使用的 Java」
        if (!_shellMode && head == "java")
        {
            _output(Lang.F("String.Cli.CmdJava", _javaSummary));
            return false;
        }

        if (_shellMode)
        {
            _output(Lang.F("String.Cli.ShellUnknown", head));
            return false;
        }

        // 其余转发给游戏进程（服务端有效，客户端通常忽略）
        return ForwardToGame(cmd);
    }

    private bool ForwardToGame(string command)
    {
        if (_process is null || !_process.CanSendInput)
        {
            _output(Lang.T("String.Cli.CmdNoStdin"));
            return false;
        }
        return !_process.SendInput(command);
    }

    private void StopGame()
    {
        if (_process is null) return;

        // 先尝试优雅停止（服务端支持 stop 命令）
        if (_process.CanSendInput) _process.SendInput("stop");

        // 给它一点时间自己退出
        for (var i = 0; i < 30 && _process.IsRunning; i++)
            Thread.Sleep(100);

        if (_process.IsRunning) _process.Kill();
    }

    private void PrintStatus()
    {
        if (_instance is null) return;
        var uptime = DateTimeOffset.Now - _startedAt;
        _output(Lang.F("String.Cli.CmdStatus",
            _instance.Name,
            _instance.VersionId,
            _process?.ProcessId?.ToString() ?? "-",
            (int)uptime.TotalMinutes,
            uptime.Seconds,
            _process?.IsRunning == true ? Lang.T("String.Cli.Running") : Lang.T("String.Cli.NotRunning")));
    }

    /// <summary>完整命令表见 CliRunner.Commands.cs 的 PrintFullHelp。</summary>
    private void PrintReplHelp() => PrintFullHelp();

    // ── 启动器 shell：游戏启动前的交互界面 ─────────────────

    /// <summary>
    /// 不带参数运行 --cli 时进入这里：底部固定输入行，可 ls / launch / quit。
    /// 这样即使还没有实例、或者不想记命令行参数，也有一个可用的输入框。
    /// </summary>
    private int RunShellAsync(CancellationToken token)
    {
        _shellToken = token;
        _shellMode = true;

        // 输出被重定向时画不出分区，退化成普通逐行输入
        if (!ReplConsole.CanInteractive)
            return RunPlainShell(token);

        var repl = new ReplConsole(
            Lang.T("String.Cli.ShellHeader"),
            Lang.T("String.Cli.ShellHint"));

        _shellRepl = repl;
        _output = repl.AppendLog;
        _clearScreen = repl.ClearLog;

        // 启动器自身日志（Guard、Java 扫描等）也投进日志区，
        // 否则直接 Console.WriteLine 会把 REPL 的上下分区冲掉。
        var prevHook = LPML.App.App.CliLogHook;
        LPML.App.App.CliLogHook = e => repl.AppendLog(e.ToString());

        repl.OnCommand = cmd => HandleCommand(cmd);

        try
        {
            repl.Run();
        }
        finally
        {
            LPML.App.App.CliLogHook = prevHook;
            _output = Console.WriteLine;
            _clearScreen = () => { try { Console.Clear(); } catch (IOException) { } };
            _shellRepl = null;
            _shellMode = false;
        }
        return _gameExitCode ?? 0;
    }

    private int RunPlainShell(CancellationToken token)
    {
        Console.WriteLine(Lang.T("String.Cli.ShellHeader"));
        Console.WriteLine(Lang.T("String.Cli.ShellHint"));
        while (!token.IsCancellationRequested)
        {
            Console.Write("> ");
            string? line;
            try { line = Console.ReadLine(); }
            catch (IOException) { break; }
            if (line is null) break;
            if (HandleCommand(line.Trim())) break;
        }
        return 0;
    }

    /// <summary>兼容旧调用：统一走完整命令表。</summary>
    private bool HandleShellCommand(string head, string tail)
    {
        if (HandleCommonCommand(head, tail, out var exit)) return exit;
        _output(Lang.F("String.Cli.ShellUnknown", head));
        return false;
    }

    /// <summary>完整命令表见 CliRunner.Commands.cs 的 PrintFullHelp。</summary>
    private void PrintShellHelp() => PrintFullHelp();

    /// <summary>
    /// 在 shell 里启动游戏。复用 RunAsync 的同一套选择/启动逻辑，
    /// 成功后不另开 REPL，而是复用当前这个，只把命令集切成游戏态。
    /// </summary>
    private void LaunchFromShell(string tail)
    {
        var repl = _shellRepl;
        if (repl is null) return;

        // launch [实例名] [-a 账户] [-v 版本]
        var toks = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var instanceKey = toks.Length > 0 && !toks[0].StartsWith('-') ? toks[0] : null;
        var versionId = Value(toks, "-v", "--version");
        var accountKey = Value(toks, "-a", "--account");

        var instance = ResolveInstance(instanceKey, versionId);
        if (instance is null)
        {
            _output(Lang.T("String.Cli.NoInstance"));
            _output(Lang.T("String.Cli.UseListHint"));
            return;
        }

        var account = ResolveAccount(accountKey);
        if (account is null) { _output(Lang.T("String.Cli.NoAccount")); return; }

        _output(Lang.F("String.Cli.Launching", instance.Name, instance.VersionId));

        try
        {
            // shell 里不弹 Java 架构选择（会破坏输入行），缺运行时按推荐项自动处理
            if (!EnsureJavaAsync(instance, null, true, _shellToken).GetAwaiter().GetResult()) return;

            var result = LaunchAsync(instance, account, _shellToken).GetAwaiter().GetResult();
            if (!result.Success)
            {
                _output(Lang.F("String.Cli.LaunchFailed", result.ErrorMessage ?? ""));
                if (result.FailedJavaPaths.Count > 0)
                    _output(Lang.F("String.Cli.TriedJavas", string.Join(", ", result.FailedJavaPaths)));
                return;
            }

            _instance = instance;
            _startedAt = DateTimeOffset.Now;
            _javaSummary = result.UsedJava is not null
                ? $"{result.UsedJava.TypeText} {result.UsedJava.MajorVersion} · {result.UsedJava.Vendor}"
                : Lang.T("String.Msg.Common.Unknown");
            if (result.JavaRetryCount > 0)
                _output(Lang.F("String.Cli.Retried", result.JavaRetryCount));

            var process = _ctx.Launcher.RunningProcess;
            if (process is null) return;
            _process = process;

            var logFile = OpenConsoleLogFile();
            process.OutputReceived += (_, line) =>
            {
                repl.AppendLog(line);
                WriteToLogFile(logFile, line);
            };

            var finished = false;
            process.Exited += (_, code) =>
            {
                lock (_exitLock)
                {
                    if (finished) return;
                    finished = true;
                }
                repl.AppendLog(Lang.F("String.Cli.GameExited", code));
                repl.SetHint(Lang.T("String.Cli.ReplGameOver"));
                _gameExitCode = code;
                Task.Delay(1500).ContinueWith(_ => repl.Stop());
            };

            // 切到游戏命令集，同一个输入行继续可用
            _shellMode = false;
            repl.SetHeader(Lang.F("String.Cli.ReplHeader", instance.Name, result.ProcessId, _javaSummary));
            repl.SetHint(Lang.T("String.Cli.ReplHint"));
            _output(Lang.F("String.Cli.GameStarted", result.ProcessId));
        }
        catch (Exception ex)
        {
            _output(Lang.F("String.Cli.LaunchFailed", ex.Message));
        }
    }

    // ── 日志落盘 ───────────────────────────────────────────

    private StreamWriter? _consoleLog;

    private StreamWriter? OpenConsoleLogFile()
    {
        try
        {
            var dir = Path.Combine(_ctx.Paths.LogDirectory, "cli");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"mc-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _consoleLog = new StreamWriter(
                new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read),
                FileLogSink.LogEncoding)
            {
                AutoFlush = true
            };
            return _consoleLog;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteToLogFile(StreamWriter? writer, string line)
    {
        try { writer?.WriteLine(line); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // ── 参数 ───────────────────────────────────────────────

    private static string? Value(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"LPML — " + Lang.T("String.Cli.HelpTitle"));
        Console.WriteLine();
        Console.WriteLine($@"  {Lang.T("String.Cli.HelpUsage")}
    LPML.exe --cli [选项]

  {Lang.T("String.Cli.HelpOptions")}
    --instance, -i <名称/ID>   {Lang.T("String.Cli.HelpInstance")}
    --version,  -v <版本ID>    {Lang.T("String.Cli.HelpVersion")}
    --account,  -a <用户名>    {Lang.T("String.Cli.HelpAccount")}
    --arch <架构>              {Lang.T("String.Cli.HelpArch")}
    --log-only, --log          {Lang.T("String.Cli.HelpLogOnly")}
    --input-only, --input      {Lang.T("String.Cli.HelpInputOnly")}
    --after <keep|hide|exit>   {Lang.T("String.Cli.HelpAfter")}
    --yes,      -y             {Lang.T("String.Cli.HelpYes")}

  {Lang.T("String.Cli.HelpQueries")}
    --list, ls                 {Lang.T("String.Cli.HelpList")}
    --list-versions            {Lang.T("String.Cli.HelpListVersions")}
    --list-accounts            {Lang.T("String.Cli.HelpListAccounts")}
    --list-java                {Lang.T("String.Cli.HelpListJava")}
    --help, -h                 {Lang.T("String.Cli.HelpHelp")}

  {Lang.T("String.Cli.HelpExamples")}
    LPML.exe --cli --list
    LPML.exe --cli -i 我的生存服 -a Steve          # 默认 REPL
    LPML.exe --cli -i 服务端 --log-only            # 只要日志
    LPML.exe --cli -i 服务端 --input-only          # 只要输入
    LPML.exe --cli -v 1.21.4 --arch arm64 --yes");
        Console.WriteLine();
        Console.WriteLine(Lang.T("String.Cli.HelpMore"));
    }
}
