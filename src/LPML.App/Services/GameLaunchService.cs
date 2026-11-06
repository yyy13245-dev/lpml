using LPML.App.Controls;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Auth;
using LPML.Core.Download;
using LPML.Core.Instances;
using LPML.Core.Launch;
using LPML.Core.Utils;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Threading.Tasks;

namespace LPML.App.Services;

/// <summary>
/// 统一的游戏启动编排：补全文件 → 应用隔离 → 启动进程 → 挂日志监听（可选日志窗口）
/// → 端口开放提示 → 崩溃提示。
/// </summary>
public sealed class GameLaunchService
{
    private readonly LauncherContext _ctx;
    private GameLogWindow? _logWindow;
    private GameLogMonitor? _monitor;
    private StreamWriter? _gameLogWriter;

    // 异常退出排查用的尾部缓冲：只在内存里留最近 20 行，不依赖日志窗口或隐私开关
    private readonly object _tailSync = new();
    private readonly Queue<string> _tail = new();
    private const int TailCapacity = 20;
    private const int CrashTailCount = 5;

    public GameLaunchService(LauncherContext ctx)
    {
        _ctx = ctx;
    }

    private void AppendTail(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_tailSync)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailCapacity) _tail.Dequeue();
        }
    }

    private List<string> SnapshotTail(int count)
    {
        lock (_tailSync) return _tail.TakeLast(count).ToList();
    }

    /// <summary>当前日志窗口（若为 null 表示未开启）。</summary>
    public GameLogWindow? LogWindow => _logWindow;

    /// <summary>状态变化（供界面显示）。</summary>
    public event EventHandler<string>? StatusChanged;

    // ── 文件完整性 ──────────────────────────────────────────

    /// <summary>列出缺失文件（不下载）。</summary>
    public List<string> FindMissing(GameInstance instance)
        => _ctx.Launcher.FindMissingFiles(instance.VersionId);

    /// <summary>
    /// 补全游戏文件：把该版本需要的全部联网文件（jar / 依赖库 / 资源索引与资源）
    /// 一次性下载完整，避免首次进游戏还要再下。已存在且校验通过的文件自动跳过。
    /// </summary>
    public async Task<int> CompleteFilesAsync(GameInstance instance,
        IProgress<DownloadAggregate>? progress = null, CancellationToken token = default,
        string? targetDirectory = null, LPML.Core.Download.DownloadTask? task = null)
    {
        // 指定了目标目录就临时切换，让版本 / 库 / 资源都落到那个文件夹里
        var previous = _ctx.Paths.BeginGameDirectory(targetDirectory);
        try
        {
            return await CompleteFilesCoreAsync(instance, progress, token, task).ConfigureAwait(false);
        }
        finally
        {
            _ctx.Paths.EndGameDirectory(previous);
        }
    }

    private async Task<int> CompleteFilesCoreAsync(GameInstance instance,
        IProgress<DownloadAggregate>? progress, CancellationToken token,
        LPML.Core.Download.DownloadTask? task = null)
    {
        var items = await _ctx.Launcher.BuildRepairItemsAsync(instance.VersionId, token).ConfigureAwait(false);
        if (items.Count == 0) return 0;

        EventHandler<DownloadAggregate>? handler = null;
        if (progress is not null)
        {
            handler = (_, agg) => progress.Report(agg);
            _ctx.Downloads.ProgressChanged += handler;
        }

        try
        {
            // 不清空全局队列：启动补全可能和别的安装同时进行
            if (task is null) _ctx.Downloads.Enqueue(items);
            else _ctx.Downloads.Enqueue(items, task);
            await _ctx.Downloads.RunAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (handler is not null) _ctx.Downloads.ProgressChanged -= handler;
        }
        return items.Count;
    }

    // ── 启动 ────────────────────────────────────────────────

    /// <summary>
    /// 启动实例。
    /// </summary>
    /// <param name="debugMode">true = 调试模式（附带日志窗口）；关闭日志监听后自动退化为普通启动。</param>
    /// <summary>
    /// 无界面模式（命令行）。为 true 时不创建日志窗口、不弹 MessageBox、
    /// 不往 Dispatcher 排队（CLI 下 Dispatcher 不运行，排队会无限堆积）。
    /// 游戏日志由命令行侧直接读进程输出转发。
    /// </summary>
    public bool Headless { get; set; }

    /// <summary>
    /// 找出实例缺失的关键游戏文件（版本 JSON / 客户端 jar）。
    /// 返回空表示文件齐全；否则返回"缺失项的人话描述"。
    /// </summary>
    private List<string> FindMissingGameFiles(GameInstance instance)
    {
        var missing = new List<string>();
        var versionId = instance.VersionId;
        if (string.IsNullOrWhiteSpace(versionId)) return missing;

        var versionDir = Path.Combine(_ctx.Paths.GameDirectory, "versions", versionId);
        var json = Path.Combine(versionDir, versionId + ".json");
        var jar = Path.Combine(versionDir, versionId + ".jar");

        if (!File.Exists(json)) missing.Add(Lang.T("String.Launch.MissingJson"));
        if (!File.Exists(jar)) missing.Add(Lang.T("String.Launch.MissingJar"));

        // 文件不在这个实例的目录里，但可能装在别的目录（实例文件夹 / 共享 .minecraft）。
        // 以前会直接说"文件不见了"，用户看到的却是 .minecraft 空、东西都在别处。
        if (missing.Count > 0)
        {
            var elsewhere = FindVersionJsonElsewhere(versionId);
            if (elsewhere is not null)
            {
                missing.Add(Lang.F("String.Launch.FoundElsewhere", elsewhere));
                Log.Warn("Launch", Lang.F("String.Launch.FoundElsewhereLog", versionId,
                    _ctx.WorkingDirectoryFor(instance), elsewhere));
            }
        }
        return missing;
    }

    /// <summary>
    /// 从其他已装好的游戏目录复用版本文件（JSON / jar）。
    /// 同一个 versionId 的内容完全一样，没必要再下一遍——
    /// 这就是"把已有版本文件夹当缓存"：源是别的版本目录，目的地始终是当前实例的版本目录。
    /// 同卷走硬链接（零拷贝），跨卷退回复制。
    /// </summary>
    private int ReuseVersionFilesFrom(GameInstance instance, string versionId)
    {
        // 目的地是共享游戏目录（.minecraft 或指定文件夹），隔离目录只作来源、不作存放点
        var targetRoot = _ctx.SharedGameDirectoryFor(instance);
        var targetDir = Path.Combine(targetRoot, "versions", versionId);
        var fixedCount = 0;

        foreach (var directory in CandidateDirectories())
        {
            if (string.Equals(directory, targetRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var sourceDir = Path.Combine(directory, "versions", versionId);
            if (!Directory.Exists(sourceDir)) continue;

            foreach (var fileName in new[] { versionId + ".json", versionId + ".jar" })
            {
                var source = Path.Combine(sourceDir, fileName);
                var target = Path.Combine(targetDir, fileName);
                if (!File.Exists(source) || File.Exists(target)) continue;
                try
                {
                    Directory.CreateDirectory(targetDir);
                    if (HardLink.TryCreate(source, target))
                    {
                        fixedCount++;
                        Log.Info("Launch", Lang.F("String.Launch.ReusedVersionFile", fileName, directory));
                    }
                }
                catch (Exception) { /* 复用失败就照常报错，不中断 */ }
            }
            if (fixedCount > 0) break;
        }
        return fixedCount;
    }

    /// <summary>
    /// 联网补装缺失的版本文件：从版本清单找到对应条目，重下版本 JSON 与本体。
    /// 用于整合包只装了模组、本体从未装过，或 .minecraft 被清掉的情况。
    /// 返回 true 表示补装成功。
    /// </summary>
    private async Task<bool> TryRepairVersionFilesAsync(GameInstance instance)
    {
        var versionId = instance.VersionId;
        if (string.IsNullOrWhiteSpace(versionId)) return false;

        var repairTask = _ctx.Downloads.BeginTask(L.T("String.Task.KindGame"), "game");
        _ctx.Downloads.MarkTaskReady();
        try
        {
            var manifest = await _ctx.ManifestService.GetManifestAsync().ConfigureAwait(false);
            var entry = manifest?.Versions.FirstOrDefault(
                v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                Log.Warn("Launch", Lang.F("String.Launch.RepairNoManifestEntry", versionId));
                return false;
            }

            var previous = _ctx.Paths.BeginGameDirectory(_ctx.SharedGameDirectoryFor(instance));
            try
            {
                var json = await _ctx.ManifestService.FetchVersionJsonAsync(entry).ConfigureAwait(false);
                if (json is null)
                {
                    Log.Warn("Launch", Lang.F("String.Launch.RepairJsonFailed", versionId));
                    return false;
                }

                var items = await _ctx.Installer.BuildInstallItemsAsync(json, true).ConfigureAwait(false);
                if (items.Count > 0)
                    await _ctx.Downloads.RunItemsAsync(items, repairTask).ConfigureAwait(false);

                return true;
            }
            finally
            {
                _ctx.Paths.EndGameDirectory(previous);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Launch", Lang.F("String.Launch.RepairFailed", versionId) + " " + ex.Message);
            return false;
        }
        finally
        {
            _ctx.Downloads.MarkFinished(repairTask);
        }
    }

    /// <summary>
    /// 在其他已知目录里找同名版本 JSON。用于"文件装好了但实例指向错的目录"的诊断。
    /// 只在读，不做任何移动。
    /// </summary>
    private string? FindVersionJsonElsewhere(string versionId)
    {
        var relative = Path.Combine("versions", versionId, versionId + ".json");
        foreach (var directory in CandidateDirectories())
        {
            try
            {
                var candidate = Path.Combine(directory, relative);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { /* 路径非法就跳过 */ }
        }
        return null;
    }

    /// <summary>所有可能的游戏目录：当前游戏目录、实例文件夹、隔离实例根目录。</summary>
    private IEnumerable<string> CandidateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        var current = _ctx.Paths.GameDirectory;
        if (!string.IsNullOrWhiteSpace(current)) { list.Add(current); seen.Add(current); }

        if (_ctx.Folders is not null)
            foreach (var folder in _ctx.Folders.Folders)
            {
                if (!_ctx.Folders.TryResolve(folder, out var full)) continue;
                if (seen.Add(full)) list.Add(full);
            }

        var instancesRoot = _ctx.Paths.InstancesRoot;
        if (!string.IsNullOrWhiteSpace(instancesRoot) && Directory.Exists(instancesRoot))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(instancesRoot))
                    if (seen.Add(directory)) list.Add(directory);
            }
            catch (Exception) { }
        }
        return list;
    }

    /// <summary>指定本次启动使用的账户；为空则用设置里选中的那个。</summary>
    public LPML.Core.Auth.Account? AccountOverride { get; set; }

    public async Task<LaunchResult> LaunchAsync(GameInstance instance, bool debugMode,
        CancellationToken token = default)
    {
        // 允许多开：不再做全局占位拦截，每个实例各自登记成一个会话。
        // 命令行模式没有会话服务，这里只写日志，不参与判断。
        Log.Info("Launch", Lang.F("String.Msg.Game.LaunchLog", instance.Name));
        return await LaunchGuardedAsync(instance, debugMode, token).ConfigureAwait(false);
    }

    private async Task<LaunchResult> LaunchGuardedAsync(GameInstance instance, bool debugMode,
        CancellationToken token)
    {
        var privacy = _ctx.Settings.Privacy;
        var account = AccountOverride ?? _ctx.Accounts.Current(_ctx.Settings.SelectedAccountId);

        // 应用版本隔离布局（独立目录 / 共享链接）
        _ctx.Instances.ApplyIsolationLayout(instance, _ctx.Paths.GameDirectory);

        // 启动前把实例信息写进实例目录的 lpml.json：
        // 万一以后 .minecraft 被删、启动器注册表也丢了，靠这个文件还能恢复出可启动的实例。
        try
        {
            var mode = _ctx.IsolationModeFor(instance);
            _ctx.Instances.WriteLocalConfig(instance, mode);
        }
        catch (Exception) { /* 写不了就算了，不该拦住启动 */ }

        // 版本文件（versions / libraries / assets / natives）在"共享游戏目录"里解析：
        // 指定目录 > 实例文件夹 > .minecraft。版本隔离不影响它，隔离只隔离 mods/saves/config。
        // 以前连版本文件也跟着隔离走，装好的版本跑进 LPML\instances\<id>，主目录看着是空的。
        var sharedDirectory = _ctx.SharedGameDirectoryFor(instance);
        var previousDirectory = _ctx.Paths.BeginGameDirectory(sharedDirectory);
        try
        {
            return await LaunchCoreAsync(instance, debugMode, account, privacy, token).ConfigureAwait(false);
        }
        finally
        {
            _ctx.Paths.EndGameDirectory(previousDirectory);
        }
    }

    private async Task<LaunchResult> LaunchCoreAsync(GameInstance instance, bool debugMode,
        LPML.Core.Auth.Account? account, LPML.Core.Config.PrivacySettings privacy, CancellationToken token)
    {

        // 实例还没绑定游戏版本（例如整合包没解析出 MC 版本）→ 直接说明，而不是去找"Java 0"
        if (string.IsNullOrWhiteSpace(instance.VersionId))
        {
            Log.Warn("Launch", Lang.T("String.Launch.NoVersionLog"));
            return new LaunchResult
            {
                Success = false,
                ErrorMessage = Lang.T("String.Launch.NoVersion")
            };
        }

        // 本实例目录里没有，但别的游戏目录装过同一个版本 → 直接复用过来（硬链接 / 复制）。
        // 这样"已有版本文件夹就是缓存"，不用重下，也不用维护独立的缓存目录。
        if (FindMissingGameFiles(instance).Count > 0)
        {
            var reused = ReuseVersionFilesFrom(instance, instance.VersionId);
            if (reused > 0)
                Log.Info("Launch", Lang.F("String.Launch.ReusedVersionFiles", reused, instance.Name));
        }

        // 复用不到、本地又没有，就联网补装一遍。
        // 典型场景：整合包实例只装了模组，MC 本体从来没装过，
        // 于是 VersionId 指向 26.3 但 versions\26.3\26.3.json 根本不存在。
        // 以前只会报"文件不见了"，用户只能手动重装。
        if (FindMissingGameFiles(instance).Count > 0 && _ctx.Settings.Launch.AutoDownloadMissingFiles)
        {
            var repaired = await TryRepairVersionFilesAsync(instance);
            if (repaired)
                Log.Info("Launch", Lang.F("String.Launch.RepairedVersionFiles", instance.VersionId, instance.Name));
        }

        // 游戏文件被删（比如手动清了 .minecraft）时，实例标识还在但文件没了。
        // 以前会一路走到 Java 解析，抛出误导性的"没有找到可用的 Java 0"。
        var missingCore = FindMissingGameFiles(instance);
        if (missingCore.Count > 0)
        {
            Log.Warn("Launch", Lang.F("String.Launch.MissingFilesLog", string.Join("、", missingCore)));
            return new LaunchResult
            {
                Success = false,
                ErrorMessage = Lang.F("String.Launch.MissingFiles", string.Join("、", missingCore))
            };
        }

        // ── 启动进度窗：依赖 → 登录 → 准备 → 启动 → 到主界面才关 ──
        LaunchProgressWindow? progressWindow = null;
        if (!Headless)
        {
            progressWindow = CreateProgressWindow(instance);
            progressWindow.SetStage(1);
            progressWindow.SetDetail(1, L.T("String.Launch.Checking"));
        }

        var options = BuildOptions(instance, account);

        StatusChanged?.Invoke(this, L.T("String.Msg.Game.Launching"));

        // 阶段 1：补全依赖（显示文件数、速度）
        var missing = await _ctx.Launcher.BuildRepairItemsAsync(instance.VersionId, token).ConfigureAwait(false);
        if (missing.Count > 0)
        {
            progressWindow?.SetDetail(1, L.F("String.Launch.FilesToDownload", missing.Count));
            var repairTask = _ctx.Downloads.BeginTask(L.T("String.Task.KindGame"), "game");
            _ctx.Downloads.MarkTaskReady();
            var progress = new Progress<DownloadAggregate>(agg => progressWindow?.SetDownloadProgress(agg));
            await CompleteFilesAsync(instance, progress, token, null, repairTask).ConfigureAwait(false);
            _ctx.Downloads.MarkFinished(repairTask);
        }
        // 补全之后再查一次：下载失败了（403/429 之类）就别硬启动，
        // 否则游戏会因为缺库崩溃，用户只会看到"启动成功但没窗口"。
        var stillMissing = missing.Count == 0
            ? 0
            : (await _ctx.Launcher.BuildRepairItemsAsync(instance.VersionId, token).ConfigureAwait(false)).Count;
        if (stillMissing > 0)
        {
            progressWindow?.SetDetail(1, L.F("String.Launch.FilesStillMissing", stillMissing));
            Log.Warn("Launch", Lang.F("String.Launch.FilesStillMissingLog", instance.Name, stillMissing));
            progressWindow?.RequestClose();
            return new LaunchResult
            {
                Success = false,
                ErrorMessage = Lang.F("String.Launch.FilesStillMissing", stillMissing)
            };
        }

        progressWindow?.SetDetail(1, missing.Count == 0
            ? L.T("String.Launch.FilesUpToDate")
            : L.F("String.Launch.FilesDownloaded", missing.Count));

        // 阶段 2：登录
        progressWindow?.SetStage(2);
        var loginDetail = account is null
            ? L.T("String.Launch.NoAccount")
            : account.Type switch
            {
                LPML.Core.Auth.AccountType.Offline => L.T("String.Launch.OfflinePass"),
                LPML.Core.Auth.AccountType.Microsoft => L.T("String.Launch.MicrosoftLogin"),
                _ => L.T("String.Launch.YggdrasilLogin")
            };
        progressWindow?.SetDetail(2, loginDetail);
        if (account is not null && account.Type != LPML.Core.Auth.AccountType.Offline)
        {
            // 正版 / 外置：确保令牌有效，过期就刷新
            try
            {
                // 令牌过期就刷新（内部会按账户类型走微软 / Yggdrasil）
                await _ctx.Accounts.RefreshAllAsync(token).ConfigureAwait(false);
                progressWindow?.SetDetail(2, L.T("String.Launch.LoginDone"));
            }
            catch (Exception ex)
            {
                Log.Warn("Launch", "Login refresh failed: " + ex.Message);
                progressWindow?.SetDetail(2, L.T("String.Launch.LoginFailed") + ex.Message);
            }
        }
        else
        {
            progressWindow?.SetDetail(2, L.T("String.Launch.LoginDone"));
        }

        // 阶段 3：准备（拼命令等）
        progressWindow?.SetStage(3);
        progressWindow?.SetDetail(3, L.T("String.Launch.Preparing"));

        var result = await _ctx.Launcher.LaunchAsync(instance.VersionId, account, options, token).ConfigureAwait(false);

        // 记录本次实际使用的 Java（失败重试后会变），供诊断与提示展示
        if (result.UsedJava is not null)
        {
            instance.LastLaunchSummary = $"{result.UsedJava.TypeText} {result.UsedJava.MajorVersion} · {result.UsedJava.Vendor}";
            instance.LastLaunchAt = DateTimeOffset.Now;
            instance.LastLaunchSuccess = result.Success;
        }

        if (!result.Success)
        {
            // 失败原因必须进日志。以前只在弹窗里显示，日志一片空白，
            // 事后根本查不出"Java 0"到底是哪种失败。
            Log.Warn("Launch", Lang.F("String.Launch.FailedLog",
                instance.Name, instance.VersionId, result.ErrorMessage ?? ""));
            if (result.FailedJavaPaths is { Count: > 0 })
                Log.Warn("Launch", Lang.F("String.Launch.FailedJavaLog", string.Join(" | ", result.FailedJavaPaths)));
            progressWindow?.RequestClose();
            return result;
        }

        // 换过 Java 才成功时提示一句，让用户知道发生了什么
        if (result.JavaRetryCount > 0 && result.UsedJava is not null)
        {
            StatusChanged?.Invoke(this, Lang.F("String.Java.RetriedWith", result.JavaRetryCount, result.UsedJava.Name));
            Log.Info("Launch", Lang.F("String.Java.RetriedLog", result.UsedJava.Name));
        }

        instance.LastPlayed = DateTimeOffset.Now;
        instance.PlayCount++;
        _ctx.Instances.Save();

        var process = _ctx.Launcher.RunningProcess;
        if (process is null) return result;

        // 登记成独立会话：结束游戏时按会话选择，不影响其它正在跑的实例
        var sessionId = App.GameSession?.Register(process, instance);

        // 无论是否开日志窗口/隐私开关，都在内存里留一份输出尾部，供异常退出时回看。
        // 游戏输出带实例名，多个实例一起跑时日志不会混在一起。
        process.OutputReceived += (_, line) =>
        {
            AppendTail(line);
            Log.Info("Game", $"[{instance.Name}] {line}");
        };

        // 日志监听：实例级开关优先，其次全局隐私开关
        var monitoring = instance.MonitorLogEffective(privacy.EnableLogMonitoring);
        var showWindow = monitoring && debugMode && privacy.LaunchWithLogWindow;

        // 阶段 4：已运行 Java，等 MC 输出"到主界面"
        progressWindow?.SetStage(4);
        progressWindow?.SetDetail(4, L.T("String.Launch.Running"));

        if (progressWindow is not null)
        {
            var window = progressWindow;
            _monitor = new GameLogMonitor { DetectPort = privacy.NotifyServerPort, DetectCrash = privacy.NotifyOnCrash };
            _monitor.LineReceived += (_, line) => window.AppendLog(line.Raw);
            _monitor.ReadyDetected += (_, _) => window.RequestClose();
            StartPump(process);
        }

        if (monitoring && !Headless) AttachMonitor(instance, process, options, showWindow);

        // 退出事件无条件挂上：命令行模式也要把退出码写进启动器日志
        process.Exited += (_, code) => OnProcessExited(instance, code, sessionId);

        // 无界面模式只保留落盘日志，不建窗口
        if (Headless && privacy.SaveGameLogToFile) OpenGameLogFile(instance);

        return result;
    }

    private LaunchProgressWindow CreateProgressWindow(GameInstance instance)
    {
        LaunchProgressWindow? window = null;
        Application.Current.Dispatcher.Invoke(() =>
        {
            window = new LaunchProgressWindow(instance.Name);
            window.Show();
        });
        return window!;
    }

    /// <summary>把进程输出喂给监控器（供进度窗显示日志尾巴 / 检测主界面）。</summary>
    private void StartPump(GameProcess process)
    {
        // GameProcess 自带输出事件，直接接上即可
        process.OutputReceived += (_, line) => _monitor?.Feed(line);
    }

    private void AttachMonitor(GameInstance instance, GameProcess process, LaunchOptions options, bool showWindow)
    {
        var privacy = _ctx.Settings.Privacy;
        _monitor = new GameLogMonitor
        {
            DetectPort = privacy.NotifyServerPort,
            DetectCrash = privacy.NotifyOnCrash
        };

        if (showWindow && !Headless)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _logWindow ??= new GameLogWindow();
                _logWindow.Show();
                _logWindow.Activate();
            });
        }

        if (privacy.SaveGameLogToFile) OpenGameLogFile(instance);

        _monitor.LineReceived += (_, line) =>
        {
            if (!Headless)
                Application.Current.Dispatcher.BeginInvoke(() => _logWindow?.Append(line));
            WriteGameLog(line.Raw);
        };

        _monitor.PortOpened += (_, args) =>
        {
            WriteGameLog($"[{DateTime.Now:HH:mm:ss}] [LPML/INFO] {L.F("String.Msg.Game.PortOpenedLog", args.Port)}");
            if (!privacy.NotifyServerPort || Headless) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
                Notice.Info(L.T("String.Msg.Game.PortTitle"),
                    L.F("String.Msg.Game.PortOpened", args.Port)));
        };

        _monitor.CrashDetected += (_, args) =>
        {
            if (!privacy.NotifyOnCrash || Headless) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (privacy.ShowLogWindowOnCrash)
                {
                    _logWindow ??= new GameLogWindow();
                    _logWindow.Show();
                    _logWindow.Activate();
                }
                NotifyCrash(instance, args.Reason, args.CrashReportPath);
            });
        };

        process.OutputReceived += (_, text) => _monitor.Feed(text);
    }

    private void OpenGameLogFile(GameInstance instance)
    {
        try
        {
            var directory = _ctx.Instances.InstanceLogDirectory(instance);
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"game-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _gameLogWriter = new StreamWriter(
                new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read),
                FileLogSink.LogEncoding)
            {
                AutoFlush = true
            };
            Log.Info("Game", "Game log saved: " + file);
        }
        catch (IOException ex) { Log.Warn("Game", "Failed to save game log: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { Log.Warn("Game", "Failed to save game log: " + ex.Message); }
    }

    private void WriteGameLog(string line)
    {
        try { _gameLogWriter?.WriteLine(line); }
        catch (IOException) { }
    }

    private void OnProcessExited(GameInstance instance, int exitCode, string? sessionId)
    {
        _logWindow?.MarkStopped();
        // 只判断"这一次会话"是不是被用户结束，别把别的实例也当成主动关闭
        var killedByUser = App.GameSession?.WasKilledByUser(sessionId) == true;
        var exitText = killedByUser
            ? Lang.T("String.Msg.Game.KilledByUser")
            : L.F("String.Msg.Game.ExitedLog", exitCode);
        var tagged = $"[{instance.Name}] {exitText}";
        WriteGameLog($"[{DateTime.Now:HH:mm:ss}] [LPML/INFO] {tagged}");

        // 退出码之前只写进游戏日志文件，启动器日志里完全没有，排查时看不到 -7 这类信息
        if (killedByUser || exitCode == 0) Log.Info("Launch", tagged);
        else Log.Warn("Launch", tagged);

        // 异常退出时把崩溃前最后几行原始输出补进启动器日志，方便一眼定位
        if (!killedByUser && exitCode != 0)
        {
            var tail = SnapshotTail(CrashTailCount);
            if (tail.Count == 0)
            {
                Log.Warn("Launch", L.T("String.Msg.Game.CrashTailEmpty"));
            }
            else
            {
                Log.Warn("Launch", $"[{instance.Name}] " + L.F("String.Msg.Game.CrashTailHeader", tail.Count));
                foreach (var line in tail) Log.Warn("Game", $"  [{instance.Name}] {line}");
            }
        }

        try { _gameLogWriter?.Dispose(); }
        catch (IOException) { }
        _gameLogWriter = null;

        var privacy = _ctx.Settings.Privacy;
        var abnormal = !killedByUser && exitCode != 0;

        if (!abnormal || !privacy.NotifyOnCrash || Headless) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
            NotifyCrash(instance, L.F("String.Msg.Game.CrashReason.ExitCode", exitCode), null));
    }

    /// <summary>崩溃提示：可一键打开日志文件夹。</summary>
    private void NotifyCrash(GameInstance instance, string reason, string? crashReportPath)
    {
        var logDirectory = _ctx.Instances.InstanceLogDirectory(instance);
        try { Directory.CreateDirectory(logDirectory); } catch (IOException) { }

        var text = L.F("String.Msg.Game.Crashed", reason);
        if (!Notice.Ask(L.T("String.Msg.Game.CrashTitle"),
                text + "\n\n" + L.T("String.Msg.Game.OpenLogQuestion"))) return;

        var target = !string.IsNullOrWhiteSpace(crashReportPath) && File.Exists(crashReportPath)
            ? Path.GetDirectoryName(crashReportPath)
            : logDirectory;

        try
        {
            Process.Start(new ProcessStartInfo(target ?? logDirectory) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>组装启动参数（实例覆盖优先于全局设置）。</summary>
    public LaunchOptions BuildOptions(GameInstance instance, Account? account)
    {
        var java = _ctx.Settings.Java;
        var launch = _ctx.Settings.Launch;

        var autoMemory = instance.AutoMemoryOverride ?? java.AutoMemory;
        var max = autoMemory ? ResolveAutoMemory() : (instance.MaxMemoryOverrideMb ?? java.MaxMemoryMb);
        var min = instance.MinMemoryOverrideMb ?? java.MinMemoryMb;

        var options = new LaunchOptions
        {
            VersionId = instance.VersionId,
            WorkingDirectory = _ctx.WorkingDirectoryFor(instance),
            JavaPath = instance.JavaPathOverride ?? (string.IsNullOrWhiteSpace(java.JavaPath) ? null : java.JavaPath),
            MinMemoryMb = Math.Min(min, max),
            MaxMemoryMb = max,
            ExtraJvmArguments = instance.JvmArgumentOverride ?? java.JvmArguments,
            ExtraGameArguments = instance.GameArgumentOverride ?? launch.GameArguments,
            WindowWidth = instance.WindowWidthOverride ?? launch.WindowWidth,
            WindowHeight = instance.WindowHeightOverride ?? launch.WindowHeight,
            Fullscreen = instance.FullscreenOverride ?? launch.Fullscreen,
            ServerAddress = string.IsNullOrWhiteSpace(instance.ServerOverride)
                ? NullIfEmpty(launch.QuickJoinServer)
                : instance.ServerOverride,
            WorldName = NullIfEmpty(launch.QuickJoinWorld),
            EnableCheats = launch.EnableCheats,
            CustomWindowTitle = (instance.UseCustomWindowTitleOverride ?? launch.UseCustomWindowTitle)
                ? (instance.WindowTitleOverride ?? launch.CustomWindowTitle)
                : null,
            ProcessPriority = instance.ProcessPriorityOverride ?? java.ProcessPriority,
            UseIsolatedNativesDirectory = launch.UseIsolatedNativesDirectory,
            EnvironmentVariables = ParseEnvironment(instance.EnvironmentVariablesOverride ?? java.EnvironmentVariables),
            AutoJoinAddress = PendingSessionStore.RequestedAddress
        };
        PendingSessionStore.RequestedAddress = null; // 只用一次，避免重启后误连

        if (account is { RequiresAuthlibInjector: true } && !string.IsNullOrEmpty(account.AuthServer))
        {
            options.AuthServerUrl = account.AuthServer;
            options.AuthlibInjectorPath = _ctx.Settings.Account.AuthlibInjectorPath;
            options.AuthPrefetched = YggdrasilService.BuildPrefetched(account);
        }

        RunPreLaunchCommand(instance, options);
        return options;
    }

    /// <summary>执行实例的「启动前命令」（若配置了）。失败不阻断启动，只记日志。</summary>
    private void RunPreLaunchCommand(GameInstance instance, LaunchOptions options)
    {
        var command = instance.ExpandPreLaunchCommand(
            _ctx.Instances.InstanceDirectory(instance), _ctx.Paths.GameDirectory, options.JavaPath);
        if (string.IsNullOrWhiteSpace(command)) return;

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = options.WorkingDirectory
            };
            if (OperatingSystem.IsWindows()) psi.ArgumentList.Add("/c"); else psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            System.Diagnostics.Process.Start(psi);
            Log.Info("Launch", $"Pre-launch command executed: {command}");
        }
        catch (System.ComponentModel.Win32Exception ex) { Log.Warn("Launch", "Pre-launch command failed: " + ex.Message); }
        catch (InvalidOperationException ex) { Log.Warn("Launch", "Pre-launch command failed: " + ex.Message); }
    }

    /// <summary>按物理内存比例算出自动内存上限（MB）。</summary>
    private int ResolveAutoMemory()
    {
        var java = _ctx.Settings.Java;
        try
        {
            var totalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
            if (totalMb <= 0) return java.MaxMemoryMb;
            var value = (int)(totalMb * java.AutoMemoryRatio);
            return Math.Clamp(value, java.MinMemoryMb, java.MaxMemoryMb);
        }
        catch (OverflowException) { return java.MaxMemoryMb; }
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
}
