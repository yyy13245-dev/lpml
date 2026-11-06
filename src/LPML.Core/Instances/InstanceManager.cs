using LPML.Core.Utils;

namespace LPML.Core.Instances;

/// <summary>实例管理：持久化到 config\instances.json。</summary>
public sealed class InstanceManager
{
    private readonly string _file;
    private readonly string _instancesRoot;

    public List<GameInstance> Instances { get; private set; } = new();

    public event EventHandler? InstancesChanged;

    public InstanceManager(string file, string instancesRoot)
    {
        _file = file;
        _instancesRoot = instancesRoot;
    }

    public void Load()
    {
        Instances = Json.Load<List<GameInstance>>(_file) ?? new List<GameInstance>();
        InstancesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try { Json.Save(_file, Instances); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>实例隔离目录。_instancesRoot 已指向 <root>\LPML\instances。</summary>
    /// <summary>实例的 mods 目录（光影加载器检测用）。</summary>
    public string InstanceModsDirectory(GameInstance instance)
        => Path.Combine(InstanceDirectory(instance), "mods");

    public string InstanceDirectory(GameInstance instance)
        => Path.Combine(_instancesRoot, FileHelper.SafeFileName(instance.Id));

    /// <summary>实例日志目录（开启落盘后游戏输出写到这里）。</summary>
    public string InstanceLogDirectory(GameInstance instance)
        => Path.Combine(InstanceDirectory(instance), "logs");

    /// <summary>实例崩溃报告目录。</summary>
    public string InstanceCrashDirectory(GameInstance instance)
        => Path.Combine(InstanceDirectory(instance), "crash-reports");

    /// <summary>
    /// 应用版本隔离布局：隔离开启时为实例建独立目录，
    /// 并按共享开关把指定子目录链接回主游戏目录（失败则退化为普通目录）。
    /// </summary>
    public void ApplyIsolationLayout(GameInstance instance, string gameDirectory)
    {
        var root = InstanceDirectory(instance);
        FileHelper.EnsureDirectory(root);

        // 需要独立 / 共享的子目录
        var categories = new (string Name, bool Shared)[]
        {
            ("mods", instance.ShareMods),
            ("saves", instance.ShareSaves),
            ("config", instance.ShareConfig),
            ("resourcepacks", instance.ShareResourcePacks),
            ("shaderpacks", instance.ShareShaderPacks)
        };

        foreach (var (name, shared) in categories)
        {
            var target = Path.Combine(root, name);

            if (!shared)
            {
                // 独立：确保是真实目录（若之前是链接则移除）
                if (IsLink(target)) RemoveLink(target);
                FileHelper.EnsureDirectory(target);
                continue;
            }

            // 共享：主游戏目录里的真实目录 + 实例内链接
            var source = Path.Combine(gameDirectory, name);
            FileHelper.EnsureDirectory(source);
            LinkDirectory(source, target);
        }
    }

    /// <summary>
    /// 把实例的关键信息写进实例目录里的 lpml.json。
    /// 启动前调用：.minecraft 被删、注册表还在时，靠这个文件也能恢复出可启动的实例。
    /// </summary>
    public void WriteLocalConfig(GameInstance instance, IsolationMode mode)
        => InstanceLocalConfig.Save(InstanceDirectory(instance), InstanceLocalConfig.From(instance, mode));

    /// <summary>读取实例目录里的 lpml.json（没有则返回 null）。</summary>
    public InstanceLocalConfig? ReadLocalConfig(GameInstance instance)
        => InstanceLocalConfig.Load(InstanceDirectory(instance));

    /// <summary>
    /// 在两种隔离模式之间迁移实例数据。
    /// UserData→Full：把 .minecraft 里的版本文件搬进实例目录（整个目录可拷走）。
    /// Full→UserData：把实例目录里的版本文件搬回 .minecraft（省空间），用户数据留在实例目录。
    /// 同卷走硬链接（零拷贝、不额外占空间），跨卷或已存在则复制 / 跳过。
    /// </summary>
    /// <returns>实际搬运的文件数。</returns>
    public int MigrateIsolation(GameInstance instance, string sharedDirectory,
        LPML.Core.Instances.IsolationMode target)
    {
        var instanceDir = InstanceDirectory(instance);
        var categories = new[] { "versions", "libraries", "assets" };

        // Full：共享目录 → 实例目录；UserData：实例目录 → 共享目录
        var source = target == LPML.Core.Instances.IsolationMode.Full ? sharedDirectory : instanceDir;
        var destination = target == LPML.Core.Instances.IsolationMode.Full ? instanceDir : sharedDirectory;

        var moved = 0;
        try
        {
            FileHelper.EnsureDirectory(destination);
            foreach (var category in categories)
            {
                var from = Path.Combine(source, category);
                var to = Path.Combine(destination, category);
                if (!Directory.Exists(from) || Path.GetFullPath(from) == Path.GetFullPath(to)) continue;
                moved += CopyTree(from, to);
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn("Instance", L.T("String.Core.Log.Instance.MigrateFailed") + " " + ex.Message);
        }
        return moved;
    }

    /// <summary>递归搬运目录树；同卷优先硬链接，失败则复制。已存在的目标跳过。</summary>
    private static int CopyTree(string source, string destination)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            if (File.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // 硬链接失败（跨卷等）就退回复制
                if (!Utils.HardLink.TryCreate(file, target)) File.Copy(file, target, true);
                count++;
            }
            catch (Exception) { /* 单个文件失败不中断整体迁移 */ }
        }
        return count;
    }

    private static bool IsLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch (IOException) { return false; }
    }

