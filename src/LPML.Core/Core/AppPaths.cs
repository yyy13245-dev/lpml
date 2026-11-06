using System.Reflection;
using LPML.Core.Utils;

namespace LPML.Core;

/// <summary>
/// LPML 是全绿色（Portable）启动器：
/// 1. 根目录 = 程序自身所在目录（U 盘 / 任意文件夹均可）；
/// 2. 游戏目录默认 <root>\.minecraft，结构与官方启动器一致（versions / libraries / assets / saves ...）；
/// 3. 下载缓存默认 <root>\cache；
/// 4. 临时目录默认不写入系统 TEMP，只有用户显式开启时才写入，且退出时清理、可设容量上限。
/// </summary>
public sealed class AppPaths
{
    public string RootDirectory { get; private set; }

    /// <summary>启动器自身的目录：&lt;root&gt;\LPML，除 exe 及其必需配套文件外的一切都放这里。</summary>
    public string AppDirectory { get; private set; }

    /// <summary>游戏目录：&lt;root&gt;\.minecraft，保持官方结构，PCL / HMCL / 官方启动器通用。</summary>
    private string? _gameDirectoryOverride;

    /// <summary>
    /// 游戏目录。存在临时覆盖时用覆盖值——用于「把文件下载到指定实例文件夹」，
    /// 下载期间临时切换，结束后恢复（见 WithGameDirectory）。
    /// </summary>
    public string GameDirectory => _gameDirectoryOverride ?? _gameDirectory;

    private string _gameDirectory = "";

