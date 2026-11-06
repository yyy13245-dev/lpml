using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LPML.Core.Integration;

/// <summary>CurseForge manifest.json 里需要手动下载的模组条目。</summary>
public sealed class ManualModEntry
{
    public long ProjectId { get; set; }
    public long FileId { get; set; }
    public bool Required { get; set; } = true;

    /// <summary>CurseForge 项目页（用 projectID 拼，可跳转）。</summary>
    public string WebUrl => $"https://www.curseforge.com/projects/{ProjectId}";

    public string Display => Required
        ? $"#{ProjectId} (file {FileId})"
        : $"#{ProjectId} (file {FileId}) — {L.T("String.Core.Modpack.Optional")}";
}

/// <summary>整合包的探测结果（只读元信息，不做任何解压/下载）。</summary>
public sealed class ModpackInfo
{
    public ModpackFormat Format { get; set; } = ModpackFormat.Unknown;

    public string Name { get; set; } = "";
    public string? GameVersion { get; set; }

    /// <summary>加载器名称，如 forge / fabric / neoforge。</summary>
    public string? LoaderName { get; set; }

    /// <summary>加载器版本，如 47.2.0。</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>包内自带的模组数量（MultiMC / HMCL / 通用 zip）。</summary>
    public int IncludedModCount { get; set; }

    /// <summary>需要联网下载的模组数量（Modrinth）。</summary>
    public int DownloadModCount { get; set; }

    /// <summary>需要手动下载的 CurseForge 模组清单。</summary>
    public List<ManualModEntry> ManualMods { get; set; } = new();

    public string FormatText => Format.DisplayName();

    /// <summary>摘要文本，给确认弹窗用。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(GameVersion)) parts.Add("MC " + GameVersion);
            if (!string.IsNullOrWhiteSpace(LoaderName))
                parts.Add(LoaderName + (string.IsNullOrWhiteSpace(LoaderVersion) ? "" : " " + LoaderVersion));
            if (IncludedModCount > 0) parts.Add(L.F("String.Core.Modpack.IncludedMods", IncludedModCount));
            if (DownloadModCount > 0) parts.Add(L.F("String.Core.Modpack.DownloadMods", DownloadModCount));
            if (ManualMods.Count > 0) parts.Add(L.F("String.Core.Modpack.ManualMods", ManualMods.Count));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>只读地探测一个整合包，供导入前确认。不写任何文件。</summary>
public static class ModpackProbe
{
    public static ModpackInfo Inspect(string packPath)
    {
        var info = new ModpackInfo();

        using var archive = ZipFile.OpenRead(packPath);

        // ── Modrinth ─────────────────────────────────────
        var mrIndex = archive.GetEntry("modrinth.index.json");
        if (mrIndex is not null)
        {
            info.Format = ModpackFormat.Modrinth;
            try
            {
                using var reader = new StreamReader(mrIndex.Open());
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var name)) info.Name = name.GetString() ?? "";
                if (root.TryGetProperty("dependencies", out var deps))
                {
                    if (deps.TryGetProperty("minecraft", out var mc)) info.GameVersion = mc.GetString();
                    foreach (var key in new[] { "forge", "fabric-loader", "neoforge", "quilt-loader" })
                        if (deps.TryGetProperty(key, out var loader))
                        {
                            info.LoaderName = key.Replace("-loader", "");
                            info.LoaderVersion = loader.GetString();
                            break;
                        }
                }
                if (root.TryGetProperty("files", out var files) &&
                    files.ValueKind == JsonValueKind.Array)
                    info.DownloadModCount = files.GetArrayLength();
            }
            catch (System.Text.Json.JsonException) { }
            return info;
        }

