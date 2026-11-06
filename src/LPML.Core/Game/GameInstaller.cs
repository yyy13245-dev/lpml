using LPML.Core.Download;
using LPML.Core.Game.Models;
using LPML.Core.Mirror;
using LPML.Core.Config;
using LPML.Core.Utils;

namespace LPML.Core.Game;

/// <summary>
/// 原版安装器：把版本 JSON、客户端 jar、依赖库（含 natives）、资源索引与资源文件
/// 全部转成 DownloadItem，交给 DownloadManager 做多任务 + 多线程下载。
/// </summary>
public sealed class GameInstaller
{
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly MirrorManager _mirrors;
    private readonly DownloadSettings _settings;

    public GameInstaller(AppPaths paths, HttpService http, MirrorManager mirrors, DownloadSettings settings)
    {
        _paths = paths;
        _http = http;
        _mirrors = mirrors;
        _settings = settings;
    }

    /// <summary>构建完整下载清单（已存在且校验通过的文件会被自动跳过）。</summary>
    public async Task<List<DownloadItem>> BuildInstallItemsAsync(
        VersionJson version, bool includeAssets = true, CancellationToken token = default)
    {
        var items = await BuildInstallItemsCoreAsync(version, includeAssets, token).ConfigureAwait(false);
        // 这批文件本质都是原版游戏文件；整合包安装时和模组混在一个任务里，
        // 任务详情靠这个标记把它们分到「原版内容」分区。
        foreach (var item in items) item.Section = DownloadSections.VanillaContent;
        return items;
    }

    private async Task<List<DownloadItem>> BuildInstallItemsCoreAsync(
        VersionJson version, bool includeAssets, CancellationToken token)
    {
        using var _perf = PhaseTimer.Measure(L.F("String.Core.Perf.BuildManifest", version.Id ?? ""));
        var items = new List<DownloadItem>();
        var versionId = version.Id ?? "";
        if (string.IsNullOrEmpty(versionId)) return items;

        // 1. 客户端 jar
        if (version.Downloads?.TryGetValue("client", out var client) == true && !string.IsNullOrEmpty(client.Url))
        {
            items.Add(new DownloadItem
            {
                Name = L.F("String.Core.Item.ClientJar", versionId),
                Url = _mirrors.For(ResourceCategory.ClientJar).Rewrite(client.Url!),
                OriginalUrl = client.Url ?? "",
                Destination = _paths.VersionJarPath(versionId),
                Sha1 = client.Sha1,
                ExpectedSize = client.Size,
                Category = ResourceCategory.ClientJar,
                Group = L.T("String.Core.Group.GameCore")
            });
        }

        // 2. 依赖库
        items.AddRange(BuildLibraryItems(version));

        if (!includeAssets) return items;

        // 3. 资源索引
        var assetIndexId = version.AssetIndex?.Id ?? version.Assets;
        if (!string.IsNullOrEmpty(assetIndexId))
        {
            var indexUrl = version.AssetIndex?.Url;
            if (string.IsNullOrEmpty(indexUrl))
                indexUrl = _mirrors.For(ResourceCategory.AssetsIndex)
                    .Rewrite($"https://piston-meta.mojang.com/v1/packages/{version.AssetIndex?.Sha1}/{assetIndexId}.json");

            var indexPath = Path.Combine(_paths.AssetsIndexesDirectory, assetIndexId + ".json");
            bool needDownload = true;
            if (File.Exists(indexPath) && !string.IsNullOrEmpty(version.AssetIndex?.Sha1))
                needDownload = !string.Equals(HashHelper.Sha1File(indexPath), version.AssetIndex!.Sha1, StringComparison.OrdinalIgnoreCase);

            if (needDownload)
            {
                items.Add(new DownloadItem
                {
                    Name = L.F("String.Core.Item.AssetsIndex", assetIndexId),
                    Url = _mirrors.For(ResourceCategory.AssetsIndex).Rewrite(indexUrl!),
                    OriginalUrl = indexUrl ?? "",
                    Destination = indexPath,
                    Sha1 = version.AssetIndex?.Sha1,
                    ExpectedSize = version.AssetIndex?.Size ?? 0,
                    Category = ResourceCategory.AssetsIndex,
                    Group = L.T("String.Core.Group.Assets")
                });
            }

            var index = Json.Load<AssetIndex>(indexPath);
            if (index is null && !string.IsNullOrEmpty(indexUrl))
            {
                var text = await _http.GetStringAsync(_mirrors.For(ResourceCategory.AssetsIndex).Rewrite(indexUrl!), token).ConfigureAwait(false);
                index = Json.Deserialize<AssetIndex>(text);
                if (index is not null && !string.IsNullOrEmpty(text)) FileHelper.WriteAllTextAsync(indexPath, text).GetAwaiter().GetResult();
            }

            if (index is not null)
                items.AddRange(BuildAssetItems(index));
        }

        return items;
    }

