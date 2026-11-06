using LPML.Core.Java;

namespace LPML.Core.Launch;

public sealed class LaunchOptions
{
    public string VersionId { get; set; } = "";

    /// <summary>实例独立目录（游戏运行目录），默认与游戏目录一致。</summary>
    public string WorkingDirectory { get; set; } = "";

    public string? JavaPath { get; set; }
    public int MinMemoryMb { get; set; } = 512;
    public int MaxMemoryMb { get; set; } = 4096;
    public string ExtraJvmArguments { get; set; } = "";
    public string ExtraGameArguments { get; set; } = "";
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public string? ServerAddress { get; set; }
    public string? WorldName { get; set; }
    public string? CustomWindowTitle { get; set; }
    public bool EnableCheats { get; set; }
    public string? AuthlibInjectorPath { get; set; }
    public string? AuthServerUrl { get; set; }
    public string? AuthPrefetched { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>联机：启动后自动直连的地址（房间页写入，启动参数合并时追加）。</summary>
    public string? AutoJoinAddress { get; set; }
    public string ProcessPriority { get; set; } = "Normal";
    public bool UseIsolatedNativesDirectory { get; set; } = true;
    public bool ShowConsole { get; set; } = true;
}

public sealed class LaunchResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string ArgumentsPreview { get; set; } = "";
    public int ProcessId { get; set; }
    public string JavaPath { get; set; } = "";

    /// <summary>本次启动实际使用的 Java（含版本与 JDK/JRE 类型），失败重试时会变。</summary>
    public JavaInfo? UsedJava { get; set; }

    /// <summary>因首次失败而自动换 Java 重试的次数；0 表示一次成功。</summary>
    public int JavaRetryCount { get; set; }

    /// <summary>重试过程中被弃用的 Java（供日志与提示展示）。</summary>
    public List<string> FailedJavaPaths { get; set; } = new();
}
