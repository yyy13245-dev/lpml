using System.Threading;
using LPML.Core.Auth;
using LPML.Core.Config;
using LPML.Core.Download;
using LPML.Core.Game;
using LPML.Core.Instances;
using LPML.Core.Integration;
using LPML.Core.Java;
using LPML.Core.Launch;
using LPML.Core.Mirror;
using LPML.Core.Modrinth;
using LPML.Core.Utils;

namespace LPML.Core;

/// <summary>
/// 启动器总控：一处创建，全局共享。所有服务都不依赖 UI，可单独测试。
/// </summary>
public sealed class LauncherContext
{
    public AppPaths Paths { get; } = new();
    public SettingsService SettingsService { get; } = new();
    public HttpService Http { get; } = new();
    public MirrorManager Mirrors { get; private set; } = new(new MirrorSettings());
    public TempFileManager Temp { get; private set; } = null!;
    public SpeedLimiter Limiter { get; } = new();
    public DownloadManager Downloads { get; private set; } = null!;

    /// <summary>本地库索引：复用已有版本目录里的 jar，避免重复下载。</summary>
    public LocalLibraryIndex DownloadIndex { get; private set; } = null!;
    public VersionManifestService ManifestService { get; private set; } = null!;
    public VersionResolver Resolver { get; private set; } = null!;
    public GameInstaller Installer { get; private set; } = null!;
    public LoaderInstaller Loaders { get; private set; } = null!;
    public JavaService Java { get; private set; } = null!;

    /// <summary>Java 列表管理（持久化 + 自动选择）。</summary>
    public JavaManager Javas { get; private set; } = null!;
    public AccountManager Accounts { get; private set; } = null!;
    public InstanceManager Instances { get; private set; } = null!;
    public LaunchArgumentBuilder Arguments { get; private set; } = null!;
    public LaunchService Launcher { get; private set; } = null!;
    public ModpackImporter Modpacks { get; private set; } = null!;

    /// <summary>Modrinth 客户端（模组 / 资源包 / 光影 / 整合包）。</summary>
    public ModrinthClient Modrinth { get; private set; } = null!;

    public LauncherSettings Settings => SettingsService.Settings;

    /// <summary>出厂预设（实例模板 / 推荐版本 / 镜像源）。</summary>
    public LauncherPresets Presets { get; private set; } = new();

    /// <summary>实例文件夹注册表（每个文件夹是一套完整的游戏目录）。</summary>
    public InstanceFolderManager Folders { get; private set; } = new("", "");

    /// <summary>初始化：建立目录、加载配置、装配服务。</summary>
    /// <summary>索引的扫描根：游戏目录 + 所有实例文件夹 + 用户额外指定的目录。</summary>
    public IEnumerable<string> IndexRoots()
    {
        yield return Paths.GameDirectory;
        if (Folders is not null)
            foreach (var folder in Folders.Folders)
            {
                if (Folders.TryResolve(folder, out var full)) yield return full;
            }
        var extra = Settings.Download.ExtraLibraryDirectories;
        if (extra is not null)
            foreach (var directory in extra)
                if (!string.IsNullOrWhiteSpace(directory)) yield return directory!;
    }

