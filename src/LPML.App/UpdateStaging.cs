using System.Diagnostics;
using System.IO;
using System.Text;
using LPML.Core.Update;
using LPML.Core.Utils;

namespace LPML.App;

/// <summary>
/// 自更新接力，在 Program.Main 创建任何 WPF 对象之前执行：
///
///   ① 旧启动器把新包解压到 cache\update-*\app，写 lpml-update-stage.json，
///      拉起 app 里的 exe 后关闭自己；
///   ② 这里的「暂存程序」按命令行参数（或按位置找到 flag）认出自己，
///      等旧进程与守护进程释放 exe → 删掉原目录里的程序文件 → 把新程序复制过去
///      → 启动原目录里的新 exe → 自己退出；
///   ③ 最终启动器带 cleanup 参数启动，删掉 cache 里的中间程序。
///
/// 全程不加载语言包、不创建窗口、不启动守护进程，也不碰 LPML\ 与 .minecraft\ 里的用户数据。
/// </summary>
public static class UpdateStaging
{
    private const string StageSwitch = "--lpml-update-stage";
    private const string TargetArg = "--lpml-update-target=";
    private const string SourceArg = "--lpml-update-source=";
    private const string CleanupArg = "--lpml-update-cleanup=";
    private const string PidArg = "--lpml-update-pid=";
    private const string NumArg = "--lpml-update-num=";
    private const string IdArg = "--lpml-update-id=";
    private const string ExeArg = "--lpml-update-exe=";

    /// <summary>最终启动器带的清理参数前缀（Program.Main 也要识别）。</summary>
    public const string CleanupPrefix = CleanupArg;

