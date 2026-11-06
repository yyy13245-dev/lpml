using LPML.Core.Utils;

namespace LPML.Core.Instances;

/// <summary>
/// 游戏实例。开启隔离后，实例拥有独立的 mods / saves / config / resourcepacks，
/// 但共享 versions / libraries / assets（与 HMCL / PCL 的做法一致）。
/// </summary>
public sealed class GameInstance : ObservableObject
{
    private string _name = "";
    private string _versionId = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string VersionId { get => _versionId; set => Set(ref _versionId, value); }

    /// <summary>兜底：万一某处 DisplayMemberPath 没生效，也别显示类名。</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? VersionId : Name;
    public string? Notes { get; set; }
    public string? IconPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset LastPlayed { get; set; } = DateTimeOffset.Now;
    public long PlayCount { get; set; }
    public double TotalPlayMinutes { get; set; }

    /// <summary>版本隔离：实例使用独立目录，不共用 .minecraft 的 mods / saves 等。</summary>
    public bool Isolated { get; set; } = true;

    /// <summary>
    /// 该实例的隔离模式。为 null 时跟随全局设置（设置 → 路径 → 隔离模式）。
    /// 用户数据隔离：只隔离 mods/saves/config，版本文件仍在 .minecraft（省空间）。
    /// 完整隔离：版本文件也进实例目录（占空间，但整个目录拷走即可迁移）。
    /// </summary>
    public IsolationMode? IsolationModeOverride { get; set; }

    /// <summary>该实例实际生效的隔离模式（实例级 > 全局）。</summary>
    public IsolationMode IsolationModeEffective(IsolationMode global)
    {
        var mode = IsolationModeOverride ?? global;
        // 指定了自己的目录（实例文件夹 / 直接指定）时，那本来就是完整独立的一套
        if (!string.IsNullOrWhiteSpace(DirectoryOverride)) return IsolationMode.Full;
        return mode;
    }

    /// <summary>所属实例文件夹 Id；null 表示用默认游戏目录（.minecraft）。</summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// 直接指定的实例目录（相对启动器根目录或绝对路径）。
    /// 优先级高于 FolderId：填了就以它为准。
    /// </summary>
    public string? DirectoryOverride { get; set; }

    /// <summary>隔离开启时，这些目录仍与主游戏目录共用（勾选即共享）。</summary>
    public bool ShareMods { get; set; } = false;
    public bool ShareSaves { get; set; } = false;
    public bool ShareConfig { get; set; } = false;
    public bool ShareResourcePacks { get; set; } = false;
    public bool ShareShaderPacks { get; set; } = false;

    /// <summary>实例级日志监听开关；null 表示跟随「设置 → 隐私」的全局开关。</summary>
    public bool? MonitorLog { get; set; }

    /// <summary>实际是否监听日志（实例优先，其次全局）。</summary>
    public bool MonitorLogEffective(bool globalEnabled) => MonitorLog ?? globalEnabled;
    public string? JavaPathOverride { get; set; }
    public int? MaxMemoryOverrideMb { get; set; }
    public int? MinMemoryOverrideMb { get; set; }

    /// <summary>实例级「自动内存」开关；null 表示跟随全局设置。</summary>
    public bool? AutoMemoryOverride { get; set; }
    public string? JvmArgumentOverride { get; set; }
    public string? GameArgumentOverride { get; set; }
    public bool? FullscreenOverride { get; set; }
    public int? WindowWidthOverride { get; set; }
    public int? WindowHeightOverride { get; set; }
    public string? ServerOverride { get; set; }
    public string? LoaderName { get; set; }
    public string? LoaderVersion { get; set; }

    // ── 实例级覆盖：null = 跟随全局设置 ──────────────────────

    /// <summary>窗口标题；null 跟随全局。</summary>
    public string? WindowTitleOverride { get; set; }

    /// <summary>是否启用自定义窗口标题。</summary>
    public bool? UseCustomWindowTitleOverride { get; set; }

    /// <summary>进程优先级；null 跟随全局。</summary>
    public string? ProcessPriorityOverride { get; set; }

    /// <summary>启动后启动器行为；null 跟随全局。取值同 AfterLaunchAction。</summary>
    public int? AfterLaunchOverride { get; set; }

    /// <summary>启动前执行的命令，支持 {path} {verpath} {java} {name} {version} 占位符。</summary>
    public string? PreLaunchCommand { get; set; }

    /// <summary>环境变量覆盖（每行 KEY=VALUE）；null 跟随全局。</summary>
    public string? EnvironmentVariablesOverride { get; set; }

    /// <summary>本次实例最近一次启动结果摘要，用于诊断卡片。</summary>
    public string? LastLaunchSummary { get; set; }
    public DateTimeOffset? LastLaunchAt { get; set; }
    public bool LastLaunchSuccess { get; set; }

    /// <summary>把启动前命令里的占位符展开成实际路径。</summary>
    public string ExpandPreLaunchCommand(string instanceDirectory, string gameDirectory, string? javaPath)
    {
        if (string.IsNullOrWhiteSpace(PreLaunchCommand)) return "";
        var map = new Dictionary<string, string>
        {
            ["{path}"] = instanceDirectory,
            ["{verpath}"] = gameDirectory,
            ["{java}"] = javaPath ?? "",
            ["{name}"] = Name,
            ["{version}"] = VersionId,
            ["{id}"] = Id
        };
        var text = PreLaunchCommand;
        foreach (var (key, value) in map) text = text.Replace(key, value);
        return text;
    }

    public string LoadersText => string.IsNullOrEmpty(LoaderName) ? L.T("String.Core.Item.Vanilla") : $"{LoaderName} {LoaderVersion}";

    public string PlayTimeText => TotalPlayMinutes >= 60
        ? L.F("String.Core.Item.Hours", TotalPlayMinutes / 60)
        : L.F("String.Core.Item.Minutes", TotalPlayMinutes);
}
