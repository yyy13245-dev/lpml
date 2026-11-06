using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LPML.Core.Java;

/// <summary>处理器架构。下载 Java 运行时时不硬编码，由用户选择。</summary>
public enum JavaArch
{
    /// <summary>未知（无法确定，需用户指定）。</summary>
    Unknown = 0,
    X86 = 1,
    X64 = 2,
    Arm64 = 3,
    Arm32 = 4
}

/// <summary>
/// Java 架构探测。跨芯片场景（x64 / ARM64 / Apple Silicon / 32 位）下
/// 不能假设本机架构就是目标架构，因此：
/// 1) 优先读已有 Java 自报的 os.arch（最可靠——它会告诉你这个 java 是给什么架构编译的）
/// 2) 读不到再退回 .NET 的进程架构
/// 3) 仍然不确定就返回 Unknown，由用户选择
/// </summary>
public static class JavaArchitecture
{
    public static string DisplayName(JavaArch arch) => arch switch
    {
        JavaArch.X64 => "x64 (64 位 x86 / Intel / AMD)",
        JavaArch.X86 => "x86 (32 位)",
        JavaArch.Arm64 => "ARM64 (Apple Silicon / 骁龙 / Windows on ARM)",
        JavaArch.Arm32 => "ARM32 (32 位 ARM)",
        _ => L.T("String.Msg.Common.Unknown")
    };

    public static string ShortName(JavaArch arch) => arch switch
    {
        JavaArch.X64 => "x64",
        JavaArch.X86 => "x86",
        JavaArch.Arm64 => "arm64",
        JavaArch.Arm32 => "arm32",
        _ => "unknown"
    };

    /// <summary>当前操作系统在 Mojang 运行时索引里的前缀。</summary>
    public static string OsPrefix =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "mac-os"
        : "linux";

    /// <summary>
    /// 拼出 Mojang 索引里的平台键。注意：Mojang 的键是 "windows-x64" / "mac-os-arm64"
    /// 这种「系统-架构」连写形式，且并非所有组合都存在（如 linux 的 64 位就叫 "linux"）。
    /// </summary>
    public static string PlatformKey(string osPrefix, JavaArch arch)
    {
        // linux 的 64 位键是裸 "linux"，32 位才是 "linux-i386"
        if (osPrefix == "linux")
            return arch == JavaArch.X86 ? "linux-i386" : "linux";

        // mac-os 的 x64 键是裸 "mac-os"，ARM 是 "mac-os-arm64"
        if (osPrefix == "mac-os")
            return arch == JavaArch.Arm64 ? "mac-os-arm64" : "mac-os";

        // windows 一律带后缀
        return arch switch
        {
            JavaArch.X64 => "windows-x64",
            JavaArch.X86 => "windows-x86",
            JavaArch.Arm64 => "windows-arm64",
            _ => "windows-x64"
        };
    }

    /// <summary>该平台下 Mojang 可能提供的架构（用于让用户挑选）。</summary>
    public static IReadOnlyList<JavaArch> CandidatesFor(string osPrefix) => osPrefix switch
    {
        "linux" => new[] { JavaArch.X64, JavaArch.X86 },
        "mac-os" => new[] { JavaArch.Arm64, JavaArch.X64 },
        _ => new[] { JavaArch.X64, JavaArch.X86, JavaArch.Arm64 }
    };

    /// <summary>
    /// 询问一个 java 可执行文件自报的架构。通过 java -XshowSettings:properties -version
    /// 读取 os.arch（amd64 / x86 / aarch64 / arm）。失败返回 Unknown。
    /// </summary>
    public static JavaArch Detect(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return JavaArch.Unknown;

        try
        {
            var startInfo = new ProcessStartInfo(exePath, "-XshowSettings:properties -version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return JavaArch.Unknown;

            var stdout = process.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
            var stderr = process.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
            process.WaitForExit(5000);

            return ParseArch(stdout + Environment.NewLine + stderr);
        }
        catch
        {
            return JavaArch.Unknown;
        }
    }

    /// <summary>从 os.arch 输出里解析架构。</summary>
    public static JavaArch ParseArch(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"os\.arch\s*=\s*(?<arch>[a-zA-Z0-9_]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var value = match.Success ? match.Groups["arch"].Value.ToLowerInvariant() : "";

        return value switch
        {
            "amd64" or "x86_64" or "x64" => JavaArch.X64,
            "x86" or "i386" or "i686" or "x86_32" => JavaArch.X86,
            "aarch64" or "arm64" => JavaArch.Arm64,
            "arm" or "armv7" or "armv7l" or "arm32" => JavaArch.Arm32,
            _ => JavaArch.Unknown
        };
    }

    /// <summary>
    /// 兜底：当前进程的架构（注意这是启动器自己的架构，
    /// 在 x64 机器上跑 32 位 java、或用 Rosetta 转译时可能与目标 java 不一致）。
    /// </summary>
    public static JavaArch CurrentProcess()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        return arch switch
        {
            Architecture.X64 => JavaArch.X64,
            Architecture.X86 => JavaArch.X86,
            Architecture.Arm64 => JavaArch.Arm64,
            Architecture.Arm => JavaArch.Arm32,
            _ => JavaArch.Unknown
        };
    }

    /// <summary>
    /// 推荐架构：优先采用「已有 Java 们」里出现得最多的架构
    /// （说明这台机器上的 Java 生态是这一架构），否则退回进程架构。
    /// 仅作默认值，最终仍由用户确认。
    /// </summary>
    public static JavaArch Suggest(IReadOnlyList<JavaInfo>? installed)
    {
        var known = (installed ?? Array.Empty<JavaInfo>())
            .Where(j => j.IsValid && File.Exists(j.Path))
            .Select(j => Detect(j.Path))
            .Where(a => a != JavaArch.Unknown)
            .ToList();

        if (known.Count > 0)
            return known.GroupBy(a => a).OrderByDescending(g => g.Count()).First().Key;

        return CurrentProcess();
    }

    /// <summary>从 os.arch 字符串反推，给 UI 显示用（如 "aarch64" → ARM64）。</summary>
    public static string Describe(string? osArch) =>
        osArch is null ? L.T("String.Msg.Common.Unknown") : DisplayName(ParseArch("os.arch = " + osArch));
}
