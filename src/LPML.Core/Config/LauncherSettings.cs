using System.Text.Json.Serialization;
using LPML.Core.Auth;
using LPML.Core.Game.Models;
using LPML.Core.Java;
using LPML.Core.Multiplayer;
using LPML.Core.Utils;

namespace LPML.Core.Config;

public enum MirrorKind
{
    /// <summary>Mojang / Fabric / Forge 官方地址，恒等变换。</summary>
    Official = 0,

    /// <summary>BMCLAPI（bangbang93），单一域名 + 路径前缀。</summary>
    BMCLAPI = 1,

    /// <summary>
    /// MCBBS 镜像（download.mcbbs.net）已停止服务，域名不再解析。
    /// 保留枚举值只为能反序列化旧配置，加载后会自动归一化为 BMCLAPI；
    /// 不再出现在界面、预设、测速与故障转移候选中。
    /// </summary>
    MCBBS = 2,

    /// <summary>
    /// 光速镜像 FastMinecraftMirror（fastmcmirror.org）。
    /// 与 BMCLAPI 不同：它按「官方域名 → 专属域名」逐一替换，没有统一的路径前缀。
    /// </summary>
    FastMCMirror = 3
}

/// <summary>可单独指定下载源的资源类别。</summary>
public enum ResourceCategory
{
    VersionManifest,
    VersionJson,
    ClientJar,
    Libraries,
    AssetsIndex,
    Assets,
    Forge,
    NeoForge,
    FabricMeta,
    FabricMaven,
    QuiltMeta,
    OptiFine,
    JavaRuntime,
    AuthlibInjector,
    Modrinth,
    CurseForge,
    Skin
}

public enum ThemeMode
{
    System = 0,
    Light = 1,
    Dark = 2
}

public enum BackdropMode
{
    None = 0,
    Mica = 1,
    Acrylic = 2,
    BlurBehind = 3
}

/// <summary>
/// 界面性能模式。
/// Standard：完整观感（毛玻璃 + 半透明卡片 + 补间动效）。
/// Smooth：流畅优先（不透明遮罩 + 交互反馈瞬时切换 + 只保留最轻量的动画）。
/// </summary>
public enum PerformanceMode
{
    Standard = 0,
    Smooth = 1
}

public enum TempWriteMode
{
    /// <summary>完全不使用系统 TEMP，全部写入根目录缓存。</summary>
    Never = 0,
    /// <summary>小文件用 TEMP，超过阈值走根目录缓存（推荐）。</summary>
    SmallFilesOnly = 1,
    /// <summary>所有临时文件都写 TEMP。</summary>
    Always = 2
}

/// <summary>启动 MC 后启动器自身的行为。默认保持原状。</summary>
public enum AfterLaunchAction
{
    /// <summary>保持原状（默认）：窗口不动，最小化过就继续最小化。</summary>
    KeepOpen = 0,

    /// <summary>最小化到任务栏。</summary>
    Hide = 1,

    /// <summary>直接退出启动器。</summary>
    Exit = 2
}

/// <summary>账户数据（离线账户 + 在线令牌）的存放位置。</summary>
public enum AccountStorageLocation
{
    /// <summary>便携（默认）：&lt;root&gt;\LPML\accounts，跟着启动器走，不写系统盘。</summary>
    Portable = 0,
    /// <summary>本机用户目录：%USERPROFILE%\LPML\accounts，换启动器目录也不丢账户。</summary>
    UserProfile = 1,
    /// <summary>自定义目录。</summary>
    Custom = 2
}

public enum LauncherLanguage
{
    /// <summary>简体中文（语言包 Locales/zh-CN.xaml）。</summary>
    ZhHans = 0,
    /// <summary>English（语言包 Locales/en-US.xaml）。</summary>
    English = 1
}

public sealed class MirrorSettings
{
    /// <summary>全局默认下载源。</summary>
    public MirrorKind Default { get; set; } = MirrorKind.BMCLAPI;

    /// <summary>分类覆盖：null / 不包含该键 表示跟随全局。</summary>
    public Dictionary<ResourceCategory, MirrorKind> Overrides { get; set; } = new();