    /// <summary>
    /// 在指定游戏目录下执行操作（下载 / 安装），结束后自动恢复。
    /// 传 null 或空表示不切换。
    /// </summary>
    /// <summary>开始临时覆盖（异步场景用），返回旧值供 EndGameDirectory 恢复。</summary>
    public string? BeginGameDirectory(string? directory)
    {
        var previous = _gameDirectoryOverride;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _gameDirectoryOverride = Expand(directory);
            FileHelper.EnsureDirectory(_gameDirectoryOverride);
        }
        return previous;
    }

    /// <summary>结束临时覆盖。</summary>
    public void EndGameDirectory(string? previous) => _gameDirectoryOverride = previous;

    public void WithGameDirectory(string? directory, Action action)
    {
        if (string.IsNullOrWhiteSpace(directory)) { action(); return; }
        var previous = _gameDirectoryOverride;
        try
        {
            _gameDirectoryOverride = Expand(directory);
            FileHelper.EnsureDirectory(_gameDirectoryOverride);
            action();
        }
        finally { _gameDirectoryOverride = previous; }
    }
    public string CacheDirectory { get; private set; }
    public string TempDirectory { get; private set; }
    public string ConfigDirectory { get; private set; }
    public string LogDirectory { get; private set; }

    /// <summary>
    /// 账户数据目录。默认 <root>\LPML\accounts（便携）；
    /// 用户可改到 %USERPROFILE%\LPML\accounts 或自定义目录。
    /// </summary>
    public string AccountDirectory { get; private set; }

    public string SettingsFile => Path.Combine(ConfigDirectory, "launcher.json");

    /// <summary>预设文件：LPML\presets.json（随安装自带，可自由编辑）。</summary>
    public string PresetsFile => Path.Combine(AppDirectory, LauncherPresets.FileName);
    public string AccountsFile => Path.Combine(AccountDirectory, "accounts.json");
    public string InstancesFile => Path.Combine(ConfigDirectory, "instances.json");

    /// <summary>实例文件夹注册表。</summary>
    public string FoldersFile => Path.Combine(ConfigDirectory, "folders.json");

    /// <summary>背景图存放目录：用户选的图会复制进来，避免原图被删/移动后失效。</summary>
    public string BackgroundDirectory => Path.Combine(AppDirectory, "backgrounds");

    /// <summary>当前背景图文件（复制后的那份）。</summary>
    public string BackgroundFile(string fileName) => Path.Combine(BackgroundDirectory, fileName);

    /// <summary>Java 列表（LPML\config\javas.json）。</summary>
    public string JavasFile => Path.Combine(ConfigDirectory, "javas.json");
    public string LauncherLogFile => Path.Combine(LogDirectory, "lpml.log");

    /// <summary>系统 TEMP 子目录（仅在用户开启"允许写入 TEMP"时使用）。</summary>
    public string SystemTempDirectory { get; private set; }

    public AppPaths()
    {
        string baseDir;
        try
        {
            var asm = Assembly.GetEntryAssembly()?.Location;
            baseDir = string.IsNullOrEmpty(asm) ? AppContext.BaseDirectory : Path.GetDirectoryName(asm)!;
        }
        catch
        {
            baseDir = AppContext.BaseDirectory;
        }
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.CurrentDirectory;

        RootDirectory = baseDir;
        AppDirectory = Path.Combine(RootDirectory, "LPML");
        _gameDirectory = Path.Combine(RootDirectory, ".minecraft");
        CacheDirectory = Path.Combine(AppDirectory, "cache");
        TempDirectory = Path.Combine(CacheDirectory, "temp");
        ConfigDirectory = Path.Combine(AppDirectory, "config");
        LogDirectory = Path.Combine(AppDirectory, "logs");
        AccountDirectory = Path.Combine(AppDirectory, "accounts");
        SystemTempDirectory = Path.Combine(Path.GetTempPath(), "LPML");
    }

    public void Apply(string rootDirectory, string gameDirectory, string cacheDirectory)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? RootDirectory : rootDirectory;
        AppDirectory = Path.Combine(RootDirectory, "LPML");
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory)
            ? Path.Combine(RootDirectory, ".minecraft")
            : Expand(gameDirectory);
        CacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(AppDirectory, "cache")
            : Expand(cacheDirectory);
        TempDirectory = Path.Combine(CacheDirectory, "temp");
        ConfigDirectory = Path.Combine(AppDirectory, "config");
        LogDirectory = Path.Combine(AppDirectory, "logs");
        AccountDirectory = Path.Combine(AppDirectory, "accounts");
    }

    /// <summary>本机用户目录下的 LPML 数据根：C:\Users\&lt;用户名&gt;\LPML。</summary>
    public static string UserProfileLpmlDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LPML");

    /// <summary>本机用户目录下的账户目录：C:\Users\&lt;用户名&gt;\LPML\accounts。</summary>
    public static string UserProfileAccountDirectory =>
        Path.Combine(UserProfileLpmlDirectory, "accounts");

    /// <summary>
    /// 解析账户目录的实际位置。必须在 Apply 之后调用。
    /// </summary>
    public string ResolveAccountDirectory(Config.AccountStorageLocation location, string? customDirectory)
    {
        return location switch
        {
            Config.AccountStorageLocation.UserProfile => UserProfileAccountDirectory,
            Config.AccountStorageLocation.Custom when !string.IsNullOrWhiteSpace(customDirectory)
                => Expand(customDirectory),
            _ => Path.Combine(AppDirectory, "accounts")
        };
    }

    /// <summary>应用账户存储位置（不搬运文件，搬运见 MigrateAccountStorage）。</summary>
    public void ApplyAccountStorage(Config.AccountStorageLocation location, string? customDirectory)
        => AccountDirectory = ResolveAccountDirectory(location, customDirectory);

    /// <summary>把用户输入的相对路径 / 环境变量展开为绝对路径。</summary>
    public string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(RootDirectory, expanded));
    }

    /// <summary>实例隔离目录（放 LPML 下，避免污染 .minecraft 的通用结构）。</summary>
    /// <summary>
    /// 实例隔离数据的根目录：放在 .minecraft\instances 下。
    /// 以前在 LPML\instances，跟真正的游戏文件分家，迁移时得两头搬；
    /// 放在 .minecraft 里，整个 .minecraft 拷走就带走了一切。
    /// </summary>
    public string InstancesRoot => Path.Combine(GameDirectory, "instances");

    /// <summary>
    /// 内置文档目录（docs\zh-CN、docs\en-US）。
    /// 往这里放 Markdown 就会出现在「文档」页上，不用改代码。
    /// 优先用程序目录下的 docs（发布时跟着走），找不到就用开发目录。
    /// </summary>
    public string DocsDirectory
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "docs"),
                Path.Combine(RootDirectory, "docs")
            };
            foreach (var candidate in candidates)
                if (Directory.Exists(candidate)) return candidate;
            return candidates[0];
        }
    }

    /// <summary>旧位置（LPML\instances），仅用于一次性迁移检测。</summary>
    public string LegacyInstancesRoot => Path.Combine(AppDirectory, "instances");

    public void EnsureCoreDirectories()
    {
        FileHelper.EnsureDirectory(RootDirectory);
        FileHelper.EnsureDirectory(AppDirectory);
        FileHelper.EnsureDirectory(ConfigDirectory);
        FileHelper.EnsureDirectory(AccountDirectory);
        FileHelper.EnsureDirectory(BackgroundDirectory);
        FileHelper.EnsureDirectory(LogDirectory);
        FileHelper.EnsureDirectory(CacheDirectory);
        FileHelper.EnsureDirectory(GameDirectory);
    }

    // ── 游戏目录内的标准结构 ─────────────────────────────────
    public string VersionsDirectory => Path.Combine(GameDirectory, "versions");
    public string LibrariesDirectory => Path.Combine(GameDirectory, "libraries");
    public string AssetsRootDirectory => Path.Combine(GameDirectory, "assets");
    public string AssetsIndexesDirectory => Path.Combine(AssetsRootDirectory, "indexes");
    public string AssetsObjectsDirectory => Path.Combine(AssetsRootDirectory, "objects");
    public string AssetsLegacyDirectory => Path.Combine(AssetsRootDirectory, "virtual", "legacy");

    public string VersionDirectory(string versionId) => Path.Combine(VersionsDirectory, versionId);
    public string VersionJsonPath(string versionId) => Path.Combine(VersionDirectory(versionId), versionId + ".json");
    public string VersionJarPath(string versionId) => Path.Combine(VersionDirectory(versionId), versionId + ".jar");
    public string LibraryPath(string mavenPath) => Path.Combine(LibrariesDirectory, mavenPath.Replace('/', Path.DirectorySeparatorChar));
    public string AssetObjectPath(string hash) => Path.Combine(AssetsObjectsDirectory, hash[..2], hash);

    /// <summary>
    /// 一次性迁移：把旧版本散落在根目录的 config / logs / cache 收进 LPML 子目录。
    /// 必须在 EnsureCoreDirectories 之前调用，否则目标目录已存在会跳过。
    /// </summary>
    public void MigrateLegacyLayout()
    {
        var moves = new (string From, string To)[]
        {
            (Path.Combine(RootDirectory, "config"), ConfigDirectory),
            (Path.Combine(RootDirectory, "logs"), LogDirectory),
            (Path.Combine(RootDirectory, "cache"), CacheDirectory),
            (Path.Combine(GameDirectory, "instances"), InstancesRoot)
        };

        // 旧版本把 accounts.json 放在 config\ 下，搬到独立的 accounts\ 目录
        var legacyAccounts = Path.Combine(ConfigDirectory, "accounts.json");
        var newAccounts = Path.Combine(AccountDirectory, "accounts.json");
        if (File.Exists(legacyAccounts) && !File.Exists(newAccounts))
        {
            try
            {
                FileHelper.EnsureDirectory(AccountDirectory);
                File.Move(legacyAccounts, newAccounts);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var (from, to) in moves)
        {
            if (!Directory.Exists(from) || Directory.Exists(to)) continue;
            try
            {
                FileHelper.EnsureDirectory(Path.GetDirectoryName(to));
                Directory.Move(from, to);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 切换账户存放位置时，把已有 accounts.json 搬到新位置（旧文件保留为备份）。
    /// </summary>
    public static bool MigrateAccountStorage(string fromFile, string toFile)
    {
        if (string.Equals(fromFile, toFile, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(fromFile) || File.Exists(toFile)) return false;
        try
        {
            var directory = Path.GetDirectoryName(toFile);
            if (!string.IsNullOrEmpty(directory)) FileHelper.EnsureDirectory(directory);
            File.Copy(fromFile, toFile, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
