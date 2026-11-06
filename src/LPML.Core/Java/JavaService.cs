using LPML.Core.Download;
using LPML.Core.Game.Models;
using LPML.Core.Mirror;
using System.Text.Json.Serialization;
using LPML.Core.Config;

namespace LPML.Core.Java;

/// <summary>Java 管理：探测、推荐、按需下载 Mojang 官方运行时（可走镜像）。</summary>
public sealed class JavaService
{
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly MirrorManager _mirrors;
    private readonly JavaSettings _settings;

    public JavaService(AppPaths paths, HttpService http, MirrorManager mirrors, JavaSettings settings)
    {
        _paths = paths;
        _http = http;
        _mirrors = mirrors;
        _settings = settings;
    }

    /// <summary>
    /// 由 LauncherContext 装配的 Java 列表管理器。装配后，
    /// 探测与推荐都优先使用用户维护的那份列表，扫盘只作为兜底。
    /// </summary>
    public JavaManager? Manager { get; set; }

    /// <summary>当前 Java 设置（选择策略、JDK/JRE 偏好、失败策略）。</summary>
    public JavaSettings Settings => _settings;

    /// <summary>失败重试用：给出下一个候选 Java（排除已试过的）。</summary>
    public JavaInfo? NextCandidate(int requiredMajor, ISet<string> triedIds)
    {
        var pool = Manager?.Javas.Where(j => j.IsValid && File.Exists(j.Path)).ToList()
                   ?? Detect().ToList();
        return JavaManager.NextCandidate(requiredMajor, pool, triedIds, _settings.RuntimePreference);
    }

    public string RuntimeRoot => Path.Combine(_paths.GameDirectory, "runtime");

    /// <summary>可用 Java 池：优先用户列表，其次扫盘。</summary>
    public IReadOnlyList<JavaInfo> Detect()
    {
        var managed = Manager?.Javas.Where(j => j.IsValid && File.Exists(j.Path)).ToList() ?? new List<JavaInfo>();
        if (managed.Count > 0) return managed;

        var extra = new List<string> { RuntimeRoot };
        if (!string.IsNullOrWhiteSpace(_settings.JavaPath)) extra.Add(_settings.JavaPath!);
        return JavaLocator.DetectAll(extra);
    }

    /// <summary>根据版本要求的 Java 大版本挑选最合适的 Java。</summary>
    public JavaInfo? Recommend(int requiredMajor, IReadOnlyList<JavaInfo>? pool = null)
    {
        var list = (pool ?? Detect()).ToList();
        if (list.Count == 0) return null;

        // 手动模式：固定用用户指定的那个
        if (Manager is not null && _settings.SelectionMode == JavaSelectionMode.Manual &&
            !string.IsNullOrWhiteSpace(_settings.PreferredJavaId))
        {
            var pinned = list.FirstOrDefault(j => j.Id == _settings.PreferredJavaId);
            if (pinned is not null) return pinned;
        }

        return JavaManager.Recommend(requiredMajor, list, _settings.RuntimePreference);
    }

    public static int RequiredMajor(VersionJson? version)
    {
        if (version?.JavaVersion?.MajorVersion > 0) return version.JavaVersion.MajorVersion;
        var id = version?.Id ?? "";
        var major = RequiredMajorByGameId(id);
        // 保底：算不出来（版本 JSON 缺 javaVersion、id 也不是标准格式）时按现代版本给 21，
        // 否则会去找"Java 0"这种根本不存在的运行时。
        return major > 0 ? major : 21;
    }

    public static int RequiredMajorByGameId(string gameVersion)
    {
        var id = (gameVersion ?? "").Trim();
        var parts = id.Split('.');

        // 正式版：1.16 → 8，1.17 → 16，1.18/1.19 → 17，1.20 及以上 → 21
        if (parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor))
        {
            if (major != 1) return 21;
            if (minor >= 20) return 21;
            if (minor >= 18) return 17;
            if (minor >= 17) return 16;
            return 8;
        }

        // 快照：25w31a → 年份 25 → 对应 1.21.x 之后的世代，需要 21
        // 26wxx 起按官方计划需要更高版本，这里保守取 21（真实值以版本 JSON 的 javaVersion 为准）
        var m = System.Text.RegularExpressions.Regex.Match(id, @"^(\d{2})w\d+[a-z]$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var year))
            return year >= 26 ? 25 : 21;

        return 8;
    }