    /// <summary>自定义镜像根地址（留空则使用内置地址），例如自建 BMCLAPI 节点。</summary>
    public Dictionary<MirrorKind, string> CustomBaseUrls { get; set; } = new();

    /// <summary>启动时自动测速并挑选最快源。</summary>
    public bool AutoSelectFastest { get; set; } = false;

    /// <summary>当前仍可用的下载源。MCBBS 已停运，不参与下拉、预设与测速。</summary>
    public static IReadOnlyList<MirrorKind> SupportedKinds { get; } =
        new[] { MirrorKind.Official, MirrorKind.BMCLAPI, MirrorKind.FastMCMirror };

    /// <summary>
    /// 把旧 launcher.json 里残留的 MCBBS 值归一化为 BMCLAPI。
    /// 返回是否改动过，供调用方决定要不要记日志 / 重新保存。
    /// </summary>
    public bool NormalizeLegacyMirrors()
    {
        var changed = false;
        if (Default == MirrorKind.MCBBS) { Default = MirrorKind.BMCLAPI; changed = true; }
        foreach (var key in Overrides.Where(kv => kv.Value == MirrorKind.MCBBS).Select(kv => kv.Key).ToList())
        {
            Overrides[key] = MirrorKind.BMCLAPI;
            changed = true;
        }
        if (CustomBaseUrls.Remove(MirrorKind.MCBBS)) changed = true;
        return changed;
    }

    public MirrorKind Resolve(ResourceCategory category)
    {
        var kind = Overrides.TryGetValue(category, out var k) ? k : Default;
        // 兜底：即使别处直接改了配置而没走 Normalize，也不会再撞那个死域名
        return kind == MirrorKind.MCBBS ? MirrorKind.BMCLAPI : kind;
    }
}

public sealed class DownloadSettings
{
    /// <summary>同时下载的任务数（多任务）。</summary>
    /// <summary>
    /// 忽略 SSL 证书错误。镜像源证书过期 / 自签名 / 公司代理中间人时会握手失败，
    /// 打开这个开关可以继续下载（安全性降低，但下载的是公开的游戏文件）。
    /// </summary>
    public bool IgnoreCertificateErrors { get; set; } = false;

    /// <summary>把 SSL 握手失败的信息说清楚，而不是只抛"SSL connection could not be established"。</summary>
    public bool FriendlySslErrors { get; set; } = true;

    /// <summary>
    /// 同时下载的文件数。MC 的库和资源几乎全是几十 KB 的小文件，
    /// 每个文件一次请求往返，所以并发数直接决定吞吐——这就是要开高的原因。
    /// 这里不是"每个任务占一个 CPU 线程"：它们是 I/O 等待，线程池会自动复用。
    /// </summary>
    public int MaxConcurrentTasks { get; set; } = 32;

    /// <summary>单个文件内的分段线程数（多线程分段下载）。</summary>
    public int MaxChunksPerFile { get; set; } = 8;

    /// <summary>
    /// 并发"渐进启动"：不要一瞬间把所有连接全部建立起来。
    /// 同时出发几十个 TCP/TLS 握手会让老机器卡顿，也容易被镜像判定为攻击而限流。
    /// 做法是先放一小批，之后每隔 RampUpIntervalMs 再放一个，平滑爬到目标并发。
    /// </summary>
    public bool EnableRampUp { get; set; } = true;

    /// <summary>首批同时放行的数量（这批不做延迟，避免开头完全串行）。</summary>
    public int RampUpStartCount { get; set; } = 4;

    /// <summary>渐进启动的间隔（毫秒）。填 0 则一把全部放行。</summary>
    public int RampUpIntervalMs { get; set; } = 60;

    /// <summary>文件大于该体积（MB）才启用分段下载。</summary>
    public int ChunkThresholdMb { get; set; } = 2;

    /// <summary>服务端不支持 Range 时自动回退单线程。</summary>
    public bool FallbackWhenRangeUnsupported { get; set; } = true;