    public void Initialize()
    {
        SettingsService.Load(Paths.SettingsFile);
        Presets = LauncherPresets.Load(Paths.PresetsFile);
        Folders = new InstanceFolderManager(Paths.FoldersFile, Paths.RootDirectory);
        Folders.Load();
        Paths.Apply(Paths.RootDirectory, Settings.Paths.GameDirectory, Settings.Paths.CacheDirectory);
        // 便携档的账户目录始终跟随启动器根目录；用户目录 / 自定义由 AccountManager 自行解析
        Paths.ApplyAccountStorage(AccountStorageLocation.Portable, null);
        Paths.MigrateLegacyLayout();
        Paths.EnsureCoreDirectories();

        Log.Service.MinimumLevel = Settings.Advanced.LogLevel;
        Log.Service.AttachFile(Paths.LauncherLogFile);

        Http.Configure(Settings.Download);
        Mirrors = new MirrorManager(Settings.Mirror);
        Temp = new TempFileManager(Paths, Settings.Temp);
        Limiter.LimitBytesPerSecond = Settings.Download.SpeedLimitKb * 1024L;
        DownloadIndex = new LocalLibraryIndex(Paths.ConfigDirectory);
        Downloads = new DownloadManager(Http, Temp, Limiter, Settings.Download, DownloadIndex, Mirrors);
        ManifestService = new VersionManifestService(Paths, Http, Mirrors);
        Resolver = new VersionResolver(Paths);
        Installer = new GameInstaller(Paths, Http, Mirrors, Settings.Download);
        Loaders = new LoaderInstaller(Paths, Http, Mirrors);
        Java = new JavaService(Paths, Http, Mirrors, Settings.Java);
        Javas = new JavaManager(Paths.JavasFile, Settings.Java, Java.RuntimeRoot);
        Javas.Load();
        Java.Manager = Javas;
        // 首次使用（列表为空且开启了自动探测）时自动同步系统 PATH 里的 Java
        if (Javas.Javas.Count == 0 && Settings.Java.AutoDetectJava)
        {
            var found = Javas.SyncFromEnvironment();
            if (found.Count > 0)
                Log.Info("Java", L.F("String.Core.Log.Java.Synced", found.Count));
        }
        Accounts = new AccountManager(Paths, Http)
        {
            MicrosoftClientId = Settings.Account.MicrosoftClientId ?? "",
            AutoOpenBrowser = Settings.Account.AutoOpenBrowser,
            DefaultStorageLocation = Settings.Account.StorageLocation,
            DefaultCustomDirectory = Settings.Account.StorageCustomDirectory
        };
        Accounts.SetImportedDirectories(Settings.Account.ImportedDirectories);
        Accounts.Load();
        Instances = new InstanceManager(Paths.InstancesFile, Paths.InstancesRoot);
        Instances.Load();
        Arguments = new LaunchArgumentBuilder(Paths);
        Launcher = new LaunchService(Paths, Resolver, Arguments, Java, Installer, Downloads);
        Modpacks = new ModpackImporter(Paths, Mirrors);
        Modrinth = new ModrinthClient(Http);

        // 旧位置 LPML\instances 里可能还留着隔离数据，搬到 .minecraft\instances 下。
        // 放在 .minecraft 里，整个目录拷走就带走了一切，方便迁移。
        try { MigrateLegacyInstances(); } catch (Exception) { }

        // 旧版本遗留过"独立缓存目录"，文件被复制进去导致版本目录是空的。
        // 现在没有这个目录了，检测到就提醒一次，用户可以直接删掉省空间。
        try
        {
            var legacy = Path.Combine(Paths.CacheDirectory, "objects");
            if (Directory.Exists(legacy) && Directory.EnumerateFileSystemEntries(legacy).Any())
                Log.Warn("Context", L.T("String.Core.Log.Context.LegacyCache"));
        }
        catch (Exception) { }

        // 后台扫描已有目录建索引：装过版本后，共用的库就不用再下了。
        // 放在最后且不 await，避免拖慢启动。
        _ = Downloads.WarmupIndexAsync(IndexRoots());

        // 勾了「自动选择最快镜像」才测；不阻塞启动，结果只作用于本次会话
        if (Settings.Mirror.AutoSelectFastest)
            _ = SelectFastestMirrorAsync();

        SettingsService.SettingsChanged += (_, _) => ApplySettings();
        // 启动时按物理目录补齐实例登记：磁盘里有版本、注册表里却没有，
        // 就不能在文件夹页 / 实例设置里显示成「0 个实例」。
        try { EnsureFolderInstances(CurrentFolderId); } catch (Exception ex) { Log.Warn("Context", "Folder scan failed: " + ex.Message); }
        Log.Info("Context", L.F("String.Core.Log.Context.Ready", Paths.RootDirectory));
    }

