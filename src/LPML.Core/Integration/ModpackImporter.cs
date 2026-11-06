using LPML.Core.Download;
using LPML.Core.Instances;
using LPML.Core.Mirror;
using System.IO.Compression;
using System.Text.Json.Serialization;
using LPML.Core.Config;
using LPML.Core.Utils;

namespace LPML.Core.Integration;

/// <summary>
/// 整合包导入：
/// - Modrinth (.mrpack)：解析 modrinth.index.json，下载 mods / 展开 overrides
/// - HMCL / MultiMC / Curse 导出的 zip：识别内部 .minecraft 目录并拷贝
/// 所有下载依然走统一的下载中心（多任务 + 多线程）。
/// </summary>
public sealed class ModpackImporter
{
    private readonly AppPaths _paths;
    private readonly MirrorManager _mirrors;

    public ModpackImporter(AppPaths paths, MirrorManager mirrors)
    {
        _paths = paths;
        _mirrors = mirrors;
    }

    /// <summary>
    /// 导入一个整合包。支持 Modrinth(.mrpack) / CurseForge(zip) / MultiMC(zip) / HMCL(zip) / 通用 zip。
    /// 返回：需联网下载的项、可直接解压的项、MC 版本、以及需手动下载的 CurseForge 模组清单。
    /// </summary>
    public async Task<(List<DownloadItem> Downloads, List<(string Source, string Target)> Extractions,
        string? GameVersion, List<ManualModEntry> ManualMods)> PrepareAsync(
        string packPath, string targetDirectory, CancellationToken token = default)
    {
        var downloads = new List<DownloadItem>();
        var extractions = new List<(string, string)>();
        var manual = new List<ManualModEntry>();
        string? gameVersion = null;

        FileHelper.EnsureDirectory(targetDirectory);

        var format = ModpackProbe.Inspect(packPath).Format;

        using var archive = ZipFile.OpenRead(packPath);

        var indexEntry = archive.GetEntry("modrinth.index.json");
        if (indexEntry is not null)
        {
            using var reader = new StreamReader(indexEntry.Open());
            var index = Json.Deserialize<ModrinthIndex>(reader.ReadToEnd());
            if (index is null) throw new IOException(L.T("String.Core.Err.Modpack.IndexFailed"));

            gameVersion = index.Dependencies?["minecraft"];
            var modsDirectory = Path.Combine(targetDirectory, "mods");
            FileHelper.EnsureDirectory(modsDirectory);

            foreach (var file in index.Files ?? new List<ModrinthFile>())
            {
                if (string.IsNullOrEmpty(file.Path)) continue;
                var destination = Path.GetFullPath(Path.Combine(targetDirectory, file.Path.Replace('\\', '/')));
                if (!destination.StartsWith(Path.GetFullPath(targetDirectory), StringComparison.OrdinalIgnoreCase)) continue;

                if (file.Env?.TryGetValue("client", out var support) == true &&
                    string.Equals(support, "unsupported", StringComparison.OrdinalIgnoreCase)) continue;

                var url = file.Downloads?.FirstOrDefault();
                if (string.IsNullOrEmpty(url)) continue;
                downloads.Add(new DownloadItem
                {
                    Name = Path.GetFileName(file.Path),
                    Url = url!,
                    Destination = destination,
                    Sha1 = file.Hashes?["sha1"],
                    ExpectedSize = file.Size,
                    Category = ResourceCategory.Modrinth,
                    Group = L.T("String.Core.Group.Modpack")
                });
            }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith('/')) continue;
                if (!entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) &&
                    !entry.FullName.StartsWith("server-overrides/", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = entry.FullName[(entry.FullName.IndexOf('/') + 1)..];
                extractions.Add((entry.FullName, Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar))));
            }

            return (downloads, extractions, gameVersion, manual);
        }

        // ── CurseForge：overrides 可直接解压，files 需手动下载 ──
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is not null)
        {
            var info = ModpackProbe.Inspect(packPath);
            gameVersion = info.GameVersion;

            // overrides/ 是本地文件，直接解压
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith('/')) continue;
                if (!entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = entry.FullName[(entry.FullName.IndexOf('/') + 1)..];
                if (relative.Length == 0) continue;
                extractions.Add((entry.FullName, Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar))));
            }

            // 模组拿不到下载地址，列成清单交给用户
            manual.AddRange(info.ManualMods);
            return (downloads, extractions, gameVersion, manual);
        }

        // ── MultiMC / HMCL / 通用 zip：内容全在包里，整包解压 ──
        // 定位 .minecraft 前缀（MultiMC 是 .minecraft/，HMCL 导出也可能直接是根目录）
        var prefixes = new[] { ".minecraft/", "minecraft/", "" };
        var matched = prefixes.FirstOrDefault(p =>
            p.Length == 0 || archive.Entries.Any(e => e.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase))) ?? "";

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith('/')) continue;
            if (matched.Length > 0 && !entry.FullName.StartsWith(matched, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = matched.Length > 0 ? entry.FullName[matched.Length..] : entry.FullName;
            if (relative.Length == 0) continue;
            extractions.Add((entry.FullName, Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar))));
        }

        // 尝试读取版本信息
        var versionJson = archive.GetEntry("version.json") ?? archive.GetEntry(".minecraft/version.json");
        if (versionJson is not null)
        {
            using var reader = new StreamReader(versionJson.Open());
            var text = reader.ReadToEnd();
            var idMatch = System.Text.RegularExpressions.Regex.Match(text, "\"id\"\\s*:\\s*\"(?<id>[^\"]+)\"");
            if (idMatch.Success) gameVersion = idMatch.Groups["id"].Value;
        }

        var manifest = archive.GetEntry("manifest.json");
        if (manifest is not null && gameVersion is null)
        {
            using var reader = new StreamReader(manifest.Open());
            var text = reader.ReadToEnd();
            var idMatch = System.Text.RegularExpressions.Regex.Match(text, "\"gameVersion\"\\s*:\\s*\"(?<id>[^\"]+)\"");
            if (idMatch.Success) gameVersion = idMatch.Groups["id"].Value;
        }

        return (downloads, extractions, gameVersion, manual);
    }

    /// <summary>合法化一个解压目标路径，杜绝 Zip Slip（"../../../evil.exe" 逃逸出实例目录）。</summary>
    public static bool IsSafePath(string rootDirectory, string target)
    {
        try
        {
            var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(target);
            return full.Length > root.Length &&
                   full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解压。rootDirectory 用于安全校验：任何逃逸出该目录的条目都会被跳过并记日志。
    /// </summary>
    public void Extract(string packPath, IEnumerable<(string Source, string Target)> extractions,
        string? rootDirectory = null)
    {
        using var archive = ZipFile.OpenRead(packPath);
        var blocked = 0;

        foreach (var (source, target) in extractions)
        {
            var entry = archive.GetEntry(source);
            if (entry is null) continue;

            // 没传根目录时，按每个目标自身的上一层推断——仍然保证不越界
            var root = rootDirectory ?? Path.GetDirectoryName(Path.GetDirectoryName(target)) ?? target;
            if (!IsSafePath(root, target))
            {
                blocked++;
                continue;
            }

            FileHelper.EnsureDirectory(Path.GetDirectoryName(target));
            try { entry.ExtractToFile(target, true); }
            catch (IOException) { }
        }

        if (blocked > 0)
            Log.Warn("Modpack", L.F("String.Core.Log.Modpack.BlockedPaths", blocked));
    }

    private sealed class ModrinthIndex
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
        [JsonPropertyName("game")] public string? Game { get; set; }
        [JsonPropertyName("versionId")] public string? VersionId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
        [JsonPropertyName("files")] public List<ModrinthFile>? Files { get; set; }
    }

    private sealed class ModrinthFile
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("downloads")] public List<string>? Downloads { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
        [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
    }
}