    /// <summary>
    /// 按文件大小自适应并发（默认开）：
    /// 队列里都是几十 KB 的小文件时把并发拉高（跑满请求数），
    /// 轮到大文件时自动收窄（大文件本身就靠分段下载吃带宽，再多开反而抢带宽、更容易被限流）。
    /// </summary>
    public bool EnableSizeAwareConcurrency { get; set; } = true;

    /// <summary>小文件阶段的并发上限（1~128）。</summary>
    public int SmallFileConcurrency { get; set; } = 48;

    /// <summary>大文件阶段的并发下限（1~128）。小于上限时自动生效。</summary>
    public int LargeFileConcurrency { get; set; } = 8;

    /// <summary>超过这个体积（MB）算"大文件"，并发往 LargeFileConcurrency 收。</summary>
    public int LargeFileThresholdMb { get; set; } = 5;

    /// <summary>
    /// 额外的库扫描目录。填别的启动器的游戏目录（官方 .minecraft、HMCL、PCL 等），
    /// 里面已经下好的库会被复用，装新版本时不用重下。只读取，不修改这些目录。
    /// </summary>
    public List<string?>? ExtraLibraryDirectories { get; set; }

    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// 一轮下载结束后，把仍然失败的文件整体重新排队，不再需要逐个点重试。
    /// 与 RetryCount（单个文件失败后的即时重试）互不影响。
    /// </summary>
    public bool EnableAutoRetryRound { get; set; } = true;

    /// <summary>最多自动重下几轮；0 表示不自动重下。</summary>
    public int AutoRetryRounds { get; set; } = 3;

    /// <summary>每轮自动重下之前等待的秒数。</summary>
    public int AutoRetryDelaySeconds { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>限速，单位 KB/s，0 表示不限。</summary>
    public int SpeedLimitKb { get; set; } = 0;

    public bool VerifySha1 { get; set; } = true;
    public bool VerifyFileSize { get; set; } = true;
    public bool SkipExistingFiles { get; set; } = true;

    /// <summary>下载完成后是否删除分片临时文件。</summary>
    public bool CleanChunkFiles { get; set; } = true;

    /// <summary>HTTP 用户代理，部分镜像对 UA 有要求。</summary>
    public string UserAgent { get; set; } = "LPML/0.0.001";

    /// <summary>代理设置，留空则跟随系统。</summary>
    public bool UseProxy { get; set; } = false;
    public string ProxyAddress { get; set; } = "127.0.0.1:7890";
}

public sealed class TempSettings
{
    /// <summary>是否允许写入系统 TEMP。</summary>
    public TempWriteMode Mode { get; set; } = TempWriteMode.Never;

    /// <summary>退出时清理 TEMP 目录。</summary>
    public bool ClearOnExit { get; set; } = true;

    /// <summary>TEMP 写入量上限（MB），超出后自动回落到根目录缓存；0 表示不限制。</summary>
    public long MaxTempSizeMb { get; set; } = 512;

    /// <summary>写入 TEMP 的单文件上限（MB）。</summary>
    public long SingleFileTempLimitMb { get; set; } = 64;

    /// <summary>退出时同时清理根目录 temp 缓存。</summary>
    public bool ClearLocalTempOnExit { get; set; } = true;

    /// <summary>本地缓存目录容量上限（MB），0 表示不限制，超限时清理最旧文件。</summary>
    public long MaxCacheSizeMb { get; set; } = 0;
}

public sealed class PathSettings
{
    /// <summary>游戏目录，默认 <root>\.minecraft。</summary>
    public string GameDirectory { get; set; } = ".minecraft";

    /// <summary>下载缓存目录，默认 <root>\LPML\cache。</summary>
    public string CacheDirectory { get; set; } = "LPML/cache";

    /// <summary>是否把 launcher_profiles.json 等官方文件也写入游戏目录（保持与外部启动器互通）。</summary>
    public bool WriteOfficialProfiles { get; set; } = true;