    private static readonly HashSet<string> UserDataDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "LPML", ".minecraft", "cache", "logs", "config", "accounts", "instances",
        "versions", "libraries", "assets", "saves", "mods", "resourcepacks", "shaderpacks"
    };

    /// <summary>返回 true 表示本次进程已经是「暂存程序」，Main 应直接结束。</summary>
    public static bool TryHandle(string[] args)
    {
        var hasStageSwitch = args.Any(a => a.Equals(StageSwitch, StringComparison.OrdinalIgnoreCase));

        // ③ 清理：正常启动前删掉中间程序，然后继续走原来的启动流程。
        // 暂存程序自己也带 cleanup 参数（要转交给最终程序），所以必须先判 stage 开关，
        // 否则会把「正在运行的自己」的目录删掉。
        if (!hasStageSwitch)
        {
            var cleanup = Value(args, CleanupArg);
            if (cleanup is not null)
            {
                TryDeleteDirectory(cleanup, 8, 400);
                return false;
            }
        }

        var info = (hasStageSwitch ? FromArguments(args) : null) ?? FromFlagFile();
        if (info is null || !IsUsable(info))
        {
            if (hasStageSwitch) Trace("更新接力参数不完整，按普通启动继续");
            return false;
        }

        Apply(info);
        return true;
    }

    private static string? Value(string[] args, string prefix)
        => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.Substring(prefix.Length);

    private static UpdateStageInfo? FromArguments(string[] args)
    {
        var target = Value(args, TargetArg);
        var source = Value(args, SourceArg);
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source)) return null;
        _ = int.TryParse(Value(args, PidArg), out var pid);
        _ = int.TryParse(Value(args, NumArg), out var num);
        return new UpdateStageInfo
        {
            Target = target!,
            Source = source!,
            Cleanup = Value(args, CleanupArg) ?? "",
            Pid = pid,
            Num = num,
            Id = Value(args, IdArg) ?? "",
            Exe = Value(args, ExeArg) ?? "LPML.exe"
        };
    }

    /// <summary>位置兜底：exe 位于 cache\update-* 下，且同目录链上有 stage flag。</summary>
    private static UpdateStageInfo? FromFlagFile()
    {
        try
        {
            DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
            for (var depth = 0; depth < 4 && directory is not null; depth++, directory = directory.Parent)
            {
                var full = directory.FullName;
                var cacheSegment = Path.DirectorySeparatorChar + "cache" + Path.DirectorySeparatorChar;
                if (!full.Contains(cacheSegment, StringComparison.OrdinalIgnoreCase)) continue;

                var flag = Path.Combine(full, UpdateService.StageFlagName);
                if (!File.Exists(flag)) continue;

                var info = Json.Deserialize<UpdateStageInfo>(File.ReadAllText(flag));
                if (info is null) return null;
                if (string.IsNullOrWhiteSpace(info.Source)) info.Source = Path.GetFullPath(AppContext.BaseDirectory);
                return info;
            }
        }
        catch (Exception ex) { Trace("读取更新接力标记失败：" + ex.Message); }
        return null;
    }

    private static bool IsUsable(UpdateStageInfo info)
        => !string.IsNullOrWhiteSpace(info.Target)
           && !string.IsNullOrWhiteSpace(info.Source)
           && Directory.Exists(info.Source)
           && !string.Equals(Path.GetFullPath(info.Target), Path.GetFullPath(info.Source),
               StringComparison.OrdinalIgnoreCase);

    private static void Apply(UpdateStageInfo info)
    {
        var logPath = Path.Combine(info.Target, "LPML", "logs", "update.log");
        WriteLog(logPath, $"开始应用更新：num={info.Num} id={info.Id}");
        WriteLog(logPath, $"暂存目录={info.Source} 目标目录={info.Target}");

        var backup = Path.Combine(string.IsNullOrWhiteSpace(info.Cleanup)
            ? Path.GetDirectoryName(info.Source) ?? info.Source
            : info.Cleanup, "backup");

        try
        {
            // 旧主进程退出时守护进程也要退出，两者都锁着原目录的 exe。
            // 不按 PID 猜，直接探测所有 exe 是否可独占写，最可靠；
            // 新包的可执行文件名可能与旧版不同，所以遍历整个目录。
            var deadline = DateTime.UtcNow.AddSeconds(25);
            foreach (var exe in Directory.EnumerateFiles(info.Target, "*.exe"))
                WaitUntilWritable(exe, deadline, logPath);

            var oldFiles = CollectOldFiles(info.Target, info.Source);
            foreach (var file in oldFiles) BackupFile(file, info.Target, backup);
            foreach (var file in oldFiles)
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            WriteLog(logPath, $"已清理 {oldFiles.Count} 个旧程序文件");

            var copied = CopyProgramFiles(info.Source, info.Target);
            WriteLog(logPath, $"已复制 {copied} 个新程序文件");

            var finalExe = Path.Combine(info.Target, info.Exe);
            if (!File.Exists(finalExe))
                throw new IOException("更新后找不到可执行文件：" + finalExe);

            var startInfo = new ProcessStartInfo(finalExe)
            {
                UseShellExecute = true,
                WorkingDirectory = info.Target
            };
            startInfo.ArgumentList.Add(CleanupArg + (string.IsNullOrWhiteSpace(info.Cleanup)
                ? Path.GetDirectoryName(info.Source) ?? ""
                : info.Cleanup));
            startInfo.ArgumentList.Add(NumArg + info.Num);
            startInfo.ArgumentList.Add(IdArg + info.Id);
            Process.Start(startInfo);
            WriteLog(logPath, "已启动更新后的启动器");
        }
        catch (Exception ex)
        {
            WriteLog(logPath, "更新失败：" + ex);
            WriteLog(logPath, "正在从 backup 回滚，原始程序仍保留在 " + info.Source);
            try { RestoreBackup(backup, info.Target); }
            catch (Exception restore) { WriteLog(logPath, "回滚失败：" + restore); }
        }
    }

    private static void WaitUntilWritable(string exePath, DateTime deadline, string logPath)
    {
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(exePath)) return;
            try
            {
                using var stream = new FileStream(exePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        WriteLog(logPath, "等待旧进程释放 exe 超时，仍继续尝试替换：" + exePath);
    }

    /// <summary>
    /// 只删「程序文件」：根级 exe/dll/pdb/deps/runtimeconfig，以及新包里同名文件。
    /// 用户数据目录整体不动，根目录里用户自己放的文件也不动。
    /// </summary>
    private static List<string> CollectOldFiles(string target, string source)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(target))
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                string extension = Path.GetExtension(file);
                if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".deps.json", StringComparison.Ordinal)
                    || name.EndsWith(".runtimeconfig.json", StringComparison.Ordinal))
                    result.Add(file);
            }
        }
        catch (IOException) { }

        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Length > 1 && UserDataDirectories.Contains(parts[0])) continue;
                var old = Path.Combine(target, relative);
                if (File.Exists(old)) result.Add(old);
            }
        }
        catch (IOException) { }

        return result.ToList();
    }

    private static void BackupFile(string file, string target, string backupDirectory)
    {
        try
        {
            var relative = Path.GetRelativePath(target, file);
            var destination = Path.Combine(backupDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int CopyProgramFiles(string source, string target)
    {
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Length > 1 && UserDataDirectories.Contains(parts[0])) continue;

            var destination = Path.Combine(target, relative);
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, destination, true);
            copied++;
        }
        return copied;
    }

    private static void RestoreBackup(string backupDirectory, string target)
    {
        if (!Directory.Exists(backupDirectory)) return;
        foreach (var file in Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(backupDirectory, file));
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, destination, true);
        }
    }

    private static void TryDeleteDirectory(string path, int attempts, int delayMs)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) { Thread.Sleep(delayMs); }
            catch (UnauthorizedAccessException) { Thread.Sleep(delayMs); }
        }
        Trace("暂存目录未能删除，下次启动会再试：" + path);
    }

    private static void WriteLog(string path, string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n", new UTF8Encoding(true));
        }
        catch (Exception) { /* 日志失败不能阻断更新 */ }
    }

    private static void Trace(string message)
    {
        try { Debug.WriteLine("[UpdateStaging] " + message); }
        catch (Exception) { }
    }
}