    /// <summary>启动时对各镜像做一次轻量测速，选最快的作为全局默认源。</summary>
    private async Task SelectFastestMirrorAsync()
    {
        try
        {
            var results = await Mirrors.MeasureAsync(async (url, ct) =>
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                await Http.GetStringAsync(url, probeCts.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (results.Count == 0) return;
            var best = results[0];
            Mirrors.ApplyFastest(best.Kind);
            Log.Info("Mirror", L.F("String.Core.Log.Mirror.Fastest",
                Mirrors.Get(best.Kind).Name, best.ElapsedMs));
        }
        catch (Exception ex)
        {
            Log.Warn("Mirror", L.T("String.Core.Log.Mirror.ProbeFailed") + " " + ex.Message);
        }
    }

    /// <summary>设置变更后热更新（下载源、限速、日志级别等）。</summary>
    public void ApplySettings()
    {
        Http.Configure(Settings.Download);
        Mirrors.Refresh();
        Limiter.LimitBytesPerSecond = Settings.Download.SpeedLimitKb * 1024L;
        Log.Service.MinimumLevel = Settings.Advanced.LogLevel;
        Accounts.MicrosoftClientId = Settings.Account.MicrosoftClientId ?? "";
        Accounts.AutoOpenBrowser = Settings.Account.AutoOpenBrowser;
        Accounts.DefaultStorageLocation = Settings.Account.StorageLocation;
        Accounts.DefaultCustomDirectory = Settings.Account.StorageCustomDirectory;
        Accounts.SetImportedDirectories(Settings.Account.ImportedDirectories);
        Paths.Apply(Paths.RootDirectory, Settings.Paths.GameDirectory, Settings.Paths.CacheDirectory);
    }

    public void Save()
    {
        SettingsService.Save(Paths.SettingsFile);
        Accounts.Save();
        Instances.Save();
        if (Settings.Paths.WriteOfficialProfiles) SyncOfficialProfiles();
    }

    /// <summary>
    /// 写入官方 launcher_profiles.json（仅记录已安装版本与当前选择），
    /// 让官方启动器 / 其他第三方启动器能直接识别这套 .minecraft 目录。
    /// </summary>
    private void SyncOfficialProfiles()
    {
        try
        {
            var file = Path.Combine(Paths.GameDirectory, "launcher_profiles.json");
            if (!File.Exists(file) && !Directory.Exists(Paths.VersionsDirectory)) return;

            var profiles = new Dictionary<string, object>();
            foreach (var id in InstalledVersions())
                profiles[id] = new Dictionary<string, object>
                {
                    ["name"] = id,
                    ["type"] = "custom",
                    ["lastVersionId"] = id,
                    ["icon"] = "Furnace"
                };

            var payload = new Dictionary<string, object>
            {
                ["profiles"] = profiles,
                ["selectedProfile"] = Instances.Find(Settings.SelectedInstanceId)?.VersionId ?? "",
                ["launcherVersion"] = $"LPML {BuildInfo.Version}"
            };
            Json.Save(file, payload);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>退出：保存配置 + 按策略清理 TEMP（绿色承诺）。</summary>
    public void Shutdown()
    {
        try
        {
            Save();
            Temp?.Cleanup();
            Log.Service.Flush();
        }
        catch
        {
            // 退出阶段不抛异常
        }
    }

    /// <summary>当前文件夹的物理目录（默认文件夹就是 .minecraft）。</summary>
    public string CurrentFolderDirectory()
    {
        var id = CurrentFolderId;
        if (string.IsNullOrEmpty(id)) return Paths.GameDirectory;
        var folder = Folders.Find(id);
        return folder is null ? Paths.GameDirectory : Folders.Resolve(folder);
    }

    /// <summary>
    /// 按物理目录扫描已安装版本，不读共享 .minecraft 的缓存。
    /// 用于「文件夹」页和实例页：新文件夹里没装过的版本，不该显示成已安装。
    /// </summary>
    public List<string> InstalledVersionsIn(string root)
    {
        try
        {
            var dir = Path.Combine(root, "versions");
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.EnumerateDirectories(dir)
                .Select(d => Path.Combine(d, Path.GetFileName(d) + ".json"))
                .Where(File.Exists)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return new List<string>(); }
        catch (UnauthorizedAccessException) { return new List<string>(); }
    }

    /// <summary>游戏目录内已安装的版本列表。</summary>
    public List<string> InstalledVersions()
    {
        try
        {
            if (!Directory.Exists(Paths.VersionsDirectory)) return new List<string>();
            return Directory.EnumerateDirectories(Paths.VersionsDirectory)
                .Select(dir => Path.Combine(dir, Path.GetFileName(dir) + ".json"))
                .Where(File.Exists)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return new List<string>(); }
        catch (UnauthorizedAccessException) { return new List<string>(); }
    }

    /// <summary>实例的模组目录（隔离时指向实例目录，否则指向 .minecraft\mods）。</summary>
    public string ModsDirectoryFor(GameInstance instance)
        => Path.Combine(WorkingDirectoryFor(instance), "mods");

    /// <summary>
    /// 版本文件的存放目录：versions / libraries / assets / natives 都在这里。
    /// 用户数据隔离模式：落在 .minecraft（或指定文件夹），版本文件全局共享、省空间。
    /// 完整隔离模式：落在实例目录，整个目录拷走即可迁移。
    /// </summary>
    /// <summary>
    /// 一次性迁移：把旧位置 LPML\instances 下的实例数据搬到 .minecraft\instances。
    /// 只搬不存在的目标，已存在的保持不动（避免覆盖新数据）。
    /// </summary>
    private void MigrateLegacyInstances()
    {
        var legacy = Paths.LegacyInstancesRoot;
        var current = Paths.InstancesRoot;
        if (!Directory.Exists(legacy)) return;
        if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) return;

        var moved = 0;
        foreach (var directory in Directory.EnumerateDirectories(legacy))
        {
            var name = Path.GetFileName(directory);
            var target = Path.Combine(current, name);
            if (Directory.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(current);
                Directory.Move(directory, target);
                moved++;
            }
            catch (Exception) { /* 单个失败不影响其他 */ }
        }
        if (moved > 0)
            Log.Info("Context", L.F("String.Core.Log.Context.MigratedInstances", moved, current));
    }

    private readonly Dictionary<string, string> _folderLayouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>布局记录放在该文件夹根下，拷走整个目录时也带着走。</summary>
    private string LayoutFile(string folderId)
    {
        var path = Paths.GameDirectory;
        if (!string.IsNullOrEmpty(folderId))
        {
            var folder = Folders.Find(folderId);
            if (folder is not null) path = Folders.Resolve(folder);
        }
        return Path.Combine(path, "lpml-folder.json");
    }

    /// <summary>split = 版本文件与实例数据分开放；merged = 合在一起（PCL / HMCL 式）。默认 split。</summary>
    public string FolderLayoutFor(string folderId)
    {
        var key = folderId ?? "";
        if (_folderLayouts.TryGetValue(key, out var cached)) return cached;
        var layout = "split";
        try
        {
            var file = LayoutFile(key);
            if (File.Exists(file))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("layout", out var v))
                {
                    var s = v.GetString();
                    if (s == "split" || s == "merged") layout = s;
                }
            }
        }
        catch (Exception ex) { Log.Warn("Context", "Read folder layout failed: " + ex.Message); }
        _folderLayouts[key] = layout;
        return layout;
    }

    /// <summary>整夹切换存储方式；已有实例同步搬移，之后新建的实例自动跟随。</summary>
    public int SetFolderLayout(string folderId, string layout)
    {
        if (layout != "split" && layout != "merged") return 0;
        var key = folderId ?? "";
        var root = Paths.GameDirectory;
        if (!string.IsNullOrEmpty(key))
        {
            var folder = Folders.Find(key);
            if (folder is not null) root = Folders.Resolve(folder);
        }
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(LayoutFile(key), System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { ["layout"] = layout }));
        }
        catch (Exception ex) { Log.Warn("Context", "Write folder layout failed: " + ex.Message); }
        _folderLayouts[key] = layout;

