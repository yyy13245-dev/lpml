using System.Diagnostics;

namespace LPML.Core.Java;

/// <summary>Java 探测：环境变量、常见安装目录、游戏目录 runtime、PATH。全程零注册表依赖。</summary>
public static class JavaLocator
{
    public static IReadOnlyList<JavaInfo> DetectAll(IEnumerable<string>? extraDirectories = null)
    {
        var found = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? exePath, JavaSource source)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return;
            string full;
            try { full = Path.GetFullPath(exePath); } catch { return; }
            if (!File.Exists(full)) return;
            if (found.ContainsKey(full)) return;
            var info = Probe(full);
            if (info is null) return;
            info.Source = source;
            info.JavaHome = DeriveJavaHome(full);
            info.Id = ComputeId(full);
            found[full] = info;
        }

        // JAVA_HOME
        TryAdd(CombineJava(Environment.GetEnvironmentVariable("JAVA_HOME")), JavaSource.Environment);

        // PATH
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in ExeNames)
                try { TryAdd(Path.Combine(dir, name), JavaSource.Environment); } catch { }
        }

        // 常见安装目录
        var roots = new List<string>();
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "LOCALAPPDATA" })
        {
            var value = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(value)) roots.Add(value);
        }
        var vendors = new[]
        {
            "Java", "Eclipse Adoptium", "AdoptOpenJDK", "BellSoft", "Zulu", "azul", "Microsoft",
            "GraalVM", "Amazon Corretto", "Oracle", "Semeru", "OpenLogic", "JetBrains", "Tencent", "Dragonwell"
        };
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var vendor in vendors)
            {
                var vendorDir = Path.Combine(root, vendor);
                if (!Directory.Exists(vendorDir)) continue;
                foreach (var jdkDir in SafeEnumerateDirectories(vendorDir))
                {
                    foreach (var name in ExeNames)
                        TryAdd(Path.Combine(jdkDir, "bin", name), JavaSource.Scan);
                }
            }
        }

        // 用户额外指定 / 游戏目录 runtime。
        // extraDirectories 里可能直接是一个可执行文件路径（设置页「Java 路径」输入框）：
        // 旧代码一律当作目录再拼 bin/javaw.exe，导致手动填写的路径永远探测不到。
        foreach (var dir in extraDirectories ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (File.Exists(dir))
            {
                TryAdd(dir, JavaSource.Manual);
                continue;
            }
            if (!Directory.Exists(dir)) continue;
            foreach (var name in ExeNames)
            {
                TryAdd(Path.Combine(dir, "bin", name), JavaSource.Manual);
                TryAdd(Path.Combine(dir, name), JavaSource.Manual);
            }
            foreach (var sub in SafeEnumerateDirectories(dir))
            {
                foreach (var name in ExeNames)
                {
                    TryAdd(Path.Combine(sub, "bin", name), JavaSource.GameRuntime);
                    TryAdd(Path.Combine(sub, "runtime", "bin", name), JavaSource.GameRuntime);
                }
            }
        }

        return found.Values.OrderByDescending(x => x.MajorVersion).ThenBy(x => x.Path).ToList();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>当前平台首选的 java 可执行文件名（Windows 优先 javaw.exe，无控制台窗口）。</summary>
    public static string ExeName => OperatingSystem.IsWindows() ? "javaw.exe" : "java";

    /// <summary>
    /// 扫描时一并尝试的可执行文件名。不再按平台二选一：Wine 下 .NET 仍判定为 Windows，
    /// 但用户可能使用不带 .exe 后缀的 java / javaw。不存在的名字由 File.Exists 直接跳过。
    /// </summary>
    public static string[] ExeNames => new[] { "javaw.exe", "java.exe", "javaw", "java" };

    private static string CombineJava(string? javaHome)
    {
        if (string.IsNullOrWhiteSpace(javaHome)) return "";
        var bin = Path.Combine(javaHome, "bin");
        foreach (var name in ExeNames)
        {
            var candidate = Path.Combine(bin, name);
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(bin, ExeName);
    }

    /// <summary>
    /// 探测单个可执行文件，并填充 Id / JavaHome。失败返回 null。
    /// </summary>
    public static JavaInfo? ProbeOne(string exePath, JavaSource source = JavaSource.Manual)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        string full;
        try { full = System.IO.Path.GetFullPath(exePath); } catch { return null; }
        if (!File.Exists(full)) return null;

        var info = Probe(full);
        if (info is null) return null;
        info.Source = source;
        info.JavaHome = DeriveJavaHome(full);
        info.Id = ComputeId(full);
        info.IsValid = true;
        return info;
    }

    /// <summary>
    /// 「自动同步整个 Java」：从用户选中的可执行文件出发，
    /// 把它的 JDK 根目录、父目录下的兄弟 JDK、以及 PATH 里的 Java 一网打尽。
    /// </summary>
    public static List<JavaInfo> ProbeWithSiblings(string exePath)
    {
        var result = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(JavaInfo? info)
        {
            if (info is null) return;
            result.TryAdd(info.Path, info);
        }

        // 1) 用户选中的那个
        var picked = ProbeOne(exePath, JavaSource.Manual);
        Add(picked);
        if (picked is null) return result.Values.ToList();

        // 2) 同一个 JDK 根里的其他可执行文件（含不带 .exe 后缀的 java / javaw）
        if (!string.IsNullOrWhiteSpace(picked.JavaHome))
        {
            var bin = System.IO.Path.Combine(picked.JavaHome, "bin");
            foreach (var name in ExeNames)
            {
                try { Add(ProbeOne(System.IO.Path.Combine(bin, name), JavaSource.Manual)); } catch { }
            }
        }

        // 3) 兄弟 JDK：往上找 2~3 层，把同级目录下所有 JDK 都纳入
        var home = picked.JavaHome;
        if (!string.IsNullOrWhiteSpace(home))
        {
            for (var level = 0; level < 3; level++)
            {
                var parent = System.IO.Path.GetDirectoryName(home);
                if (string.IsNullOrWhiteSpace(parent)) break;
                home = parent;
                foreach (var sibling in SafeEnumerateDirectories(home))
                {
                    foreach (var name in ExeNames)
                    {
                        var candidate = System.IO.Path.Combine(sibling, "bin", name);
                        if (File.Exists(candidate)) Add(ProbeOne(candidate, JavaSource.Scan));

                        // macOS 的 JDK 结构是 Contents/Home/bin/java
                        var mac = System.IO.Path.Combine(sibling, "Contents", "Home", "bin", name);
                        if (File.Exists(mac)) Add(ProbeOne(mac, JavaSource.Scan));
                    }
                }
            }
        }

        return result.Values.ToList();
    }

    /// <summary>只扫描 PATH 与 JAVA_HOME（不含常见安装目录的深扫），用于「刷新系统 Java」。</summary>
    public static List<JavaInfo> DetectFromEnvironment()
    {
        var found = new Dictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return;
            var info = ProbeOne(exePath, JavaSource.Environment);
            if (info is not null) found.TryAdd(info.Path, info);
        }

        TryAdd(CombineJava(Environment.GetEnvironmentVariable("JAVA_HOME")));

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVariable.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in ExeNames)
            {
                try { TryAdd(System.IO.Path.Combine(dir, name)); } catch { }
            }
        }

        return found.Values.OrderByDescending(x => x.MajorVersion).ThenBy(x => x.Path).ToList();
    }

    /// <summary>从可执行文件反推 JDK/JRE 根目录：.../jdk-21/bin/javaw.exe → .../jdk-21。</summary>
    public static string DeriveJavaHome(string exePath)
    {
        try
        {
            var bin = System.IO.Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(bin)) return "";
            // macOS 可能位于 Contents/Home/bin
            var home = System.IO.Path.GetDirectoryName(bin);
            if (string.IsNullOrWhiteSpace(home)) return bin!;
            if (home!.EndsWith("Home", StringComparison.OrdinalIgnoreCase))
            {
                var contents = System.IO.Path.GetDirectoryName(home);
                if (!string.IsNullOrWhiteSpace(contents) &&
                    contents!.EndsWith("Contents", StringComparison.OrdinalIgnoreCase))
                    return System.IO.Path.GetDirectoryName(contents) ?? home;
            }
            return home!;
        }
        catch { return ""; }
    }

    /// <summary>路径 → 稳定 Id（小写 SHA1 前 16 位），跨会话保持不变。</summary>
    public static string ComputeId(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant());
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>运行 java -version 获取真实版本。</summary>
    public static JavaInfo? Probe(string exePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(exePath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var error = process.StandardError.ReadToEndAsync().GetAwaiter().GetResult();
            var output = process.StandardOutput.ReadToEndAsync().GetAwaiter().GetResult();
            process.WaitForExit(5000);
            var text = string.IsNullOrWhiteSpace(error) ? output : error;
            var major = ParseMajor(text);
            var vendor = ParseVendor(text);
            return new JavaInfo
            {
                Path = exePath,
                Version = text.Split('\n')[0].Trim(),
                MajorVersion = major,
                Is64Bit = text.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) || !text.Contains("32-Bit", StringComparison.OrdinalIgnoreCase),
                Vendor = vendor,
                IsJdk = DetectIsJdk(exePath, text)
            };
        }
        catch
        {
            return null;
        }
    }

    private static int ParseMajor(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, "\"?(?<major>\\d+)(?:\\.\\d+)?");
        if (!match.Success) return 0;
        var major = int.Parse(match.Groups["major"].Value);
        if (major == 1)
        {
            var m2 = System.Text.RegularExpressions.Regex.Match(text, "1\\.(?<v>\\d+)");
            if (m2.Success) major = int.Parse(m2.Groups["v"].Value);
        }
        return major;
    }

    /// <summary>从 java -version 输出里识别发布者。按特异性从高到低匹配。</summary>
    private static string ParseVendor(string text)
    {
        var checks = new (string Keyword, string Vendor)[]
        {
            ("GraalVM", "GraalVM"),
            ("Zulu", "Azul Zulu"),
            ("Azul", "Azul"),
            ("Dragonwell", "Alibaba Dragonwell"),
            ("Tencent", "Tencent Kona"),
            ("Kona", "Tencent Kona"),
            ("Corretto", "Amazon Corretto"),
            ("Semeru", "IBM Semeru"),
            ("OpenJ9", "Eclipse OpenJ9"),
            ("JetBrains", "JetBrains Runtime"),
            ("BellSoft", "BellSoft Liberica"),
            ("Liberica", "BellSoft Liberica"),
            ("Microsoft", "Microsoft OpenJDK"),
            ("Bisheng", "Huawei BiSheng"),
            ("Temurin", "Eclipse Temurin"),
            ("Adoptium", "Eclipse Temurin"),
            ("AdoptOpenJDK", "AdoptOpenJDK"),
            ("Oracle", "Oracle"),
            ("OpenJDK", "OpenJDK"),
            ("HotSpot", "OpenJDK HotSpot")
        };
        foreach (var (keyword, vendor) in checks)
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return vendor;
        return L.T("String.Msg.Common.Unknown");
    }

    /// <summary>完整的 java -version 首行（如 openjdk version "21.0.3" 2024-04-16）。</summary>
    public static string VersionLine(string text) => text.Split('\n')[0].Trim();

    /// <summary>
    /// 判定是 JDK 还是 JRE。判定顺序：
    /// 1) bin 目录下有编译器（javac / javac.exe）→ JDK
    /// 2) java -version 输出含 "Runtime Environment" 且目录名带 jre → JRE
    /// 3) 路径含 "jre" 段且无编译器 → JRE
    /// 4) 其余（Java 9+ 已不再单独发行 JRE）→ 视为 JDK
    /// </summary>
    public static bool DetectIsJdk(string exePath, string versionText)
    {
        try
        {
            var bin = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(bin))
            {
                // 编译器存在即 JDK（Windows javac.exe，其他 javac）
                foreach (var name in new[] { "javac.exe", "javac" })
                {
                    try
                    {
                        if (File.Exists(Path.Combine(bin!, name))) return true;
                    }
                    catch { }
                }

                // 上层目录名里带 jre/jre8/jre1.8.0 → JRE
                var home = DeriveJavaHome(exePath);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    var dirName = Path.GetFileName(home!.TrimEnd(Path.DirectorySeparatorChar));
                    if (dirName.StartsWith("jre", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }

            // 输出特征：Server VM 通常出现在 JRE 上
            if (versionText.Contains("Server VM", StringComparison.OrdinalIgnoreCase) &&
                versionText.Contains("JRE", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch { }

        // Java 9 起官方不再发行独立 JRE，默认按 JDK 处理
        return true;
    }
}