    /// <summary>实例隔离：每个实例拥有独立的 mods / saves / config。</summary>
    /// <summary>
    /// 版本隔离：每个实例用自己的目录（mods / saves / config 互不干扰）。
    /// 默认开启——大多数人都希望实例之间互相独立，而不是共用一套模组和存档。
    /// </summary>
    public bool InstanceIsolation { get; set; } = false;

    /// <summary>
    /// 全局默认隔离模式（实例可在「实例设置」里单独覆盖）。
    /// UserData：只隔离 mods/saves/config，版本文件在 .minecraft（省空间）。
    /// Full：版本文件也进实例目录（占空间，整个目录拷走即迁移）。
    /// </summary>
    public LPML.Core.Instances.IsolationMode IsolationMode { get; set; }
        = LPML.Core.Instances.IsolationMode.UserData;

    /// <summary>资源（assets）与依赖库在所有实例间共享。</summary>
    public bool ShareLibrariesAndAssets { get; set; } = true;
}

public sealed class JavaSettings
{
    public string? JavaPath { get; set; }
    public bool AutoDetectJava { get; set; } = true;
    public bool AutoDownloadJavaRuntime { get; set; } = true;

    /// <summary>多 Java 的选择策略：自动挑最合适的 / 固定用某一个。</summary>
    public JavaSelectionMode SelectionMode { get; set; } = JavaSelectionMode.Auto;

    /// <summary>手动模式下固定使用的 Java（JavaManager 列表里的 Id）。</summary>
    public string? PreferredJavaId { get; set; }

    /// <summary>同一版本同时有 JDK 与 JRE 时的偏好，默认用 JDK。</summary>
    public JavaRuntimePreference RuntimePreference { get; set; } = JavaRuntimePreference.PreferJdk;

    /// <summary>Java 启动失败后的策略：换一个重试 / 直接抛错。</summary>
    public JavaFailureAction OnFailure { get; set; } = JavaFailureAction.RetryWithAnother;

    /// <summary>失败重试时最多试几个 Java（含首次）。</summary>
    public int MaxJavaRetries { get; set; } = 3;

    /// <summary>自动内存：按物理内存比例分配。</summary>
    public bool AutoMemory { get; set; } = true;

    /// <summary>自动内存时使用的物理内存比例（0.05 ~ 0.9）。</summary>
    public double AutoMemoryRatio { get; set; } = 0.35;

    public int MinMemoryMb { get; set; } = 512;
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>额外 JVM 参数，例如 -XX:+UseG1GC</summary>
    public string JvmArguments { get; set; } =
        "-XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1NewSizePercent=20 -XX:G1ReservePercent=20 -XX:MaxGCPauseMillis=50 -XX:G1HeapRegionSize=32M -Dfile.encoding=UTF-8";

    /// <summary>环境变量，一行一个 KEY=VALUE。</summary>
    public string EnvironmentVariables { get; set; } = "";

    public bool JavaAgentAuthlibInjector { get; set; } = true;
    public string AuthlibInjectorPath { get; set; } = "";

    /// <summary>进程优先级</summary>
    public string ProcessPriority { get; set; } = "Normal";
}

public sealed class LaunchSettings
{
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool Fullscreen { get; set; } = false;
    public bool HideLauncherWindow { get; set; } = false;
    public AfterLaunchAction AfterLaunch { get; set; } = AfterLaunchAction.KeepOpen;
    public bool ShowGameOutput { get; set; } = true;
    public bool AutoCloseOutputWhenGameExit { get; set; } = false;

    /// <summary>额外游戏参数，例如 --demo</summary>
    public string GameArguments { get; set; } = "";

    public bool UseCustomWindowTitle { get; set; } = false;
    public string CustomWindowTitle { get; set; } = "Minecraft - LPML";

    public bool CheckGameCompleteBeforeLaunch { get; set; } = true;
    public bool AutoDownloadMissingFiles { get; set; } = true;

    /// <summary>启动前自动检查版本完整性并修复。</summary>
    public bool RepairBeforeLaunch { get; set; } = false;

    public bool LauncherVisibleOnGameStart { get; set; } = false;

