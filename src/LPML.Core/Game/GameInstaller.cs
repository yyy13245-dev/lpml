using LPML.Core.Download;
using LPML.Core.Game.Models;
using LPML.Core.Mirror;
using LPML.Core.Config;
using LPML.Core.Utils;
using System.IO.Compression;

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
            // 早期版本（含部分愚人节构建）只给 assets 版本名、不给 assetIndex 对象。
            // 旧逻辑会拼出 .../packages//1.8.json 这种必然 404 的地址，这里跳过并留日志，
            // 游戏本体与依赖库照常安装，不让一个资源索引把整批下载卡住。
            if (string.IsNullOrEmpty(indexUrl) && !string.IsNullOrEmpty(version.AssetIndex?.Sha1))
                indexUrl = _mirrors.For(ResourceCategory.AssetsIndex)
                    .Rewrite($"https://piston-meta.mojang.com/v1/packages/{version.AssetIndex!.Sha1}/{assetIndexId}.json");

            if (string.IsNullOrEmpty(indexUrl))
            {
                Log.Warn("Install", L.F("String.Core.Log.Install.AssetIndexMissing", versionId));
            }
            else
            {
                var indexPath = Path.Combine(_paths.AssetsIndexesDirectory, assetIndexId + ".json");
                bool needDownload = true;
                if (File.Exists(indexPath) && !string.IsNullOrEmpty(version.AssetIndex?.Sha1))
                    needDownload = !string.Equals(HashHelper.Sha1File(indexPath), version.AssetIndex!.Sha1, StringComparison.OrdinalIgnoreCase);

                if (needDownload)
                {
                    items.Add(new DownloadItem
                    {
                        Name = L.F("String.Core.Item.AssetsIndex", assetIndexId),
                        Url = _mirrors.For(ResourceCategory.AssetsIndex).Rewrite(indexUrl),
                        OriginalUrl = indexUrl!,
                        Destination = indexPath,
                        Sha1 = version.AssetIndex?.Sha1,
                        ExpectedSize = version.AssetIndex?.Size ?? 0,
                        Category = ResourceCategory.AssetsIndex,
                        Group = L.T("String.Core.Group.Assets")
                    });
                }

                var index = Json.Load<AssetIndex>(indexPath);
                if (index is null)
                {
                    var text = await _http.GetStringAsync(_mirrors.For(ResourceCategory.AssetsIndex).Rewrite(indexUrl), token).ConfigureAwait(false);
                    index = Json.Deserialize<AssetIndex>(text);
                    if (index is not null && !string.IsNullOrEmpty(text)) FileHelper.WriteAllTextAsync(indexPath, text).GetAwaiter().GetResult();
                }

                if (index is not null)
                    items.AddRange(BuildAssetItems(index));
            }
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

    /// <summary>
    /// 列出加载器 profile 自带的依赖库（相对原版清单的增量）。
    /// 不做本地存在性过滤：已存在的文件交给下载器标成「已拥有」，
    /// 这样任务详情里始终能看到「加载器内容」这一分区，而不是被误认为没装。
    /// 也不带客户端 jar / 资源索引，避免按合并版本 ID 重复下载一份游戏本体。
    /// </summary>
    public List<DownloadItem> BuildLoaderLibraryItems(VersionJson profile, VersionJson vanilla)
    {
        var vanillaPaths = new HashSet<string>(
            BuildLibraryItems(vanilla).Select(i => i.Destination),
            StringComparer.OrdinalIgnoreCase);

        var items = BuildLibraryItems(profile)
            .Where(i => !string.IsNullOrWhiteSpace(i.Destination) && !vanillaPaths.Contains(i.Destination))
            .ToList();
        foreach (var item in items)
        {
            item.Section = DownloadSections.LoaderContent;
            item.Group = L.T("String.Core.Group.LoaderLibraries");
        }
        return items;
    }

    /// <summary>检查某个版本是否完整（用于启动前检查 / 修复）。</summary>
    public async Task<List<DownloadItem>> BuildRepairItemsAsync(VersionJson version, CancellationToken token = default)
    {
        var items = await BuildInstallItemsAsync(version, true, token).ConfigureAwait(false);
        // 清单里 client jar / 依赖库是无条件加入的，只有 assets 做了「已存在就跳过」。
        // 于是启动前检查永远返回一长串「缺失文件」，下载器瞬间全部跳过，
        // 二次检查仍旧不为 0，启动被反复中止。这里统一按「存在 + 校验通过」过滤。
        var result = items.Where(NeedsDownload).ToList();

        var libraries = BuildLibraryItems(version).ToList();
        var byDestination = new Dictionary<string, DownloadItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in libraries)
            if (!string.IsNullOrWhiteSpace(item.Destination)) byDestination[item.Destination!] = item;

        // 下载环节可能留下「存在但根本打不开」的 jar（截断的 ZIP、空响应、404 页面）。
        // NeedsDownload 只看 sha1 / size，而第三方加载器库两者都没有，于是坏文件被
        // 永久当成完整；启动前预处理必须自己把它挑出来强制重下，否则 classpath 里全是废文件。
        var force = new List<DownloadItem>();
        foreach (var (dest, item) in byDestination)
        {
            if (!File.Exists(dest) || FileHelper.IsUsableLibraryFile(dest)) continue;
            MarkForceRedownload(item);
            force.Add(item);
            Log.Warn("Install", "[Preflight] broken jar, force re-download: " + dest);
        }

        // 客户端 jar 不在 BuildLibraryItems 里，单独查一次。
        var versionJar = _paths.VersionJarPath(version.Id ?? "");
        if (File.Exists(versionJar) && !FileHelper.IsUsableLibraryFile(versionJar))
        {
            var jarItem = items.FirstOrDefault(i => string.Equals(i.Destination, versionJar,
                StringComparison.OrdinalIgnoreCase));
            if (jarItem is not null)
            {
                MarkForceRedownload(jarItem);
                force.Add(jarItem);
                Log.Warn("Install", "[Preflight] broken client jar, force re-download: " + versionJar);
            }
        }

        var main = version.MainClass;
        var mainOk = MainClassAvailable(version, byDestination.Keys, out var mainJar);
        if (!string.IsNullOrWhiteSpace(main) && !mainOk)
        {
            // 主类一个 jar 里都找不到。旧逻辑只按文件名匹配 fabric-loader / forge，
            // 漏掉 sponge-mixin、asm 以及镜像改写后的路径，重下一遍仍然缺主类。
            // 现在把所有第三方库整体重下，并优先走官方地址，避免镜像一直返回同一个坏包。
            foreach (var item in libraries)
                if (IsThirdPartyLibrary(item) && !string.IsNullOrWhiteSpace(item.Destination))
                {
                    MarkForceRedownload(item);
                    force.Add(item);
                }
            Log.Warn("Install", "[Preflight] main class " + main + " missing; force re-download " + force.Count + " item(s)");
        }
        else
        {
            Log.Info("Install", "[Preflight] " + (version.Id ?? "") +
                " main=" + (main ?? "(none)") +
                " jars=" + byDestination.Count +
                " broken=" + force.Count +
                " mainJar=" + (mainJar ?? "none"));
        }

        foreach (var item in force)
            if (!result.Any(i => string.Equals(i.Destination, item.Destination,
                    StringComparison.OrdinalIgnoreCase)))
                result.Add(item);
        return result;
    }

    private static void MarkForceRedownload(DownloadItem item)
    {
        item.ForceRedownload = true;
        item.Section = DownloadSections.LoaderContent;
        item.Group = L.T("String.Core.Group.LoaderLibraries");
        // 镜像可能一直返回「格式合法但内容不对」的同一个文件，先用官方地址重拉。
        if (!string.IsNullOrWhiteSpace(item.OriginalUrl)) item.Url = item.OriginalUrl!;
    }

    /// <summary>第三方加载器库：没有 sha1/size，普通校验最容易放过坏文件。</summary>
    private static bool IsThirdPartyLibrary(DownloadItem item)
    {
        var dest = item.Destination ?? "";
        if (dest.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
            dest.Contains("quilt", StringComparison.OrdinalIgnoreCase) ||
            dest.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
            dest.Contains("mixin", StringComparison.OrdinalIgnoreCase) ||
            dest.Contains("optifine", StringComparison.OrdinalIgnoreCase)) return true;
        var url = item.OriginalUrl ?? item.Url ?? "";
        return url.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("quiltmc", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("neoforged", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>启动主类对应的 .class 是否真的存在于 classpath 的某个 jar 里。</summary>
    private bool MainClassAvailable(VersionJson version, IEnumerable<string> libraryJars, out string? foundIn)
    {
        foundIn = null;
        var main = version.MainClass;
        if (string.IsNullOrWhiteSpace(main)) return true;
        var entry = main.Replace('.', '/') + ".class";

        var candidates = new List<string>();
        var versionId = version.Id ?? "";
        if (!string.IsNullOrWhiteSpace(versionId)) candidates.Add(_paths.VersionJarPath(versionId));
        candidates.AddRange(libraryJars);

        foreach (var jar in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!FileHelper.IsUsableLibraryFile(jar)) continue;
            try
            {
                using var zip = ZipFile.OpenRead(jar);
                if (zip.GetEntry(entry) is not null) { foundIn = jar; return true; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warn("Install", L.F("String.Core.Log.Install.JarInspectFailed", jar, ex.Message));
            }
        }
        return false;
    }

    private static bool NeedsDownload(DownloadItem item)
    {
        if (item.ForceRedownload) return true;
        var path = item.Destination;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return true;
        try
        {
            if (!FileHelper.IsUsableLibraryFile(path)) return true;
            if (!string.IsNullOrWhiteSpace(item.Sha1))
                return !string.Equals(HashHelper.Sha1File(path), item.Sha1.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            if (item.ExpectedSize is > 0)
                return new FileInfo(path).Length != item.ExpectedSize.Value;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        return false;
    }

    public bool IsJarPresent(string versionId) => File.Exists(_paths.VersionJarPath(versionId));
}
