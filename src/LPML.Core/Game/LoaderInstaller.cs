using LPML.Core.Download;
using LPML.Core.Game.Models;
using LPML.Core.Mirror;
using System.Diagnostics;
using System.Text.Json.Serialization;
using LPML.Core.Config;
using LPML.Core.Utils;

namespace LPML.Core.Game;

public sealed class ForgeVersion
{
    public string McVersion { get; set; } = "";
    public string Version { get; set; } = "";
    public int Build { get; set; }
    public string? Branch { get; set; }
    public string? Modified { get; set; }
    public string Display => string.IsNullOrEmpty(Branch) ? Version : $"{Version} ({Branch})";
}

public sealed class NeoForgeVersion
{
    public string McVersion { get; set; } = "";
    public string Version { get; set; } = "";
    public string? RawVersion { get; set; }
    public string? InstallerPath { get; set; }
}

public sealed class FabricVersion
{
    public string Version { get; set; } = "";
    public bool Stable { get; set; }
    public string Display => Stable ? Version : Version + L.T("String.Core.Item.PreviewSuffix");
}

public sealed class OptiFineVersion
{
    public string McVersion { get; set; } = "";
    public string Type { get; set; } = "";
    public string Patch { get; set; } = "";
    public string? FileName { get; set; }
    public string? Forge { get; set; }

    /// <summary>镜像在列表里直接给出的下载直链（光速镜像有），有则优先使用。</summary>
    public string? Url { get; set; }

    public string Display => $"OptiFine {McVersion}_{Type}_{Patch}";
}

/// <summary>模组加载器安装：Fabric / Quilt / Forge / NeoForge / OptiFine。</summary>
public sealed class LoaderInstaller
{
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly MirrorManager _mirrors;

    public LoaderInstaller(AppPaths paths, HttpService http, MirrorManager mirrors)
    {
        _paths = paths;
        _http = http;
        _mirrors = mirrors;
    }

