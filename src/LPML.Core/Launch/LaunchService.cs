using LPML.Core.Auth;
using LPML.Core.Game;
using LPML.Core.Game.Models;
using LPML.Core.Java;
using LPML.Core.Download;
using LPML.Core.Mirror;
using LPML.Core.Utils;

namespace LPML.Core.Launch;

/// <summary>
/// 启动编排：解析版本 → 校验文件 → 选 Java → 解压 natives → 生成参数 → 启动进程。
/// </summary>
public sealed class LaunchService
{
    private readonly AppPaths _paths;
    private readonly VersionResolver _resolver;
    private readonly LaunchArgumentBuilder _builder;
    private readonly JavaService _javaService;
    private readonly GameInstaller _installer;
    private readonly DownloadManager _downloads;

    public LaunchService(AppPaths paths, VersionResolver resolver, LaunchArgumentBuilder builder,
        JavaService javaService, GameInstaller installer, DownloadManager downloads)
    {
        _paths = paths;
        _resolver = resolver;
        _builder = builder;
        _javaService = javaService;
        _installer = installer;
        _downloads = downloads;
    }

    public GameProcess? RunningProcess { get; private set; }
    public string LastCommandLine { get; private set; } = "";

    /// <summary>列出缺失文件（启动前检查 / 修复用）。</summary>
    public List<string> FindMissingFiles(string versionId)
    {
        var version = _resolver.Load(versionId);
        var missing = new List<string>();
        if (version is null) return missing;
        _builder.BuildClasspath(version, versionId, out missing);
        if (!File.Exists(_paths.VersionJarPath(versionId))) missing.Add(_paths.VersionJarPath(versionId));
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>把缺失文件转成下载任务，交给统一的下载中心。</summary>
    public async Task<List<DownloadItem>> BuildRepairItemsAsync(string versionId, CancellationToken token = default)
    {
        var version = _resolver.Load(versionId);
        if (version is null) return new List<DownloadItem>();
        return await _installer.BuildRepairItemsAsync(version, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 启动；若失败且设置允许，自动换一个 Java 重试（优先更高版本，其次同版本）。
    /// 设置里可改为「不重启并抛错」，此时一次失败即返回错误。
    /// </summary>
    public async Task<LaunchResult> LaunchAsync(string versionId, Account? account, LaunchOptions options,
        CancellationToken token = default)
    {
        var settings = _javaService.Settings;
        var maxAttempts = settings.OnFailure == JavaFailureAction.RetryWithAnother
            ? Math.Max(1, settings.MaxJavaRetries)
            : 1;

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedPaths = new List<string>();
        var required = 0;

        // 启动入口就把关键上下文写进日志：以前日志里只有一句干巴巴的重试，
        // 排查时根本看不出是哪个版本、需要多少 Java、池里有几个。
        var pool = _javaService.Detect();
        Log.Info("Launch", L.F("String.Core.Log.Launch.Start", versionId,
            account?.UserName ?? L.T("String.Core.Log.Launch.NoAccount"),
            pool.Count,
            pool.Count == 0 ? L.T("String.Core.Log.Launch.NoJavaPool")
                            : string.Join(" | ", pool.Take(6).Select(j => $"{j.Name}({j.MajorVersion})"))));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var result = await LaunchOnceAsync(versionId, account, options, tried, token).ConfigureAwait(false);
            required = result.RequiredMajor;

            if (result.Success) return result.ToResult(attempt, failedPaths);

            // 版本 JSON 缺失这类错误换 Java 也没用——以前会继续重试，
            // 最后用 required=0 抛"没有找到可用的 Java 0"，把真正的原因盖住了。
            if (!result.Retryable) return result.ToResult(attempt, failedPaths);

            // 记录这次失败的 Java，下次排除
            if (!string.IsNullOrWhiteSpace(result.JavaPath))
                failedPaths.Add(result.JavaPath!);
            if (result.UsedJava is not null) tried.Add(result.UsedJava.Id);

            // 只重试一次就抛错，或已经没有候选了
            var next = settings.OnFailure == JavaFailureAction.RetryWithAnother
                ? _javaService.NextCandidate(required, tried)
                : null;

            // 死循环防护：java 为 null 时 UsedJava 也是 null，tried 永远是空的，
            // 于是每次都挑中同一个候选——日志里就刷出一模一样的几十条重试。
            if (next is not null)
            {
                if (!tried.Add(next.Id)) next = null;   // 已经试过 → 没有别的候选了
            }

            if (next is null)
            {
                // 到这一步就别再用 required=0 编"Java 0"这种假错误了
                Log.Warn("Launch", L.F("String.Core.Log.Launch.GiveUp",
                    versionId, required, result.ErrorMessage ?? ""));
                return result.ToResult(attempt, failedPaths);
            }

            Log.Warn("Launch", L.F("String.Core.Log.Launch.JavaRetry",
                string.IsNullOrWhiteSpace(result.JavaPath) ? "(未选中)" : result.JavaPath!, next.Name));
        }

        return new LaunchResult
        {
            ErrorMessage = L.F("String.Core.Err.Launch.NoJava", required),
            FailedJavaPaths = failedPaths,
            JavaRetryCount = Math.Max(0, maxAttempts - 1)
        };
    }

    /// <summary>单次启动尝试。tried 里的 Java 会被排除。</summary>
    private async Task<LaunchAttempt> LaunchOnceAsync(string versionId, Account? account, LaunchOptions options,
        ISet<string> tried, CancellationToken token)
    {
        var version = _resolver.Load(versionId);
        if (version is null)
            return LaunchAttempt.Fail(L.F("String.Core.Err.Launch.NoVersionJson", versionId), retryable: false);

        var required = JavaService.RequiredMajor(version);
        var java = ResolveJavaInfo(options.JavaPath, required, tried);
        if (java is null)
            return LaunchAttempt.Fail(L.F("String.Core.Err.Launch.NoJava", required), required);

        var path = java.Path;

        try
        {
            var classpath = string.Join(Path.PathSeparator, _builder.BuildClasspath(version, versionId, out var missing));
            if (missing.Count > 0)
                Log.Warn("Launch", L.F("String.Core.Log.Launch.MissingFiles", missing.Count));

            _builder.ExtractNatives(version, versionId);
            var natives = options.UseIsolatedNativesDirectory
                ? _builder.NativesDirectory(versionId)
                : Path.Combine(_paths.GameDirectory, "natives");

            var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? _paths.GameDirectory
                : options.WorkingDirectory;
            FileHelper.EnsureDirectory(workingDirectory);

            var jvmArguments = _builder.BuildJvmArguments(version, versionId, account, options, classpath, natives);
            var gameArguments = _builder.BuildGameArguments(version, versionId, account, options, classpath, natives);

            var process = new GameProcess();
            var startInfo = process.BuildStartInfo(path, jvmArguments,
                version.MainClass ?? "net.minecraft.client.main.Main", gameArguments, workingDirectory, options.EnvironmentVariables);

            LastCommandLine = startInfo.FileName + " " + string.Join(" ", startInfo.ArgumentList);
            Log.Info("Launch", L.T("String.Core.Log.Launch.Started") + LastCommandLine);

            var id = process.Start(startInfo);
            process.SetPriority(options.ProcessPriority);
            RunningProcess = process;

            return LaunchAttempt.Ok(new LaunchResult
            {
                Success = true,
                ProcessId = id,
                JavaPath = path,
                UsedJava = java,
                ArgumentsPreview = LastCommandLine
            }, required);
        }
        catch (Exception ex)
        {
            Log.Error("Launch", L.T("String.Core.Log.Launch.Failed"), ex);
            return LaunchAttempt.Fail(ex.Message, required, path, java);
        }
    }

    private JavaInfo? ResolveJavaInfo(string? preferred, int requiredMajor, ISet<string> tried)
    {
        if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred!))
            return JavaLocator.ProbeOne(preferred!, JavaSource.Manual) ?? new JavaInfo { Path = preferred!, Id = JavaLocator.ComputeId(preferred!) };

        // 优先用户列表里没试过的
        var managed = _javaService.Manager;
        if (managed is not null)
        {
            var info = managed.ResolveExcluding(requiredMajor, tried);
            if (info is not null) return info;
        }

        var pool = _javaService.Detect().Where(j => !tried.Contains(j.Id)).ToList();
        return JavaManager.Recommend(requiredMajor, pool, _javaService.Settings.RuntimePreference);
    }