    private IEnumerable<DownloadItem> BuildLibraryItems(VersionJson version)
    {
        var list = new List<DownloadItem>();
        if (version.Libraries is null) return list;

        foreach (var lib in version.Libraries)
        {
            if (lib is null) continue;
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;
            if (string.IsNullOrWhiteSpace(lib.Name)) continue;

            var (pureName, ext) = MavenHelper.SplitExtension(lib.Name);

            // 主 artifact
            var artifact = lib.Downloads?.Artifact;
            var artifactPath = artifact?.Path ?? MavenHelper.ToPath(pureName, null, ext);
            var artifactUrl = artifact?.Url;
            if (string.IsNullOrEmpty(artifactUrl))
            {
                var baseUrl = string.IsNullOrEmpty(lib.Url)
                    ? "https://libraries.minecraft.net/"
                    : (lib.Url.EndsWith('/') ? lib.Url : lib.Url + "/");
                artifactUrl = baseUrl + artifactPath.Replace('\\', '/');
            }
            var officialArtifactUrl = artifactUrl ?? "";
            artifactUrl = _mirrors.For(ResourceCategory.Libraries).Rewrite(artifactUrl!);

            // 仅有 natives 分类、没有主 artifact 的库不需要下载主 jar
            var nativesOnly = artifact is null && lib.Natives is not null &&
                              !string.IsNullOrEmpty(MavenHelper.NativeClassifier(lib.Natives));
            if (!nativesOnly)
            {
                list.Add(new DownloadItem
                {
                    Name = Path.GetFileName(artifactPath),
                    Url = artifactUrl,
                    OriginalUrl = officialArtifactUrl,
                    Destination = _paths.LibraryPath(artifactPath),
                    Sha1 = artifact?.Sha1 ?? lib.Sha1,
                    ExpectedSize = artifact?.Size ?? lib.Size,
                    Category = ResourceCategory.Libraries,
                    Group = L.T("String.Core.Group.Libraries")
                });
            }

            // natives（Windows 平台的本地库）
            if (lib.Natives is not null)
            {
                var classifier = MavenHelper.NativeClassifier(lib.Natives);
                if (!string.IsNullOrEmpty(classifier) &&
                    lib.Downloads?.Classifiers?.TryGetValue(classifier, out var nativeArtifact) == true &&
                    nativeArtifact is not null && !string.IsNullOrEmpty(nativeArtifact.Url))
                {
                    var nativePath = nativeArtifact.Path ?? MavenHelper.ToPath(pureName, classifier, "jar");
                    list.Add(new DownloadItem
                    {
                        Name = Path.GetFileName(nativePath) + " (natives)",
                        Url = _mirrors.For(ResourceCategory.Libraries).Rewrite(nativeArtifact.Url!),
                        OriginalUrl = nativeArtifact.Url ?? "",
                        Destination = _paths.LibraryPath(nativePath),
                        Sha1 = nativeArtifact.Sha1,
                        ExpectedSize = nativeArtifact.Size,
                        Category = ResourceCategory.Libraries,
                        Group = L.T("String.Core.Group.Natives")
                    });
                }
            }
        }
        return list;
    }

    private IEnumerable<DownloadItem> BuildAssetItems(AssetIndex index)
    {
        var list = new List<DownloadItem>();
        foreach (var (name, obj) in index.Objects)
        {
            if (obj is null || string.IsNullOrEmpty(obj.Hash)) continue;
            var hash = obj.Hash;
            var destination = _paths.AssetObjectPath(hash);
            if (_settings.SkipExistingFiles && File.Exists(destination)) continue;
            var officialUrl = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
            var url = _mirrors.For(ResourceCategory.Assets).Rewrite(officialUrl);
            list.Add(new DownloadItem
            {
                Name = Path.GetFileName(name),
                Url = url,
                OriginalUrl = officialUrl,
                Destination = destination,
                Sha1 = hash,
                ExpectedSize = obj.Size,
                Category = ResourceCategory.Assets,
                Group = L.T("String.Core.Group.Assets")
            });
        }
        return list;
    }

    /// <summary>检查某个版本是否完整（用于启动前检查 / 修复）。</summary>
    public async Task<List<DownloadItem>> BuildRepairItemsAsync(VersionJson version, CancellationToken token = default)
        => await BuildInstallItemsAsync(version, true, token).ConfigureAwait(false);

    public bool IsJarPresent(string versionId) => File.Exists(_paths.VersionJarPath(versionId));
}
