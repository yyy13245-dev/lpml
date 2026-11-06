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
            if (cached is not null && cached.Versions.Count > 0) return cached;
        }
        try
        {
            var url = _mirrors.VersionManifestUrl();
            var manifest = await _http.GetJsonAsync<VersionManifest>(url, token).ConfigureAwait(false);
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

    private VersionManifest? LoadCacheOnly() => Json.Load<VersionManifest>(ManifestCachePath);

    /// <summary>下载并保存某个版本的官方 version json，返回解析结果。</summary>
    public async Task<VersionJson?> FetchVersionJsonAsync(ManifestVersion entry, CancellationToken token = default)
    {
        var url = _mirrors.For(ResourceCategory.VersionJson).Rewrite(entry.Url);
        var json = await _http.GetJsonAsync<VersionJson>(url, token).ConfigureAwait(false);
        if (json is null) return null;
        var path = _paths.VersionJsonPath(entry.Id);
        FileHelper.EnsureDirectory(Path.GetDirectoryName(path));
        Json.Save(path, json);
        return json;
    }
}
