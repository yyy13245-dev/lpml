using LPML.Core.Config;

namespace LPML.Core.Mirror;

/// <summary>
/// 镜像管理器：按"资源分类"解析下载源。用户既可以一键切换全局源，
/// 也可以对版本清单、资源文件、依赖库、Forge、Fabric 等分别指定源。
/// </summary>
public sealed class MirrorManager
{
    private readonly Dictionary<MirrorKind, MirrorProvider> _providers = new();
    private readonly MirrorSettings _settings;

    public MirrorManager(MirrorSettings settings)
    {
        _settings = settings;
        // MCBBS 已停运：旧配置里若还存着它，静默归一化，别让用户一启动就撞死域名
        if (_settings.NormalizeLegacyMirrors())
            Log.Info("Mirror", L.T("String.Core.Log.Mirror.LegacyMcbbs"));
        Refresh();
    }

    public void Refresh()
    {
        _providers.Clear();
        foreach (var kind in MirrorSettings.SupportedKinds)
        {
            _settings.CustomBaseUrls.TryGetValue(kind, out var custom);
            _providers[kind] = new MirrorProvider(kind, custom);
        }
    }

    public MirrorProvider Get(MirrorKind kind)
    {
        // 最后一兜底：任何漏网的旧值都别再指向那个不解析的域名
        if (kind == MirrorKind.MCBBS) kind = MirrorKind.BMCLAPI;
        return _providers.TryGetValue(kind, out var p) ? p : new MirrorProvider(kind);
    }

    public MirrorProvider For(ResourceCategory category) => Get(_settings.Resolve(category));

    public string Rewrite(string url, ResourceCategory category) => For(category).Rewrite(url);

    /// <summary>
    /// 把「官方地址」按指定镜像改写。用于镜像缺文件时换源重试。
    /// 传 Official 就等于还原成官方地址。
    /// </summary>
    public string RewriteWith(string officialUrl, ResourceCategory category, MirrorKind kind)
    {
        if (string.IsNullOrWhiteSpace(officialUrl)) return officialUrl ?? "";
        if (_providers.TryGetValue(kind, out var provider)) return provider.Rewrite(officialUrl);
        return officialUrl;
    }

    /// <summary>
    /// 换源重试的候选顺序：当前源之外的其他镜像，官方源兜底。
    /// 镜像缺文件（403/404）时按这个顺序逐个试，而不是对着同一个源死磕。
    /// </summary>
    public List<MirrorKind> FailoverOrder(ResourceCategory category, string? officialUrl = null)
    {
        var current = _settings.Resolve(category);
        // MCBBS 已停止服务，彻底不参与故障转移；只从当前可用源里挑。
        var others = MirrorSettings.SupportedKinds.Where(k => k != current).ToList();

        // 第三方 maven（Fabric / Forge / Quilt / NeoForge）的构件在 BMCLAPI 的
        // /maven 下经常被 WAF 判 403：官方文档与 HMCL 都确认路径是 /maven，
        // 镜像上也确实同步了 fabric-loader，所以不是路径错、也不是没同步，
        // 而是它对这些构件的请求特征（UA / 302 签名节点 / Range）拦截较严。
        // 这类构件干脆不进 BMCLAPI 候选：光速镜像有专属域名，官方源一定有文件。
        // 否则 403 之后外层继续在这个域名上退避重试，很快就会撞成 429。
        if (!string.IsNullOrWhiteSpace(officialUrl)
            && Uri.TryCreate(officialUrl, UriKind.Absolute, out var uri)
            && (uri.Host.EndsWith("fabricmc.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("minecraftforge.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("neoforged.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("quiltmc.org", StringComparison.OrdinalIgnoreCase)))
        {
            others = others.Where(k => k != MirrorKind.BMCLAPI).ToList();
            static int Rank(MirrorKind k) => k switch
            {
                MirrorKind.FastMCMirror => 0,
                MirrorKind.Official => 1,
                _ => 99
            };
            return others.OrderBy(Rank).ToList();
        }

        // 资源文件（assets）第三方镜像普遍同步不全或已关闭镜像，
        // 官方 CDN 一定有文件，优先兜底，别让几千个 ogg 逐个试错。
        if (current != MirrorKind.Official && category == ResourceCategory.Assets)
            others = others.OrderBy(k => k == MirrorKind.Official ? 0 : 1).ToList();
        else
            others = others.OrderBy(k => k == MirrorKind.Official ? 1 : 0).ToList();

        return others;
    }

    /// <summary>测速结果落到全局默认源；分类覆盖仍优先，不覆盖用户的单独指定。</summary>
    public void ApplyFastest(MirrorKind kind)
    {
        if (_settings.Default == kind) return;
        _settings.Default = kind;
        Refresh();
    }

    public string VersionManifestUrl() => For(ResourceCategory.VersionManifest).VersionManifestUrl();

    /// <summary>
    /// 测速：并发探测各源的可用性，返回按耗时排序的可用源（用于"自动选择最快镜像"）。
    /// </summary>
    public async Task<List<(MirrorKind Kind, long ElapsedMs)>> MeasureAsync(
        Func<string, CancellationToken, Task> probe,
        CancellationToken token = default)
    {
        var results = new List<(MirrorKind, long)>();
        var tasks = MirrorSettings.SupportedKinds.Select(async kind =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await probe(Get(kind).VersionManifestUrl(), token).ConfigureAwait(false);
                sw.Stop();
                return (kind, sw.ElapsedMilliseconds);
            }
            catch
            {
                return (kind, long.MaxValue);
            }
        });
        var all = await Task.WhenAll(tasks).ConfigureAwait(false);
        results.AddRange(all.Where(x => x.Item2 < long.MaxValue).OrderBy(x => x.Item2));
        return results;
    }
}
