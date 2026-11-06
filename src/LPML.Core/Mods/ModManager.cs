using LPML.Core.Utils;
using System.IO.Compression;
using System.Text.Json;

namespace LPML.Core.Mods;

public sealed class ModInfo : ObservableObject
{
    private bool _enabled;
    private bool _isSelected;

    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Authors { get; set; } = "";
    public string Loader { get; set; } = L.T("String.Msg.Common.Unknown");
    public long Size { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>多选模式下的勾选状态，只存在于内存，不写进文件。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string SizeText => FileHelper.HumanSize(Size);
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? FileName : Name;
}

/// <summary>模组管理：扫描 mods 目录、读取元信息、启用/禁用（.jar.disabled）、删除。</summary>
public sealed class ModManager
{
    public static List<ModInfo> Scan(string modsDirectory)
    {
        var result = new List<ModInfo>();
        if (!Directory.Exists(modsDirectory)) return result;

        foreach (var file in Directory.EnumerateFiles(modsDirectory, "*.jar*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(file);
            var enabled = file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
            var mod = new ModInfo
            {
                FileName = info.Name,
                FullPath = file,
                Size = info.Length,
                Enabled = enabled,
                Name = Path.GetFileNameWithoutExtension(file)
            };
            try
            {
                using var archive = ZipFile.OpenRead(file);
                ReadModMetadata(archive, mod);
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            result.Add(mod);
        }
        return result.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadModMetadata(ZipArchive archive, ModInfo mod)
    {
        // Fabric / Quilt
        var fabric = archive.GetEntry("fabric.mod.json");
        if (fabric is not null)
        {
            using var reader = new StreamReader(fabric.Open());
            var json = Json.Deserialize<FabricModJson>(reader.ReadToEnd());
            if (json is not null)
            {
                mod.Loader = "Fabric/Quilt";
                mod.Name = json.Name ?? mod.Name;
                mod.Description = json.Description ?? "";
                mod.Version = json.Version ?? "";
                mod.Authors = json.Authors is null ? "" : string.Join(", ", json.Authors.Select(AuthorName));
                return;
            }
        }

        // Forge / NeoForge (mods.toml)
        var toml = archive.GetEntry("META-INF/mods.toml") ?? archive.GetEntry("META-INF/neoforge.mods.toml");
        if (toml is not null)
        {
            using var reader = new StreamReader(toml.Open());
            var text = reader.ReadToEnd();
            mod.Loader = "Forge/NeoForge";
            mod.Name = ExtractToml(text, "displayName") ?? mod.Name;
            mod.Description = ExtractToml(text, "description") ?? "";
            mod.Version = ExtractToml(text, "version") ?? "";
            mod.Authors = ExtractToml(text, "authors") ?? "";
            return;
        }

        // 旧版 Forge (mcmod.info)
        var legacy = archive.GetEntry("mcmod.info");
        if (legacy is not null)
        {
            using var reader = new StreamReader(legacy.Open());
            var text = reader.ReadToEnd();
            mod.Loader = "Forge (1.12-)";
            mod.Name = ExtractJson(text, "name") ?? mod.Name;
            mod.Description = ExtractJson(text, "description") ?? "";
            mod.Version = ExtractJson(text, "version") ?? "";
            mod.Authors = ExtractJson(text, "authorList") ?? "";
        }
    }

    private static string AuthorName(object author)
        => author?.ToString() ?? "";

    private static string? ExtractToml(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var index = trimmed.IndexOf('=');
            if (index < 0) continue;
            return trimmed[(index + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static string? ExtractJson(string text, string key)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, $"\"{key}\"\\s*:\\s*\"(?<value>[^\"]*)\"");
        return match.Success ? match.Groups["value"].Value : null;
    }

    public static bool SetEnabled(ModInfo mod, bool enabled)
    {
        try
        {
            var directory = Path.GetDirectoryName(mod.FullPath) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(mod.FullPath);
            if (baseName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^".disabled".Length];

            var target = Path.Combine(directory, baseName + (enabled ? ".jar" : ".jar.disabled"));
            if (string.Equals(target, mod.FullPath, StringComparison.OrdinalIgnoreCase)) return true;
            if (File.Exists(target)) File.Delete(target);
            File.Move(mod.FullPath, target);
            mod.FullPath = target;
            mod.FileName = Path.GetFileName(target);
            mod.Enabled = enabled;
            return true;
        }
        catch (IOException ex)
        {
            Log.Warn("Mods", L.T("String.Core.Log.Mod.ToggleFailed") + "：" + ex.Message);
            return false;
        }
    }

    public static bool Delete(ModInfo mod)
    {
        try
        {
            File.Delete(mod.FullPath);
            return true;
        }
        catch (IOException ex)
        {
            Log.Warn("Mods", L.T("String.Core.Log.Mod.DeleteFailed") + "：" + ex.Message);
            return false;
        }
    }

    private sealed class FabricModJson
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public List<object>? Authors { get; set; }
    }
}