        // ── CurseForge ───────────────────────────────────
        var manifest = archive.GetEntry("manifest.json");
        if (manifest is not null)
        {
            try
            {
                using var reader = new StreamReader(manifest.Open());
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var root = doc.RootElement;

                // 只有 minecraftModpack 才是整合包，mod 包的 manifest 不认
                var isPack = !root.TryGetProperty("manifestType", out var type) ||
                             string.Equals(type.GetString(), "minecraftModpack", StringComparison.OrdinalIgnoreCase);
                if (!isPack) throw new System.Text.Json.JsonException();

                info.Format = ModpackFormat.CurseForge;
                if (root.TryGetProperty("name", out var name)) info.Name = name.GetString() ?? "";

                if (root.TryGetProperty("minecraft", out var mc))
                {
                    if (mc.TryGetProperty("version", out var version)) info.GameVersion = version.GetString();
                    if (mc.TryGetProperty("modLoaders", out var loaders) &&
                        loaders.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var loader in loaders.EnumerateArray())
                        {
                            if (!loader.TryGetProperty("id", out var id)) continue;
                            var text = id.GetString() ?? "";
                            var dash = text.IndexOf('-');
                            if (dash <= 0) continue;
                            info.LoaderName = text[..dash];
                            info.LoaderVersion = text[(dash + 1)..];
                            if (loader.TryGetProperty("primary", out var primary) &&
                                primary.ValueKind == JsonValueKind.True) break;
                        }
                    }
                }

                if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in files.EnumerateArray())
                    {
                        var entry = new ManualModEntry();
                        if (file.TryGetProperty("projectID", out var pid)) entry.ProjectId = pid.GetInt64();
                        if (file.TryGetProperty("fileID", out var fid)) entry.FileId = fid.GetInt64();
                        if (file.TryGetProperty("required", out var required))
                            entry.Required = required.ValueKind != JsonValueKind.False;
                        if (entry.ProjectId > 0) info.ManualMods.Add(entry);
                    }
                }

                // overrides 里的文件是可以直接解压的
                info.IncludedModCount = archive.Entries.Count(e =>
                    !string.IsNullOrEmpty(e.FullName) &&
                    e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            }
            catch (System.Text.Json.JsonException)
            {
                info.Format = ModpackFormat.Unknown;
            }
            if (info.Format == ModpackFormat.CurseForge) return info;
        }

        // ── MultiMC / Prism ──────────────────────────────
        var mmc = archive.GetEntry("mmc-pack.json");
        if (mmc is not null)
        {
            info.Format = ModpackFormat.MultiMC;
            try
            {
                using var reader = new StreamReader(mmc.Open());
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var root = doc.RootElement;
                if (root.TryGetProperty("components", out var components) &&
                    components.ValueKind == JsonValueKind.Array)
                {
                    foreach (var component in components.EnumerateArray())
                    {
                        var uid = component.TryGetProperty("uid", out var u) ? u.GetString() ?? "" : "";
                        var version = component.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

                        if (string.Equals(uid, "net.minecraft", StringComparison.OrdinalIgnoreCase))
                            info.GameVersion = version;
                        else if (uid.Contains("forge", StringComparison.OrdinalIgnoreCase) ||
                                 uid.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
                                 uid.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ||
                                 uid.Contains("quilt", StringComparison.OrdinalIgnoreCase))
                        {
                            info.LoaderName = uid.Contains("neoforge") ? "neoforge"
                                : uid.Contains("forge") ? "forge"
                                : uid.Contains("quilt") ? "quilt" : "fabric";
                            info.LoaderVersion = version;
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException) { }
            info.IncludedModCount = CountModsIn(archive);
            return info;
        }

        // ── HMCL 导出 ────────────────────────────────────
        var hmcl = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith("hmcl.modpack.json", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith("hmclmodpack.json", StringComparison.OrdinalIgnoreCase));
        if (hmcl is not null)
        {
            info.Format = ModpackFormat.Hmcl;
            try
            {
                using var reader = new StreamReader(hmcl.Open());
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var name)) info.Name = name.GetString() ?? "";
                if (root.TryGetProperty("gameVersion", out var gv)) info.GameVersion = gv.GetString();
                if (root.TryGetProperty("loaderName", out var ln)) info.LoaderName = ln.GetString();
                if (root.TryGetProperty("loaderVersion", out var lv)) info.LoaderVersion = lv.GetString();
            }
            catch (System.Text.Json.JsonException) { }
            info.IncludedModCount = CountModsIn(archive);
            return info;
        }

        // ── 通用 zip ─────────────────────────────────────
        info.Format = ModpackFormat.PlainZip;
        info.IncludedModCount = CountModsIn(archive);
        return info;
    }

    private static int CountModsIn(ZipArchive archive) =>
        archive.Entries.Count(e =>
            !string.IsNullOrEmpty(e.FullName) &&
            e.FullName.Contains("mods/", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
}