    /// <summary>下载 Mojang 官方 Java 运行时（走当前镜像），返回可执行文件目录。</summary>
    public async Task<List<DownloadItem>> BuildRuntimeDownloadItemsAsync(string component,
        JavaArch arch = JavaArch.Unknown, CancellationToken token = default)
    {
        var items = new List<DownloadItem>();
        var provider = _mirrors.For(ResourceCategory.JavaRuntime);
        var indexUrl = provider.JavaRuntimeIndexUrl();
        var index = await _http.GetJsonAsync<JavaRuntimeIndex>(indexUrl, token).ConfigureAwait(false);
        if (index is null) throw new IOException(L.T("String.Core.Err.Java.NoIndex"));

        // 平台键按用户选定的架构拼（默认由已有 Java 的架构推断，绝不硬编码 x64）
        if (arch == JavaArch.Unknown) arch = JavaArchitecture.Suggest(Manager?.Javas);
        var platformKey = JavaArchitecture.PlatformKey(JavaArchitecture.OsPrefix, arch);
        var osEntry = index.Get(platformKey);
        if (osEntry is null)
            throw new IOException(L.F("String.Core.Err.Java.NoPlatformKey", platformKey));

        if (!osEntry.TryGetValue(component, out var runtime) || runtime is null || runtime.Count == 0)
            throw new IOException(L.F("String.Core.Err.Java.NoComponent", component));

        var manifestUrl = runtime[0].Manifest?.Url;
        if (string.IsNullOrEmpty(manifestUrl)) throw new IOException(L.T("String.Core.Err.Java.NoManifestUrl"));
        manifestUrl = provider.Rewrite(manifestUrl!);

        var manifest = await _http.GetJsonAsync<JavaRuntimeManifest>(manifestUrl, token).ConfigureAwait(false);
        if (manifest?.Files is null) throw new IOException(L.T("String.Core.Err.Java.ManifestFailed"));

        var targetRoot = Path.Combine(RuntimeRoot, component, platformKey);
        foreach (var (path, file) in manifest.Files)
        {
            if (file?.Downloads?.Raw?.Url is null) continue;
            var destination = Path.Combine(targetRoot, path.Replace('/', Path.DirectorySeparatorChar));
            items.Add(new DownloadItem
            {
                Name = Path.GetFileName(path),
                Url = provider.Rewrite(file.Downloads.Raw.Url),
                Destination = destination,
                Sha1 = file.Downloads.Raw.Sha1,
                ExpectedSize = file.Downloads.Raw.Size,
                Category = ResourceCategory.JavaRuntime,
                Group = L.T("String.Core.Group.JavaRuntime")
            });
        }
        return items;
    }

