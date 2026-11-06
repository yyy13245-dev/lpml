using LPML.Core.Download;
using LPML.Core.Config;

namespace LPML.Core.Modrinth;

/// <summary>
/// Modrinth（Labrinth）API v2 客户端。
/// 只接 Modrinth，不接 CurseForge——后者要申请 API key、有调用限制且条款更严。
/// Modrinth 完全够用：模组 / 资源包 / 光影 / 整合包 / 数据包 都在上面。
/// </summary>
public sealed class ModrinthClient
{
    private const string ApiBase = "https://api.modrinth.com/v2";

    private readonly HttpService _http;

    // Modrinth 对短时间内的高频请求会限流甚至封 IP。所有 API 调用共用一个节流器：
    // 请求之间至少隔 300ms，且排队按序放行，翻页 / 连点搜索都不会瞬间打出一串请求。
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(300);
    private readonly SemaphoreSlim _throttleGate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public ModrinthClient(HttpService http) => _http = http;

    public string ApiBaseUrl => ApiBase;

    /// <summary>User-Agent 必须带，Modrinth 会拒绝无标识的请求。</summary>
    public string UserAgent { get; set; } = "LPML-Launcher/1.0";

    /// <summary>占位一个发送时隙：并发请求也会排队，不会同时打向源站。</summary>
    private async Task ThrottleAsync(CancellationToken token)
    {
        await _throttleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var wait = MinRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _throttleGate.Release();
        }
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken token)
    {
        await ThrottleAsync(token).ConfigureAwait(false);
        return await _http.GetJsonAsync<T>(url, token).ConfigureAwait(false);
    }

    // ── 搜索 ───────────────────────────────────────────────

    /// <summary>
    /// 搜索。facets 用 Modrinth 的二维数组语法拼，如
    /// [["categories:fabric"],["versions:1.21.4"],["project_type:mod"]]
    /// </summary>
    public async Task<ModrinthSearchResponse> SearchAsync(string query, ModrinthProjectType type,
        string? gameVersion = null, string? loader = null, int offset = 0, int limit = 20,
        string sort = "downloads", CancellationToken token = default)
    {
        var url = $"{ApiBase}/search?query={Uri.EscapeDataString(query ?? "")}" +
                  $"&limit={limit}&offset={offset}&index={sort}";

        var facets = new List<string> { $"[\"project_type:{type.ApiName()}\"]" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
            facets.Add($"[\"versions:{gameVersion!.Trim()}\"]");
        if (!string.IsNullOrWhiteSpace(loader))
            facets.Add($"[\"categories:{loader!.Trim().ToLowerInvariant()}\"]");

        url += "&facets=" + Uri.EscapeDataString("[" + string.Join(",", facets) + "]");

        var result = await GetAsync<ModrinthSearchResponse>(url, token).ConfigureAwait(false);
        return result ?? new ModrinthSearchResponse { Hits = new List<ModrinthProject>(), TotalHits = 0 };
    }

    // ── 项目详情 ───────────────────────────────────────────

    public async Task<ModrinthProjectDetail?> GetProjectAsync(string slugOrId, CancellationToken token = default)
    {
        var url = $"{ApiBase}/project/{Uri.EscapeDataString(slugOrId)}";
        var detail = await GetAsync<ModrinthProjectDetail>(url, token).ConfigureAwait(false);
        if (detail is null) return null;

        try
        {
            var members = await GetAsync<List<ModrinthMember>>(
                $"{ApiBase}/project/{Uri.EscapeDataString(slugOrId)}/members", token).ConfigureAwait(false);
            if (members is not null) detail.Members = members;
        }
        catch (IOException) { }

        return detail;
    }

    /// <summary>
    /// 拉取全部版本。可指定 gameVersions / loaders 做服务端过滤。
    /// </summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(string slugOrId,
        string[]? gameVersions = null, string[]? loaders = null, CancellationToken token = default)
    {
        var url = $"{ApiBase}/project/{Uri.EscapeDataString(slugOrId)}/version";
        var query = new List<string>();
        if (gameVersions is { Length: > 0 })
            query.Add("game_versions=" + Uri.EscapeDataString("[\"" +
                string.Join("\",\"", gameVersions.Select(v => v.Replace("\"", ""))) + "\"]"));
        if (loaders is { Length: > 0 })
            query.Add("loaders=" + Uri.EscapeDataString("[\"" +
                string.Join("\",\"", loaders.Select(l => l.Replace("\"", ""))) + "\"]"));
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var list = await GetAsync<List<ModrinthVersion>>(url, token).ConfigureAwait(false);
        return list ?? new List<ModrinthVersion>();
    }

    /// <summary>
    /// 按 MC 版本分组。与当前实例版本一致的那组标为推荐并排到最前。
    /// </summary>
    public static List<ModrinthVersionGroup> GroupByGameVersion(List<ModrinthVersion> versions,
        string? preferredGameVersion = null)
    {
        var groups = new Dictionary<string, ModrinthVersionGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            if (version.GameVersions is null)
            {
                var key = L.T("String.Content.AnyVersion");
                AddTo(groups, key, version);
                continue;
            }
            foreach (var gameVersion in version.GameVersions)
                AddTo(groups, gameVersion, version);
        }

        var result = groups.Values.ToList();

        // 版本号排序：1.21.4 → 1.20.1 → 1.16.5，新的在前
        result.Sort((a, b) => CompareGameVersion(b.GameVersion, a.GameVersion));

        if (!string.IsNullOrWhiteSpace(preferredGameVersion))
        {
            var preferred = result.FirstOrDefault(g =>
                string.Equals(g.GameVersion, preferredGameVersion, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                preferred.IsRecommended = true;
                result.Remove(preferred);
                result.Insert(0, preferred);
            }
        }

        return result;
    }

    private static void AddTo(Dictionary<string, ModrinthVersionGroup> groups, string key, ModrinthVersion version)
    {
        if (!groups.TryGetValue(key, out var group))
        {
            group = new ModrinthVersionGroup { GameVersion = key };
            groups[key] = group;
        }
        if (!group.Versions.Any(v => v.Id == version.Id)) group.Versions.Add(version);
    }

    /// <summary>MC 版本号倒序（新→旧）。解析失败按字符串比较。</summary>
    public static int CompareGameVersion(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var na = i < pa.Length && int.TryParse(pa[i], out var va) ? va : -1;
            var nb = i < pb.Length && int.TryParse(pb[i], out var vb) ? vb : -1;
            if (na != nb) return nb.CompareTo(na);
        }
        return string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
    }

    // ── 下载 ───────────────────────────────────────────────

    /// <summary>把某个版本的主文件转成下载项。</summary>
    public DownloadItem ToDownloadItem(ModrinthVersion version, ModrinthProjectType type,
        string instanceDirectory, string projectTitle)
    {
        var file = version.PrimaryFile
                   ?? throw new IOException(L.T("String.Content.Err.NoFile"));

        var directory = Path.Combine(instanceDirectory, type.RelativeDirectory());
        var destination = Path.Combine(directory, file.Filename ?? version.Id + ".jar");

        return new DownloadItem
        {
            Name = $"{projectTitle} {version.Name}",
            Url = file.Url ?? throw new IOException(L.T("String.Content.Err.NoUrl")),
            Destination = destination,
            Sha1 = file.Sha1,
            ExpectedSize = file.Size > 0 ? file.Size : null,
            Category = ResourceCategory.Modrinth,
            Group = type.DisplayName()
        };
    }

    /// <summary>取依赖项目的 slug（dependencies 只给 project_id，需要反查）。</summary>
    public async Task<ModrinthProjectDetail?> GetDependencyAsync(string projectId, CancellationToken token = default)
        => await GetProjectAsync(projectId, token).ConfigureAwait(false);
}