        var mode = layout == "merged"
            ? LPML.Core.Instances.IsolationMode.Full
            : LPML.Core.Instances.IsolationMode.UserData;
        var changed = 0;
        foreach (var inst in Instances.Instances
                     .Where(i => string.Equals(i.FolderId ?? "", key, StringComparison.Ordinal)).ToList())
        {
            inst.IsolationModeOverride = mode;
            try { Instances.MigrateIsolation(inst, root, mode); }
            catch (Exception ex) { Log.Warn("Context", "Migrate folder layout failed: " + ex.Message); }
            changed++;
        }
        Instances.Save();
        return changed;
    }

    /// <summary>该实例生效的隔离模式（实例级覆盖 > 全局设置）。</summary>
    public LPML.Core.Instances.IsolationMode IsolationModeFor(GameInstance? instance)
    {
        if (instance is null) return Settings.Paths.IsolationMode;
        var effective = instance.IsolationModeEffective(Settings.Paths.IsolationMode);
        // 实例自己没指定时，跟随所属文件夹的存储布局
        if (instance.IsolationModeOverride is not null) return effective;
        if (!string.IsNullOrWhiteSpace(instance.FolderId) && FolderLayoutFor(instance.FolderId) == "merged")
            return LPML.Core.Instances.IsolationMode.Full;
        return effective;
    }

    private readonly HashSet<string> _syncedFolderIds = new(StringComparer.Ordinal);
    private bool _syncingFolders;

    /// <summary>
    /// 按物理目录补齐实例登记。文件夹决定实例：某个文件夹的 versions/ 下已有
    /// 完整版本 JSON，却没在 instances.json 里登记时，补一条属于该文件夹的实例，
    /// 避免「资源管理器里看得到、启动器里显示 0 个」。每次会话每个文件夹只补一次。
    /// </summary>
    public int EnsureFolderInstances(string folderId)
    {
        if (string.IsNullOrEmpty(folderId) || _syncingFolders) return 0;
        if (!_syncedFolderIds.Add(folderId)) return 0;
        _syncingFolders = true;
        try
        {
            var folder = Folders.Find(folderId);
            if (folder is null) return 0;
            var created = 0;
            foreach (var version in InstalledVersionsIn(Folders.Resolve(folder)))
            {
                var exists = Instances.Instances.Any(i =>
                    string.Equals(i.FolderId ?? "", folderId, StringComparison.Ordinal) &&
                    string.Equals(i.VersionId, version, StringComparison.OrdinalIgnoreCase));
                if (exists || Instances.NameExists(version, folderId)) continue;
                try
                {
                    var instance = Instances.Create(version, version,
                        Settings.Paths.InstanceIsolation, folderId);
                    instance.FolderId = folderId;
                    if (FolderLayoutFor(folderId) == "merged")
                        instance.IsolationModeOverride = LPML.Core.Instances.IsolationMode.Full;
                    created++;
                }
                catch (Exception ex) { Log.Warn("Context", "Register folder instance failed: " + ex.Message); }
            }
            if (created > 0)
            {
                Instances.Save();
                Log.Info("Context", $"已按目录补齐 {created} 个实例：{folder.Name}");
            }
            return created;
        }
        finally { _syncingFolders = false; }
    }

    /// <summary>用户主动查看文件夹时重扫一次（本次会话内可能又装了新版本）。</summary>
    public void RescanFolderInstances(string folderId)
    {
        _syncedFolderIds.Remove(folderId);
        EnsureFolderInstances(folderId);
    }

    /// <summary>
    /// 当前浏览的实例文件夹 id（空 = 默认 .minecraft）。
    /// 文件夹决定实例的可见范围与名称查重作用域；切换时若原来选中的实例
    /// 不属于新文件夹，自动改选新文件夹里的第一个，避免"页面里显示、磁盘里没有"。
    /// </summary>
    public string CurrentFolderId
    {
        get => Settings.CurrentFolderId ?? "";
        set
        {
            var id = value ?? "";
            if (string.Equals(Settings.CurrentFolderId ?? "", id, StringComparison.Ordinal)) return;
            Settings.CurrentFolderId = string.IsNullOrEmpty(id) ? null : id;
            var selected = Instances?.Find(Settings.SelectedInstanceId);
            if (selected is null || !string.Equals(selected.FolderId ?? "", id, StringComparison.Ordinal))
                Settings.SelectedInstanceId = Instances?.Instances
                    .FirstOrDefault(i => string.Equals(i.FolderId ?? "", id, StringComparison.Ordinal))?.Id;
            Save();
            try { EnsureFolderInstances(id); } catch (Exception ex) { Log.Warn("Context", "Folder scan failed: " + ex.Message); }
        }
    }

    /// <summary>当前文件夹里的实例。文件夹决定实例，而不是实例决定文件夹。</summary>
    public List<GameInstance> InstancesInCurrentFolder()
    {
        var id = CurrentFolderId;
        try { EnsureFolderInstances(id); } catch (Exception ex) { Log.Warn("Context", "Folder scan failed: " + ex.Message); }
        return Instances.Instances
            .Where(i => string.Equals(i.FolderId ?? "", id, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>某个实例是否属于当前文件夹。</summary>
    public bool IsInCurrentFolder(GameInstance? instance)
        => instance is not null
           && string.Equals(instance.FolderId ?? "", CurrentFolderId, StringComparison.Ordinal);

    public string SharedGameDirectoryFor(GameInstance? instance)
    {
        if (instance is null) return Paths.GameDirectory;

        if (!string.IsNullOrWhiteSpace(instance.DirectoryOverride))
            return Paths.Expand(instance.DirectoryOverride);

        // 完整隔离：版本文件也放进实例目录，整个目录拷走就能迁移
        if (IsolationModeFor(instance) == LPML.Core.Instances.IsolationMode.Full)
            return Instances.InstanceDirectory(instance);

        if (!string.IsNullOrWhiteSpace(instance.FolderId))
        {
            var folder = Folders.Find(instance.FolderId);
            if (folder is not null) return Folders.Resolve(folder);
        }

        // 默认就是 .minecraft
        return Paths.GameDirectory;
    }

    public string WorkingDirectoryFor(GameInstance? instance)
    {
        if (instance is null) return Paths.GameDirectory;

        // 1) 实例自己指定的目录（相对 / 绝对都支持）
        if (!string.IsNullOrWhiteSpace(instance.DirectoryOverride))
            return Paths.Expand(instance.DirectoryOverride);

        // 2) 所属实例文件夹
        if (!string.IsNullOrWhiteSpace(instance.FolderId))
        {
            var folder = Folders.Find(instance.FolderId);
            if (folder is not null) return Folders.Resolve(folder);
        }

        // 3) 回退：隔离实例用 LPML\instances\<id>，否则 .minecraft
        return instance.Isolated ? Instances.InstanceDirectory(instance) : Paths.GameDirectory;
    }
}