    /// <summary>服务器直连，形如 mc.example.com:25565，留空禁用。</summary>
    public string QuickJoinServer { get; set; } = "";

    /// <summary>世界直连（单人），留空禁用。</summary>
    public string QuickJoinWorld { get; set; } = "";

    public bool EnableCheats { get; set; } = false;

    /// <summary>自定义 JVM 可执行文件路径（覆盖 JavaSettings.JavaPath）。</summary>
    public string? OverrideJavaPath { get; set; }

    public bool UseIsolatedNativesDirectory { get; set; } = true;
}

public sealed class AppearanceSettings
{
    /// <summary>启动器窗口标题；留空则用默认的 "LPML"。</summary>
    public string LauncherTitle { get; set; } = "LPML";

    /// <summary>
    /// 性能模式。Smooth 会关掉毛玻璃与半透明合成、把交互反馈改成瞬时切换，
    /// 适合低性能设备或核显机型。存进 launcher.json，下次启动保持。
    /// </summary>
    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Standard;

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public BackdropMode Backdrop { get; set; } = BackdropMode.Acrylic;

    /// <summary>毛玻璃不透明度 0.1 ~ 1.0</summary>
    public double BackdropOpacity { get; set; } = 0.72;

    /// <summary>背景模糊强度（自定义绘制的背景图模糊半径）</summary>
    public double BackgroundBlurRadius { get; set; } = 24;

    public string AccentColor { get; set; } = "#7C6BFF";
    public string BackgroundImage { get; set; } = "";

    /// <summary>背景图不透明度 0.05 ~ 1.0（默认 0.35，太亮会盖住内容）。</summary>
    public double BackgroundOpacity { get; set; } = 0.6;

    /// <summary>背景图填充方式：UniformToFill（裁切铺满）/ Uniform（完整显示）/ None（原始尺寸）。</summary>
    public string BackgroundStretch { get; set; } = "UniformToFill";

    public bool EnableAnimations { get; set; } = true;

    /// <summary>动效时长（毫秒）</summary>
    public int AnimationDurationMs { get; set; } = 260;

    /// <summary>卡片拉伸 / 悬浮缩放动效强度（0 关闭，0.02 ~ 0.08 舒适）</summary>
    public double HoverScaleAmount { get; set; } = 0.03;

    public bool EnableStretchAnimation { get; set; } = true;
    public double CornerRadius { get; set; } = 14;
    public double FontScale { get; set; } = 1.0;
    public bool ShowBackgroundParticles { get; set; } = true;
}

/// <summary>
/// 隐私与日志监听。关闭「日志监听」后，调试模式不再弹出日志窗口，
/// 端口提示与崩溃提示一并停用，等同于普通启动。
/// </summary>
public sealed class PrivacySettings
{
    /// <summary>总开关：是否监听游戏输出（日志窗口 / 端口提示 / 崩溃提示都依赖它）。</summary>
    public bool EnableLogMonitoring { get; set; } = true;

    /// <summary>调试模式：启动游戏时附带日志窗口。</summary>
    public bool LaunchWithLogWindow { get; set; } = true;

    /// <summary>监听到端口开放时弹窗提示（局域网联机 / 独立服务器）。</summary>
    public bool NotifyServerPort { get; set; } = true;

    /// <summary>游戏异常退出时弹窗提示，并可一键打开日志文件夹。</summary>
    public bool NotifyOnCrash { get; set; } = true;

    /// <summary>把游戏输出落盘到实例日志目录，便于事后排查。</summary>
    public bool SaveGameLogToFile { get; set; } = true;

    /// <summary>崩溃弹出前是否总是显示日志窗口（便于立刻看到现场）。</summary>
    public bool ShowLogWindowOnCrash { get; set; } = true;
}

public sealed class AdvancedSettings
{
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public bool CheckUpdateOnStartup { get; set; } = true;
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>是否允许启动器向系统发送通知（仅本地通知，无遥测）。</summary>
    public bool EnableNotification { get; set; } = true;