    private static void RemoveLink(string path)
    {
        try { Directory.Delete(path, false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>把 target 指向 source；已存在或平台不支持时静默降级。</summary>
    private static void LinkDirectory(string source, string target)
    {
        try
        {
            if (IsLink(target))
            {
                var info = new DirectoryInfo(target);
                if (string.Equals(info.LinkTarget, source, StringComparison.OrdinalIgnoreCase)) return;
                RemoveLink(target);
            }
            if (Directory.Exists(target)) return;

            FileHelper.EnsureDirectory(Path.GetDirectoryName(target));
            Directory.CreateSymbolicLink(target, source);
        }
        catch (IOException) { FileHelper.EnsureDirectory(target); }
        catch (UnauthorizedAccessException) { FileHelper.EnsureDirectory(target); }
        catch (PlatformNotSupportedException) { FileHelper.EnsureDirectory(target); }
    }

    /// <summary>名字是否已存在（用于建实例前查重）。</summary>
    public bool NameExists(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var target = name.Trim();
        return Instances.Any(i => string.Equals(i.Name, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 建实例。重名时抛 InvalidOperationException（调用方应提示用户改名）。
    /// </summary>
    public GameInstance Create(string name, string versionId, bool isolated)
    {
        var finalName = string.IsNullOrWhiteSpace(name) ? versionId : name.Trim();
        if (NameExists(finalName))
            throw new InvalidOperationException(L.F("String.Core.Err.Instance.DuplicateName", finalName));

        var instance = new GameInstance
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = finalName,
            VersionId = versionId,
            Isolated = isolated,
            CreatedAt = DateTimeOffset.Now
        };
        FileHelper.EnsureDirectory(InstanceDirectory(instance));
        Instances.Add(instance);
        Save();
        InstancesChanged?.Invoke(this, EventArgs.Empty);
        return instance;
    }

    public void Delete(GameInstance instance, bool deleteFiles)
    {
        Instances.Remove(instance);
        if (deleteFiles) FileHelper.DeleteDirectory(InstanceDirectory(instance));
        Save();
        InstancesChanged?.Invoke(this, EventArgs.Empty);
    }

    public GameInstance Clone(GameInstance source)
    {
        var copy = new GameInstance
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = source.Name + L.T("String.Core.Item.CopySuffix"),
            VersionId = source.VersionId,
            Notes = source.Notes,
            IconPath = source.IconPath,
            Isolated = source.Isolated,
            JavaPathOverride = source.JavaPathOverride,
            MaxMemoryOverrideMb = source.MaxMemoryOverrideMb,
            MinMemoryOverrideMb = source.MinMemoryOverrideMb,
            JvmArgumentOverride = source.JvmArgumentOverride,
            GameArgumentOverride = source.GameArgumentOverride,
            LoaderName = source.LoaderName,
            LoaderVersion = source.LoaderVersion
        };
        FileHelper.EnsureDirectory(InstanceDirectory(copy));
        Instances.Add(copy);
        Save();
        InstancesChanged?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    public void MarkPlayed(GameInstance instance, double minutes)
    {
        instance.LastPlayed = DateTimeOffset.Now;
        instance.PlayCount++;
        instance.TotalPlayMinutes += minutes;
        Save();
    }

    public GameInstance? Find(string? id) => Instances.FirstOrDefault(i => i.Id == id);
}
