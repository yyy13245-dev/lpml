using LPML.Core.Game.Models;
using LPML.Core.Mirror;
using LPML.Core.Config;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.Core.Game;

/// <summary>版本清单服务：可离线缓存，缓存文件放在根目录 cache 下。</summary>
public sealed class VersionManifestService
{
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly MirrorManager _mirrors;

    public VersionManifestService(AppPaths paths, HttpService http, MirrorManager mirrors)
    {
        _paths = paths;
        _http = http;
        _mirrors = mirrors;
    }

    private string ManifestCachePath => Path.Combine(_paths.CacheDirectory, "manifests", "version_manifest_v2.json");

    public async Task<VersionManifest?> GetManifestAsync(bool forceRefresh = false, CancellationToken token = default)
    {
        var cache = ManifestCachePath;
        if (!forceRefresh && File.Exists(cache))
        {
            var cached = Json.Load<VersionManifest>(cache);
            // 旧版本或同步不全的镜像可能缓存了缺愚人节构建的清单，别直接复用。
            if (cached is not null && cached.Versions.Count > 0 &&
                VersionClassifier.HasAnyAprilFools(cached.Versions)) return cached;
        }
        try
        {
            var manifest = await FetchManifestFromAsync(_mirrors.VersionManifestUrl(), token).ConfigureAwait(false);

            // 第三方镜像偶尔只同步近期版本，老快照 / 愚人节构建会整段缺失。
            // 官方清单一定含这些条目，缺了就补一份并合并，避免下载中心凭空少一批版本。
            if (manifest is null || !VersionClassifier.HasAnyAprilFools(manifest.Versions))
            {
                var officialUrl = _mirrors.RewriteWith(OfficialManifestUrl,
                    ResourceCategory.VersionManifest, MirrorKind.Official);
                var official = await FetchManifestFromAsync(officialUrl, token).ConfigureAwait(false);
                if (official is not null && official.Versions.Count > 0)
                {
                    Log.Info("Manifest", L.T("String.Core.Log.Manifest.MirrorIncomplete"));
                    manifest = MergeManifests(official, manifest);
                }
            }

            if (manifest is null || manifest.Versions.Count == 0) return LoadCacheOnly();
            FileHelper.EnsureDirectory(Path.GetDirectoryName(cache));
            Json.Save(cache, manifest);
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Warn("Manifest", L.T("String.Core.Log.Manifest.Fallback") + "：" + ex.Message);
            return LoadCacheOnly();
        }
    }

    private const string OfficialManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private async Task<VersionManifest?> FetchManifestFromAsync(string url, CancellationToken token)
    {
        try
        {
            var manifest = await _http.GetJsonAsync<VersionManifest>(url, token).ConfigureAwait(false);
            return manifest is not null && manifest.Versions.Count > 0 ? manifest : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Manifest", L.F("String.Core.Log.Manifest.FetchFailed", url) + "：" + ex.Message);
            return null;
        }
    }

    /// <summary>官方清单为准，镜像里多出来的条目按 ID 补在后面，不覆盖官方数据。</summary>
    private static VersionManifest MergeManifests(VersionManifest primary, VersionManifest? extra)
    {
        if (extra is null) return primary;
        var seen = new HashSet<string>(primary.Versions.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var version in extra.Versions)
            if (seen.Add(version.Id)) primary.Versions.Add(version);
        return primary;
    }

    private VersionManifest? LoadCacheOnly() => Json.Load<VersionManifest>(ManifestCachePath);

    /// <summary>
    /// 下载某个版本的官方 version json。当前源拿不到或内容不对时回退官方地址；
    /// 落盘保留原始 JSON 文本，避免模型序列化丢掉快照 / 愚人节版本里的额外字段。
    /// </summary>
    public async Task<VersionJson?> FetchVersionJsonAsync(ManifestVersion entry, CancellationToken token = default)
    {
        var officialUrl = entry.Url;
        if (string.IsNullOrWhiteSpace(officialUrl)) return null;

        var candidates = new List<string> { _mirrors.For(ResourceCategory.VersionJson).Rewrite(officialUrl) };
        var official = _mirrors.RewriteWith(officialUrl, ResourceCategory.VersionJson, MirrorKind.Official);
        if (!candidates.Contains(official, StringComparer.OrdinalIgnoreCase)) candidates.Add(official);

        string? text = null;
        VersionJson? json = null;
        // id 不完全一致时不当即判死：个别镜像会改写 id 大小写或带前缀，
        // 能解析成 VersionJson 就先留作兜底，免得整批候选都被跳过。
        string? fallbackText = null;
        VersionJson? fallbackJson = null;
        foreach (var url in candidates)
        {
            try
            {
                var candidate = await _http.GetStringAsync(url, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var parsed = Json.Deserialize<VersionJson>(candidate);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Id)) continue;
                if (!string.IsNullOrWhiteSpace(entry.Sha1) &&
                    !string.Equals(HashHelper.Sha1String(candidate), entry.Sha1.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    Log.Warn("Manifest", L.F("String.Core.Log.Manifest.VersionShaMismatch", entry.Id));

                if (!string.Equals(parsed.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("Manifest", L.F("String.Core.Log.Manifest.VersionIdMismatch",
                        entry.Id, parsed.Id ?? ""));
                    if (fallbackText is null) { fallbackText = candidate; fallbackJson = parsed; }
                    continue;
                }

                text = candidate;
                json = parsed;
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("Manifest", L.F("String.Core.Log.Manifest.FetchFailed", url) + "：" + ex.Message);
            }
        }

        if (text is null && fallbackText is not null) { text = fallbackText; json = fallbackJson; }
        if (text is null || json is null) return null;
        var path = _paths.VersionJsonPath(entry.Id);
        FileHelper.EnsureDirectory(Path.GetDirectoryName(path));
        await File.WriteAllTextAsync(path, text, token).ConfigureAwait(false);
        return json;
    }
}
