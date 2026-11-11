using System.Diagnostics;
using System.IO;
using System.Windows;
using LPML.App.Windows;
using LPML.Core;
using LPML.Core.Config;
using LPML.Core.Update;
using LPML.Core.Utils;

namespace LPML.App.Services;

/// <summary>把 Core 的更新检查与界面串起来：自动检查、手动检查、下载应用、拉起暂存程序。</summary>
public static class UpdateCoordinator
{
    /// <summary>强制更新弹窗正在等待时，主窗口也不允许被关掉。</summary>
    public static bool IsMandatoryPending { get; private set; }

    private static bool Chinese => App.Context.Settings.Language == LauncherLanguage.ZhHans;

    /// <summary>启动时的自动检查：失败只记日志，绝不打断启动。</summary>
    public static async Task CheckOnStartupAsync(Window owner)
    {
        var settings = App.Context.Settings;
        if (!settings.Update.Enabled || !settings.Advanced.CheckUpdateOnStartup) return;
        try
        {
            await CheckCoreAsync(owner, manual: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("Update", "启动时检查更新失败：" + ex.Message);
        }
    }

    /// <summary>设置页的手动检查：任何结果都给用户一个明确答复。</summary>
    public static async Task CheckManuallyAsync(Window owner)
    {
        var settings = App.Context.Settings;
        if (!settings.Update.Enabled)
        {
            Notice.Info(Lang.T("String.Update.DisabledHint"));
            return;
        }
        try
        {
            await CheckCoreAsync(owner, manual: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warn("Update", "手动检查更新失败：" + ex.Message);
            Notice.Warn(Lang.F("String.Update.CheckFailed", ex.Message));
        }
    }

    private static async Task CheckCoreAsync(Window owner, bool manual)
    {
        var settings = App.Context.Settings;
        var result = await UpdateService.CheckAsync(
            App.Context.Http, Chinese, settings.Update.SignatureFileName).ConfigureAwait(true);

        if (!result.Manifest.Have)
        {
            if (manual) Notice.Info(Lang.T("String.Update.NoUpdate"));
            return;
        }
        if (result.Manifest.Num <= settings.Update.CurrentNum)
        {
            if (manual) Notice.Info(Lang.T("String.Update.NoUpdate"));
            return;
        }
        // 设置里屏蔽可选更新只作用于自动检查；手动检查仍能看到全部
        if (!manual && settings.Update.MuteOptional && result.Manifest.Important == 3)
        {
            Log.Info("Update", "可选更新已按设置屏蔽：num=" + result.Manifest.Num);
            return;
        }

        IsMandatoryPending = result.Manifest.Important == 1;
        try
        {
            var window = new UpdateWindow(result, App.Context) { Owner = owner };
            window.ShowDialog();
        }
        finally
        {
            IsMandatoryPending = false;
        }
    }

    /// <summary>
    /// 下载 → 校验 → 解压 → 写接力标记 → 拉起 cache 里的新程序 → 关闭旧启动器。
    /// 成功时进程会退出；失败抛回窗口，允许重试。
    /// </summary>
    public static async Task ApplyAsync(UpdateWindow window, UpdateCheckResult result,
        int downloadLine, int packageKind,
        Action<string> status, IProgress<(long Received, long Total)> progress)
    {
        var context = App.Context;
        var settings = context.Settings;

        settings.Update.DownloadLine = Math.Clamp(downloadLine, 0, 2);
        settings.Update.PackageKind = Math.Clamp(packageKind, 0, 2);
        context.Save();

        var runtimeBundled = UpdateRuntimeDetector.HasBundledRuntime();
        var withRuntime = UpdateService.ResolvePackageKind(settings.Update, runtimeBundled);
        Log.Info("Update", $"当前安装：{(runtimeBundled ? "带 .NET 运行时" : "不带 .NET 运行时")}"
            + $"（依据 {UpdateRuntimeDetector.DetectionEvidence()}）；"
            + $"本次下载：{(withRuntime ? "带运行时" : "不带运行时")}包");

        status(Lang.T("String.Update.Downloading"));
        var package = await UpdateService.PrepareStageAsync(
            context.Http, result.Manifest, settings.Update, runtimeBundled,
            context.Paths.CacheDirectory, progress).ConfigureAwait(true);

        status(Lang.T("String.Update.Applying"));

        var target = context.Paths.RootDirectory;
        var stageInfo = new UpdateStageInfo
        {
            Target = target,
            Source = package.SourceDirectory,
            Cleanup = package.StageDirectory,
            Pid = Environment.ProcessId,
            Num = result.Manifest.Num,
            Id = result.Manifest.Id,
            Exe = package.ExecutableName
        };
        Json.Save(Path.Combine(package.StageDirectory, UpdateService.StageFlagName), stageInfo);

        // 旧程序先记一次 num；最终程序启动时还会按 --lpml-update-num 再写一次
        settings.Update.CurrentNum = Math.Max(settings.Update.CurrentNum, result.Manifest.Num);
        settings.Update.CurrentId = result.Manifest.Id;
        context.Save();

        var stagingExe = Path.Combine(package.SourceDirectory, package.ExecutableName);
        var startInfo = new ProcessStartInfo(stagingExe)
        {
            UseShellExecute = true,
            WorkingDirectory = package.SourceDirectory
        };
        startInfo.ArgumentList.Add("--lpml-update-stage");
        startInfo.ArgumentList.Add("--lpml-update-target=" + target);
        startInfo.ArgumentList.Add("--lpml-update-source=" + package.SourceDirectory);
        startInfo.ArgumentList.Add("--lpml-update-cleanup=" + package.StageDirectory);
        startInfo.ArgumentList.Add("--lpml-update-pid=" + Environment.ProcessId);
        startInfo.ArgumentList.Add("--lpml-update-num=" + result.Manifest.Num);
        startInfo.ArgumentList.Add("--lpml-update-id=" + result.Manifest.Id);
        startInfo.ArgumentList.Add("--lpml-update-exe=" + package.ExecutableName);
        Process.Start(startInfo);

        Log.Info("Update", "已拉起暂存程序，启动器即将重启：" + stagingExe);
        window.MarkUpdateStarted();

        var application = Application.Current;
        if (application is null) Environment.Exit(0);
        else if (application.Dispatcher.CheckAccess()) application.Shutdown();
        else application.Dispatcher.Invoke(new Action(application.Shutdown));
    }
}
