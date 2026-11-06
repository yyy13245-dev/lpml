using LPML.Core.Utils;
using LPML.Core.Config;

namespace LPML.Core.Java;

/// <summary>
/// Java 管理器：维护一份持久化的 Java 列表，支持手动添加、整目录同步、
/// 系统扫描（PATH / JAVA_HOME / 常见安装目录），并按版本要求自动挑选最合适的一个。
/// 列表持久化在 <root>\LPML\config\javas.json。
/// </summary>
public sealed class JavaManager
{
    private readonly string _file;
    private readonly JavaSettings _settings;
    private readonly string _runtimeRoot;

    public List<JavaInfo> Javas { get; private set; } = new();

    public event EventHandler? JavasChanged;

    public JavaManager(string file, JavaSettings settings, string runtimeRoot)
    {
        _file = file;
        _settings = settings;
        _runtimeRoot = runtimeRoot;
    }

    public void Load()
    {
        Javas = Json.Load<List<JavaInfo>>(_file) ?? new List<JavaInfo>();
        foreach (var java in Javas)
            if (string.IsNullOrWhiteSpace(java.Id)) java.Id = JavaLocator.ComputeId(java.Path);
        JavasChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try { Json.Save(_file, Javas); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Notify()
    {
        Save();
        JavasChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── 添加 ───────────────────────────────────────────────

    /// <summary>
    /// 添加一个 Java：先探测选中的可执行文件，再自动同步它的 JDK 根目录与兄弟 JDK。
    /// 返回实际新增的条目（同步出来的兄弟也会包含）。
    /// </summary>
    public List<JavaInfo> Add(string exePath)
    {
        var discovered = JavaLocator.ProbeWithSiblings(exePath);
        var added = new List<JavaInfo>();
        foreach (var info in discovered)
        {
            if (Javas.Any(j => string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            Javas.Add(info);
            added.Add(info);
        }
        if (added.Count > 0) SortAndNotify();
        return added;
    }

    /// <summary>扫描系统 PATH / JAVA_HOME，把新发现的 Java 并入列表（已有则跳过）。</summary>
    public List<JavaInfo> SyncFromEnvironment()
    {
        var found = JavaLocator.DetectFromEnvironment();
        var added = new List<JavaInfo>();
        foreach (var info in found)
        {
            if (Javas.Any(j => string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            Javas.Add(info);
            added.Add(info);
        }
        if (added.Count > 0) SortAndNotify();

        // 同步结果写清楚：扫到几个、新增几个、各自版本
        Log.Info("Java", L.F("String.Core.Log.Java.SyncDone", found.Count, added.Count,
            found.Count == 0 ? L.T("String.Core.Log.Java.NoneFound")
                             : string.Join(" | ", found.Take(8).Select(j => $"{j.Name}({j.MajorVersion}) {j.Path}"))));
        foreach (var info in added)
            Log.Info("Java", L.F("String.Core.Log.Java.Added", info.Name, info.MajorVersion, info.Path));
        return added;
    }

    /// <summary>全量扫描：PATH + 常见安装目录 + 游戏 runtime（较慢，手动触发）。</summary>
    public List<JavaInfo> ScanSystem()
    {
        var extra = new List<string> { _runtimeRoot };
        if (!string.IsNullOrWhiteSpace(_settings.JavaPath)) extra.Add(_settings.JavaPath!);
        var found = JavaLocator.DetectAll(extra);

        var added = new List<JavaInfo>();
        foreach (var info in found)
        {
            if (Javas.Any(j => string.Equals(j.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            Javas.Add(info);
            added.Add(info);
        }
        if (added.Count > 0) SortAndNotify();

        Log.Info("Java", L.F("String.Core.Log.Java.ScanDone", found.Count, added.Count,
            found.Count == 0 ? L.T("String.Core.Log.Java.NoneFound")
                             : string.Join(" | ", found.Take(8).Select(j => $"{j.Name}({j.MajorVersion})"))));
        return added;
    }

    // ── 校验与移除 ─────────────────────────────────────────

    /// <summary>逐条验证列表里的 Java 是否仍然可用，返回失效数量。</summary>
    public int Validate()
    {
        var removed = 0;
        var changed = false;
        for (var i = Javas.Count - 1; i >= 0; i--)
        {
            var java = Javas[i];
            var alive = File.Exists(java.Path) && JavaLocator.Probe(java.Path) is not null;
            if (alive)
            {
                if (!java.IsValid) { java.IsValid = true; changed = true; }
                continue;
            }
            java.IsValid = false;
            removed++;
            changed = true;
        }
        if (changed) Notify();

        // 验证结果：总数、失效数，以及失效的具体路径
        if (removed > 0)
        {
            var dead = Javas.Where(j => !j.IsValid).Select(j => j.Path).Take(8);
            Log.Warn("Java", L.F("String.Core.Log.Java.ValidateDead", removed, string.Join(" | ", dead)));
        }
        else
        {
            Log.Info("Java", L.F("String.Core.Log.Java.ValidateOk", Javas.Count));
        }
        return removed;
    }

    public void Remove(JavaInfo java)
    {
        if (Javas.Remove(java)) Notify();
    }

    public void SetNickname(JavaInfo java, string? nickname)
    {
        java.Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname!.Trim();
        Notify();
    }

    private void SortAndNotify()
    {
        Javas = Javas
            .OrderByDescending(j => j.MajorVersion)
            .ThenBy(j => j.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Notify();
    }

    // ── 选择 ───────────────────────────────────────────────

    /// <summary>当前生效的 Java：手动模式返回用户指定的，自动模式按版本要求挑选。</summary>
    public JavaInfo? Resolve(int requiredMajor)
    {
        var usable = Javas.Where(j => j.IsValid && File.Exists(j.Path)).ToList();

        if (_settings.SelectionMode == JavaSelectionMode.Manual &&
            !string.IsNullOrWhiteSpace(_settings.PreferredJavaId))
        {
            var pinned = usable.FirstOrDefault(j => j.Id == _settings.PreferredJavaId);
            if (pinned is not null) return pinned;
        }

        return Recommend(requiredMajor, usable, _settings.RuntimePreference);
    }

    /// <summary>
    /// 按版本要求挑选：优先精确匹配且 64 位；其次更高的大版本；
    /// 最后才回退到任意 64 位（低版本 MC 在高版本 Java 上多数可跑）。
    /// 同一档内按 JDK/JRE 偏好排序（默认 JDK 优先）。
    /// </summary>
    public static JavaInfo? Recommend(int requiredMajor, IReadOnlyList<JavaInfo> pool,
        JavaRuntimePreference preference = JavaRuntimePreference.PreferJdk)
    {
        if (pool.Count == 0) return null;

        // 同档内排序：先按 JDK/JRE 偏好，再按路径稳定排序
        List<JavaInfo> Order(List<JavaInfo> group) => preference == JavaRuntimePreference.PreferJre
            ? group.OrderBy(j => j.IsJdk ? 1 : 0).ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase).ToList()
            : group.OrderBy(j => j.IsJdk ? 0 : 1).ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase).ToList();

        var exact = Order(pool.Where(j => j.MajorVersion == requiredMajor && j.Is64Bit).ToList());
        if (exact.Count > 0) return exact.First();

        var higher = Order(pool.Where(j => j.MajorVersion > requiredMajor && j.Is64Bit)
                               .OrderBy(j => j.MajorVersion).ToList());
        if (higher.Count > 0) return higher.First();

        var any64 = Order(pool.Where(j => j.Is64Bit).ToList());
        if (any64.Count > 0) return any64.First();

        return Order(pool.OrderByDescending(j => j.MajorVersion).ToList()).FirstOrDefault();
    }

    /// <summary>
    /// 失败重试用：给出「下一个候选」——**只考虑更高版本或同版本**，排除已试过的。
    /// 低版本的 Java 不作为重试候选（它本来就跑不动这个 MC 版本，换上去只会再失败）。
    /// 返回 null 表示没有可换的了，此时应直接报错。
    /// </summary>
    public static JavaInfo? NextCandidate(int requiredMajor, IReadOnlyList<JavaInfo> pool,
        ISet<string> triedIds, JavaRuntimePreference preference = JavaRuntimePreference.PreferJdk)
    {
        if (pool.Count == 0) return null;

        var usable = pool.Where(j => j.IsValid && File.Exists(j.Path) && !triedIds.Contains(j.Id)).ToList();
        if (usable.Count == 0) return null;

        // 1) 同版本优先：通常是 JDK ↔ JRE 互换，或换另一个厂商的同版本实现。
        //    跨大版本风险更高（如 1.16 在 Java 17 上可能要额外参数），所以放在后面。
        var same = usable.Where(j => j.MajorVersion == requiredMajor).ToList();
        var pickSame = OrderByPreference(same, preference);
        if (pickSame.Count > 0) return pickSame.First();

        // 2) 更高版本：从最接近的往上升，避免一下子跳到过新的大版本
        var higher = usable.Where(j => j.MajorVersion > requiredMajor && j.Is64Bit)
                           .OrderBy(j => j.MajorVersion)
                           .ToList();
        var pick = OrderByPreference(higher, preference);
        if (pick.Count > 0) return pick.First();

        // 3) 没有同版本也没有更高版本 → 不再硬凑低版本，交给调用方报错
        return null;
    }

    private static List<JavaInfo> OrderByPreference(List<JavaInfo> group, JavaRuntimePreference preference)
        => preference == JavaRuntimePreference.PreferJre
            ? group.OrderBy(j => j.IsJdk ? 1 : 0).ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase).ToList()
            : group.OrderBy(j => j.IsJdk ? 0 : 1).ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>解析时要排除已试过的（失败重试用）。</summary>
    public JavaInfo? ResolveExcluding(int requiredMajor, ISet<string> tried)
    {
        var usable = Javas.Where(j => j.IsValid && File.Exists(j.Path)).ToList();
        if (usable.Count == 0) return null;

        if (_settings.SelectionMode == JavaSelectionMode.Manual &&
            !string.IsNullOrWhiteSpace(_settings.PreferredJavaId) &&
            !tried.Contains(_settings.PreferredJavaId!))
        {
            var pinned = usable.FirstOrDefault(j => j.Id == _settings.PreferredJavaId);
            if (pinned is not null) return pinned;
        }

        var remaining = usable.Where(j => !tried.Contains(j.Id)).ToList();
        return remaining.Count == 0 ? null : Recommend(requiredMajor, remaining, _settings.RuntimePreference);
    }

    /// <summary>为展示用：给每个 Java 标注它是否会被当前版本要求选中。</summary>
    public JavaInfo? ResolveOrDefault(int requiredMajor) =>
        Resolve(requiredMajor) ?? Javas.FirstOrDefault(j => j.IsValid && File.Exists(j.Path));
}