    /// <summary>无遥测承诺：启动器不上传任何数据。</summary>
    public bool DisableTelemetry { get; set; } = true;

    public bool DeveloperMode { get; set; } = false;
    public bool ShowAdvancedOptions { get; set; } = false;

    /// <summary>启动时自动刷新账户令牌。</summary>
    public bool AutoRefreshAccount { get; set; } = true;

    public int MaxLogFiles { get; set; } = 5;

    /// <summary>启用独立日志守护进程（子进程）。关闭后日志仍由启动器自己写。</summary>
    public bool EnableGuardProcess { get; set; } = true;

    /// <summary>启动器多久没有 UI 心跳算未响应（秒，5~300）。</summary>
    public int GuardTimeoutSeconds { get; set; } = 20;

    /// <summary>未响应时直接强制关闭，不弹确认框。</summary>
    public bool GuardAutoKill { get; set; } = false;
}

public sealed class AccountSettings
{
    /// <summary>微软登录使用的 Azure 应用 Client ID（设备代码流，需开启公共客户端流）。</summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>LittleSkin / 外置登录服务器地址。</summary>
    public string LittleSkinServer { get; set; } = "https://littleskin.cn/api/yggdrasil";

    /// <summary>已保存的外置登录服务器列表。</summary>
    public List<string> SavedAuthServers { get; set; } = new();

    /// <summary>启动时自动刷新令牌。</summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary>外置登录自动注入 authlib-injector。</summary>
    public bool AutoInjectAuthlibInjector { get; set; } = true;

    /// <summary>authlib-injector jar 路径（留空则自动下载到 cache）。</summary>
    public string AuthlibInjectorPath { get; set; } = "";

    /// <summary>
    /// 新账户的默认存放位置。默认 Portable（写在启动器目录内，保持全绿色）。
    /// 每个账户之后还能单独改位置，互不影响。
    /// </summary>
    public AccountStorageLocation StorageLocation { get; set; } = AccountStorageLocation.Portable;

    /// <summary>新账户使用 Custom 位置时的默认目录；留空回退到 Portable。</summary>
    public string StorageCustomDirectory { get; set; } = "";

    /// <summary>
    /// 已手动导入的自定义目录。自定义目录必须先导入才能使用（加载其中账户 / 作为存放目标）。
    /// </summary>
    public List<string> ImportedDirectories { get; set; } = new();

    /// <summary>在线登录时自动用默认浏览器打开认证页面（设备代码流）。</summary>
    public bool AutoOpenBrowser { get; set; } = true;
}

public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;
    public LauncherLanguage Language { get; set; } = LauncherLanguage.ZhHans;
    public string? SelectedAccountId { get; set; }
    public string? SelectedInstanceId { get; set; }

    /// <summary>
    /// 当前浏览的实例文件夹 id（null / 空 = 默认 .minecraft）。
    /// 文件夹决定哪些实例可见、名称在哪一档里查重，而不是反过来由实例决定文件夹。
    /// </summary>
    public string? CurrentFolderId { get; set; }

    /// <summary>
    /// 已接受的用户协议版本。0 表示尚未同意，首次启动会显示协议窗口；
    /// 协议版本提升后也会重新显示。只存本地 launcher.json，不上传。
    /// </summary>
    public int AcceptedAgreementVersion { get; set; } = 0;

    /// <summary>联机核心设置（随启动器一起发布到 LPML\\tools\\terracotta，地址由用户填写）。</summary>
    public TerracottaSettings Terracotta { get; set; } = new();

    public AccountSettings Account { get; set; } = new();

    public MirrorSettings Mirror { get; set; } = new();
    public DownloadSettings Download { get; set; } = new();
    public TempSettings Temp { get; set; } = new();
    public PathSettings Paths { get; set; } = new();
    public JavaSettings Java { get; set; } = new();
    public LaunchSettings Launch { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();

    [JsonIgnore]
    public bool IsFirstRun { get; set; }

    public LauncherSettings Clone()
        => Json.Deserialize<LauncherSettings>(Json.Serialize(this)) ?? new LauncherSettings();
}