    // ── 列表 ───────────────────────────────────────────────
    public async Task<List<FabricVersion>> GetFabricLoadersAsync(string mcVersion, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.FabricMeta);
        var url = provider.FabricLoaderVersionsUrl(mcVersion);
        var raw = await _http.GetJsonAsync<List<FabricLoaderEntry>>(url, token).ConfigureAwait(false);
        var list = new List<FabricVersion>();
        foreach (var entry in raw ?? new List<FabricLoaderEntry>())
        {
            if (entry?.Loader is null) continue;
            list.Add(new FabricVersion { Version = entry.Loader.Version ?? "", Stable = entry.Loader.Stable });
        }
        return list;
    }

    public async Task<List<string>> GetQuiltLoadersAsync(CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.QuiltMeta);
        var url = provider.QuiltLoaderVersionsUrl();
        var raw = await _http.GetJsonAsync<List<QuiltLoaderEntry>>(url, token).ConfigureAwait(false);
        return (raw ?? new List<QuiltLoaderEntry>()).Select(x => x.Version ?? "").Where(x => x.Length > 0).ToList();
    }

    public async Task<List<ForgeVersion>> GetForgeVersionsAsync(string mcVersion, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.Forge);
        var list = new List<ForgeVersion>();
        try
        {
            var url = provider.ForgeListUrl(mcVersion);
            var raw = await _http.GetJsonAsync<List<ForgeEntry>>(url, token).ConfigureAwait(false);
            foreach (var entry in raw ?? new List<ForgeEntry>())
            {
                if (entry is null) continue;
                list.Add(new ForgeVersion
                {
                    McVersion = entry.McVersion ?? mcVersion,
                    Version = entry.Version ?? "",
                    Build = entry.Build ?? 0,
                    Branch = entry.Branch,
                    Modified = entry.Modified
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Forge", L.T("String.Core.Log.Loader.ForgeListFailed") + "：" + ex.Message);
        }
        list.Reverse();
        return list;
    }

    public async Task<List<NeoForgeVersion>> GetNeoForgeVersionsAsync(string mcVersion, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.NeoForge);
        var list = new List<NeoForgeVersion>();
        try
        {
            var url = provider.NeoForgeListUrl(mcVersion);
            var raw = await _http.GetJsonAsync<List<NeoForgeEntry>>(url, token).ConfigureAwait(false);
            foreach (var entry in raw ?? new List<NeoForgeEntry>())
            {
                if (entry is null) continue;
                list.Add(new NeoForgeVersion
                {
                    McVersion = entry.McVersion ?? mcVersion,
                    Version = entry.Version ?? "",
                    RawVersion = entry.RawVersion,
                    InstallerPath = entry.InstallerPath
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("NeoForge", L.T("String.Core.Log.Loader.NeoForgeListFailed") + "：" + ex.Message);
        }
        return list;
    }

    public async Task<List<OptiFineVersion>> GetOptiFineVersionsAsync(string mcVersion, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.OptiFine);
        var list = new List<OptiFineVersion>();
        try
        {
            var url = provider.OptiFineListUrl(mcVersion);
            var raw = await _http.GetJsonAsync<List<OptiFineEntry>>(url, token).ConfigureAwait(false);
            foreach (var entry in raw ?? new List<OptiFineEntry>())
            {
                if (entry is null) continue;
                list.Add(new OptiFineVersion
                {
                    McVersion = entry.McVersion ?? mcVersion,
                    Type = entry.Type ?? "",
                    // 光速镜像用 file / version，BMCLAPI 用 filename / patch
                    Patch = entry.Patch ?? entry.Version ?? "",
                    FileName = entry.FileName ?? entry.File,
                    Forge = entry.Forge,
                    Url = entry.Url
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("OptiFine", L.T("String.Core.Log.Loader.OptiFineListFailed") + "：" + ex.Message);
        }
        return list;
    }

    // ── 安装 ───────────────────────────────────────────────

    /// <summary>安装 Fabric：拉取官方/镜像的 profile json 作为独立版本。</summary>
    public async Task<string> InstallFabricAsync(string mcVersion, string loaderVersion, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.FabricMeta);
        var url = provider.FabricProfileUrl(mcVersion, loaderVersion);
        var json = await _http.GetStringAsync(url, token).ConfigureAwait(false);
        var versionId = $"fabric-loader-{loaderVersion}-{mcVersion}";
        SaveVersionJson(versionId, json);
        return versionId;
    }

    public async Task<string> InstallQuiltAsync(string mcVersion, string loaderVersion, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.QuiltMeta);
        var url = provider.QuiltProfileUrl(mcVersion, loaderVersion);
        var json = await _http.GetStringAsync(url, token).ConfigureAwait(false);
        var versionId = $"quilt-loader-{loaderVersion}-{mcVersion}";
        SaveVersionJson(versionId, json);
        return versionId;
    }

    /// <summary>安装 Forge：下载 installer jar 后由 Java 静默安装进游戏目录。</summary>
    public async Task<string> InstallForgeAsync(string mcVersion, string forgeVersion, string javaPath,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.Forge);
        var installerUrl = provider.ForgeInstallerUrl(mcVersion, forgeVersion);

        var installerPath = Path.Combine(_paths.CacheDirectory, "installers",
            $"forge-{mcVersion}-{forgeVersion}-installer.jar");
        FileHelper.EnsureDirectory(Path.GetDirectoryName(installerPath));
        await _http.DownloadToFileAsync(installerUrl, installerPath, null, token).ConfigureAwait(false);

        progress?.Report(L.T("String.Core.Log.Loader.RunningForge"));
        await RunInstallerAsync(javaPath, installerPath, $"-jar \"{installerPath}\" --installClient \"{_paths.GameDirectory}\"", token)
            .ConfigureAwait(false);

        var detected = DetectNewVersion(mcVersion, "forge");
        return detected ?? $"{mcVersion}-forge-{forgeVersion}";
    }

    public async Task<string> InstallNeoForgeAsync(string mcVersion, string version, string? installerPathFromApi,
        string javaPath, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.NeoForge);
        string installerUrl;
        if (!string.IsNullOrEmpty(installerPathFromApi))
            installerUrl = provider.NeoForgeMavenUrl(installerPathFromApi);
        else
            installerUrl = provider.NeoForgeInstallerUrl(mcVersion, version);

        var installerPath = Path.Combine(_paths.CacheDirectory, "installers", $"neoforge-{mcVersion}-{version}-installer.jar");
        FileHelper.EnsureDirectory(Path.GetDirectoryName(installerPath));
        await _http.DownloadToFileAsync(installerUrl, installerPath, null, token).ConfigureAwait(false);

        progress?.Report(L.T("String.Core.Log.Loader.RunningNeoForge"));
        await RunInstallerAsync(javaPath, installerPath, $"-jar \"{installerPath}\" --install-client \"{_paths.GameDirectory}\"", token)
            .ConfigureAwait(false);

        var detected = DetectNewVersion(mcVersion, "neoforge");
        return detected ?? $"{mcVersion}-neoforge-{version}";
    }

    /// <summary>OptiFine 官方安装器带 GUI，这里下载后交给用户/Java 完成安装。</summary>
    public async Task<string> InstallOptiFineAsync(OptiFineVersion version, string javaPath,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var provider = _mirrors.For(ResourceCategory.OptiFine);
        var fileName = string.IsNullOrEmpty(version.FileName)
            ? $"OptiFine_{version.McVersion}_{version.Type}_{version.Patch}.jar"
            : version.FileName!;
        var url = string.IsNullOrWhiteSpace(version.Url)
            ? provider.OptiFineDownloadUrl(version.McVersion, version.Type, version.Patch)
            : version.Url!;
        var target = Path.Combine(_paths.CacheDirectory, "installers", fileName);
        FileHelper.EnsureDirectory(Path.GetDirectoryName(target));
        await _http.DownloadToFileAsync(url, target, null, token).ConfigureAwait(false);

        progress?.Report(L.T("String.Core.Log.Loader.RunningOptiFine"));
        _ = Process.Start(new ProcessStartInfo(javaPath, $"-jar \"{target}\"") { UseShellExecute = true });
        return fileName;
    }

    private async Task RunInstallerAsync(string javaPath, string installerPath, string arguments, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo(javaPath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _paths.GameDirectory
        };
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("Installer", e.Data!); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn("Installer", e.Data!); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            Log.Warn("Installer", L.F("String.Core.Log.Loader.InstallerExit", process.ExitCode));
    }

    private string? DetectNewVersion(string mcVersion, string keyword)
    {
        try
        {
            if (!Directory.Exists(_paths.VersionsDirectory)) return null;
            var candidates = Directory.EnumerateDirectories(_paths.VersionsDirectory)
                .Select(dir => Path.Combine(dir, Path.GetFileName(dir) + ".json"))
                .Where(File.Exists)
                .Select(path => Json.Load<VersionJson>(path))
                .Where(v => v is not null)
                .Select(v => v!)
                .Where(v => (v.Id ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                            ((v.InheritsFrom ?? "").StartsWith(mcVersion, StringComparison.OrdinalIgnoreCase) ||
                             (v.Id ?? "").StartsWith(mcVersion, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(v => File.GetLastWriteTimeUtc(_paths.VersionJsonPath(v.Id ?? "")))
                .ToList();
            return candidates.FirstOrDefault()?.Id;
        }
        catch (IOException) { return null; }
    }

    private void SaveVersionJson(string versionId, string json)
    {
        var path = _paths.VersionJsonPath(versionId);
        FileHelper.EnsureDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, json);
    }

    // ── 列表模型 ────────────────────────────────────────────
    private sealed class FabricLoaderEntry
    {
        [JsonPropertyName("loader")] public FabricLoaderInfo? Loader { get; set; }
    }

    private sealed class FabricLoaderInfo
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("stable")] public bool Stable { get; set; }
    }

    private sealed class QuiltLoaderEntry
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
    }

    private sealed class ForgeEntry
    {
        [JsonPropertyName("_id")] public string? Id { get; set; }
        [JsonPropertyName("mcversion")] public string? McVersion { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("build")] public int? Build { get; set; }
        [JsonPropertyName("branch")] public string? Branch { get; set; }
        [JsonPropertyName("modified")] public string? Modified { get; set; }
    }

    private sealed class NeoForgeEntry
    {
        [JsonPropertyName("_id")] public string? Id { get; set; }
        [JsonPropertyName("mcversion")] public string? McVersion { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("rawVersion")] public string? RawVersion { get; set; }
        [JsonPropertyName("installerPath")] public string? InstallerPath { get; set; }
    }

    private sealed class OptiFineEntry
    {
        [JsonPropertyName("_id")] public string? Id { get; set; }
        [JsonPropertyName("mcversion")] public string? McVersion { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("patch")] public string? Patch { get; set; }
        [JsonPropertyName("filename")] public string? FileName { get; set; }
        [JsonPropertyName("forge")] public string? Forge { get; set; }

        // 光速镜像（fastmcmirror.org）使用的字段名
        [JsonPropertyName("file")] public string? File { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