    /// <summary>单次尝试的内部结果（比 LaunchResult 多带 required 与进程信息）。</summary>
    private sealed class LaunchAttempt
    {
        public bool Success { get; private set; }
        public LaunchResult? Result { get; private set; }
        public string? ErrorMessage { get; private set; }
        public int RequiredMajor { get; private set; }
        public string? JavaPath { get; private set; }
        public JavaInfo? UsedJava { get; private set; }

        /// <summary>换一个 Java 重试是否有意义。版本 JSON 缺失时为 false。</summary>
        public bool Retryable { get; private set; } = true;

        public static LaunchAttempt Ok(LaunchResult result, int required) => new()
        {
            Success = true, Result = result, RequiredMajor = required,
            JavaPath = result.JavaPath, UsedJava = result.UsedJava
        };

        public static LaunchAttempt Fail(string message, int required = 0, string? javaPath = null, JavaInfo? used = null, bool retryable = true)
            => new() { Success = false, ErrorMessage = message, RequiredMajor = required, JavaPath = javaPath, UsedJava = used, Retryable = retryable };

        /// <summary>转成对外的 LaunchResult，并带上重试次数与失败过的 Java 列表。</summary>
        public LaunchResult ToResult(int retries, List<string> failedPaths)
        {
            if (Success && Result is not null)
            {
                Result.JavaRetryCount = retries;
                Result.FailedJavaPaths = failedPaths;
                return Result;
            }
            return new LaunchResult
            {
                Success = false,
                ErrorMessage = ErrorMessage,
                JavaPath = JavaPath ?? "",
                UsedJava = UsedJava,
                JavaRetryCount = retries,
                FailedJavaPaths = failedPaths
            };
        }
    }

    public string BuildPreview(string versionId, Account? account, LaunchOptions options)
    {
        var version = _resolver.Load(versionId);
        if (version is null) return "";
        return _builder.BuildCommandLine(version, versionId, account, options);
    }
}