    /// <summary>拉取远程索引里实际存在的平台键（供 UI 列出可选架构）。</summary>
    public async Task<IReadOnlyList<string>> AvailableRuntimePlatformsAsync(CancellationToken token = default)
    {
        try
        {
            var provider = _mirrors.For(ResourceCategory.JavaRuntime);
            var index = await _http.GetJsonAsync<JavaRuntimeIndex>(provider.JavaRuntimeIndexUrl(), token)
                .ConfigureAwait(false);
            return index?.AvailablePlatforms ?? Array.Empty<string>();
        }
        catch (IOException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// 列出可供下载的架构选项：取「本平台常见架构 ∩ 远程索引里真实存在」，
    /// 并标出推荐项（依据已有 Java 的架构）。调用方（UI / CLI）必须让用户确认后再下载。
    /// </summary>
    public async Task<List<JavaArchOption>> RuntimeArchOptionsAsync(CancellationToken token = default)
    {
        var available = await AvailableRuntimePlatformsAsync(token).ConfigureAwait(false);
        var prefix = JavaArchitecture.OsPrefix;
        var suggested = JavaArchitecture.Suggest(Manager?.Javas);

        var options = new List<JavaArchOption>();
        foreach (var arch in JavaArchitecture.CandidatesFor(prefix))
        {
            var key = JavaArchitecture.PlatformKey(prefix, arch);
            if (!available.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            options.Add(new JavaArchOption
            {
                Arch = arch,
                PlatformKey = key,
                Name = JavaArchitecture.DisplayName(arch),
                IsSuggested = arch == suggested
            });
        }

        // 一个都没匹配上（索引结构变了）→ 直接把远程存在的键列出来，仍然让用户选
        if (options.Count == 0)
        {
            foreach (var key in available.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new JavaArchOption
                {
                    Arch = JavaArch.Unknown,
                    PlatformKey = key,
                    Name = key,
                    IsSuggested = key.Equals(JavaArchitecture.PlatformKey(prefix, suggested),
                        StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        return options.OrderByDescending(o => o.IsSuggested).ToList();
    }

    /// <summary>返回运行时目录下的 java 可执行文件路径（Windows 用 javaw.exe）。按平台键查找。</summary>
    public string? FindRuntimeJava(string component, JavaArch arch = JavaArch.Unknown)
    {
        if (arch == JavaArch.Unknown) arch = JavaArchitecture.Suggest(Manager?.Javas);
        var platformKey = JavaArchitecture.PlatformKey(JavaArchitecture.OsPrefix, arch);
        var root = Path.Combine(RuntimeRoot, component, platformKey);
        var exe = OperatingSystem.IsWindows() ? "javaw.exe" : "java";
        var direct = Path.Combine(root, "bin", exe);
        if (File.Exists(direct)) return direct;
        var home = Path.Combine(root, "jre", "bin", exe);
        if (File.Exists(home)) return home;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, exe, SearchOption.AllDirectories))
                return file;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    // ── 运行时索引模型 ──────────────────────────────────────
    /// <summary>
    /// Mojang 运行时索引。顶层键是「系统-架构」连写形式（windows-x64 / mac-os-arm64 / linux），
    /// 且不同平台命名不统一（linux 的 64 位就叫 linux，32 位才叫 linux-i386）。
    /// 这里用字典整体接收，避免漏键。
    /// </summary>
    private sealed class JavaRuntimeIndex
    {
        public Dictionary<string, Dictionary<string, List<JavaRuntimeEntry>>> Platforms { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>按完整平台键取值，如 "windows-x64"。</summary>
        public Dictionary<string, List<JavaRuntimeEntry>>? Get(string platformKey)
        {
            if (Platforms.TryGetValue(platformKey, out var v) && v is not null && v.Count > 0) return v;
            return null;
        }

        /// <summary>该索引里实际存在哪些平台键（供用户挑选架构时过滤不存在的组合）。</summary>
        public IReadOnlyList<string> AvailablePlatforms =>
            Platforms.Where(p => p.Value is { Count: > 0 }).Select(p => p.Key).ToList();
    }

    private sealed class JavaRuntimeEntry
    {
        [JsonPropertyName("manifest")] public JavaRuntimeArtifact? Manifest { get; set; }
        [JsonPropertyName("version")] public JavaRuntimeVersion? Version { get; set; }
    }

    private sealed class JavaRuntimeVersion
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("released")] public string? Released { get; set; }
    }

    private sealed class JavaRuntimeArtifact
    {
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class JavaRuntimeManifest
    {
        [JsonPropertyName("files")] public Dictionary<string, JavaRuntimeFile>? Files { get; set; }
    }

    private sealed class JavaRuntimeFile
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("executable")] public bool Executable { get; set; }
        [JsonPropertyName("downloads")] public JavaRuntimeDownloads? Downloads { get; set; }
    }

    private sealed class JavaRuntimeDownloads
    {
        [JsonPropertyName("raw")] public JavaRuntimeArtifact? Raw { get; set; }
        [JsonPropertyName("lzma")] public JavaRuntimeArtifact? Lzma { get; set; }
    }
}


/// <summary>可下载的架构选项（下载前必须让用户确认）。</summary>
public sealed class JavaArchOption
{
    public JavaArch Arch { get; set; }
    public string PlatformKey { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>是否为启动器依据已有 Java 推断出的推荐项。</summary>
    public bool IsSuggested { get; set; }

    public string Display => IsSuggested ? Name + " " + L.T("String.Java.Arch.Suggested") : Name;
}
