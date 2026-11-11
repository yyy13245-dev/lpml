using System.Text.Json;

namespace LPML.Core.Update;

/// <summary>
/// 判断当前安装是「带 .NET 运行时（self-contained）」还是「不带（framework-dependent）」。
/// 更新时自动模式会拉同类型的包，避免把依赖系统 .NET 的安装换成整包（或反过来）。
/// 只做本地文件探测，不联网、不改任何文件。
/// </summary>
public static class UpdateRuntimeDetector
{
    /// <summary>
    /// 随 self-contained 发布一起分发到程序目录的运行时文件。
    /// framework-dependent 安装只带自己的 dll 与 apphost，不会在程序目录里出现这些文件。
    /// </summary>
    private static readonly string[] BundledRuntimeFiles =
    {
        "coreclr.dll",
        "System.Private.CoreLib.dll",
        "clrjit.dll",
        "hostpolicy.dll"
    };

    private static readonly Lazy<bool> CachedDefault = new(() => Detect(AppContext.BaseDirectory));

    /// <summary>程序目录里带有 .NET 运行时文件 → true；否则按不带运行时处理。</summary>
    public static bool HasBundledRuntime(string? appDirectory = null)
        => appDirectory is null ? CachedDefault.Value : Detect(appDirectory);

    /// <summary>给日志看的判定依据，便于用户核对有没有判错。</summary>
    public static string DetectionEvidence(string? appDirectory = null)
    {
        var directory = Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory);
        try
        {
            foreach (var name in BundledRuntimeFiles)
                if (File.Exists(Path.Combine(directory, name))) return name;

            foreach (var config in Directory.EnumerateFiles(directory, "*.runtimeconfig.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(config));
                    if (!document.RootElement.TryGetProperty("runtimeOptions", out var options)) continue;
                    if (options.TryGetProperty("includedFrameworks", out _))
                        return Path.GetFileName(config) + " (includedFrameworks)";
                    if (options.TryGetProperty("framework", out _))
                        return Path.GetFileName(config) + " (framework)";
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
        }
        catch (Exception) { /* 探测失败按不带运行时处理 */ }
        return "no runtime files in app directory";
    }

    private static bool Detect(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            foreach (var name in BundledRuntimeFiles)
                if (File.Exists(Path.Combine(full, name))) return true;

            // 兜底：self-contained 的 runtimeconfig 写 includedFrameworks，
            // framework-dependent 写 framework。两者都没有时按不带运行时处理。
            foreach (var config in Directory.EnumerateFiles(full, "*.runtimeconfig.json"))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(config));
                    if (!document.RootElement.TryGetProperty("runtimeOptions", out var options)) continue;
                    if (options.TryGetProperty("includedFrameworks", out _)) return true;
                    if (options.TryGetProperty("framework", out _)) return false;
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
        }
        catch (Exception) { /* 探测失败按不带运行时处理 */ }
        return false;
    }
}
