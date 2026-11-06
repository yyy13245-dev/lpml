using System.Text.Json;
using System.Text.Json.Serialization;

namespace LPML.Core;

/// <summary>
/// 启动器预设：放在 LPML\presets.json，随安装自带。
/// 目的：新装好时不至于到处都是空的——给实例模板、推荐版本、镜像源一个初始值。
/// 用户可以随意改，改错了删掉文件会重新生成默认预设。
/// </summary>
public sealed class LauncherPresets
{
    /// <summary>新建实例时可选的模板。</summary>
    public List<InstancePreset> Instances { get; set; } = new();

    /// <summary>推荐下载的游戏版本（下载中心置顶）。</summary>
    public List<string> RecommendedVersions { get; set; } = new();

    /// <summary>推荐镜像源（下载源列表置顶）。</summary>
    public List<MirrorPreset> Mirrors { get; set; } = new();

    /// <summary>常用 Java 版本需求对照（提示用）。</summary>
    public List<JavaHintPreset> JavaHints { get; set; } = new();

    // ── 读 / 写 ──────────────────────────────────────────────

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string FileName => "presets.json";

    /// <summary>读取；不存在或损坏时返回默认预设。</summary>
    public static LauncherPresets Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<LauncherPresets>(text, Options);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Presets", "Failed to load presets, using defaults: " + ex.Message);
        }

        var defaults = Defaults();
        Save(path, defaults);
        return defaults;
    }

    public static void Save(string path, LauncherPresets presets)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(presets, Options));
        }
        catch (Exception ex)
        {
            Log.Warn("Presets", "Failed to save presets: " + ex.Message);
        }
    }

    /// <summary>出厂预设。</summary>
    public static LauncherPresets Defaults() => new()
    {
        Instances = new List<InstancePreset>
        {
            new() { Name = L.T("String.Preset.Instance.Latest"), VersionId = "latest-release", Loader = "vanilla", Icon = "🎮" },
            new() { Name = L.T("String.Preset.Instance.Fabric"), VersionId = "latest-release", Loader = "fabric", Icon = "🧵" },
            new() { Name = L.T("String.Preset.Instance.Forge"), VersionId = "1.20.1", Loader = "forge", Icon = "🔨" },
            new() { Name = L.T("String.Preset.Instance.NeoForge"), VersionId = "1.21.1", Loader = "neoforge", Icon = "⚡" }
        },
        RecommendedVersions = new List<string> { "1.21.4", "1.21.1", "1.20.1", "1.19.4", "1.18.2", "1.16.5" },
        Mirrors = new List<MirrorPreset>
        {
            new() { Name = "BMCLAPI", Kind = "Bmclapi", Note = L.T("String.Preset.Mirror.Bmclapi") },
            new() { Name = L.T("String.Mirror.FastMcMirror"), Kind = "FastMcMirror", Note = L.T("String.Preset.Mirror.FastMcMirror") },
            new() { Name = L.T("String.Mirror.Official"), Kind = "Official", Note = L.T("String.Preset.Mirror.Official") }
        },
        JavaHints = new List<JavaHintPreset>
        {
            new() { GameVersion = "1.16.5", RequiredMajor = 8 },
            new() { GameVersion = "1.17.1", RequiredMajor = 16 },
            new() { GameVersion = "1.18.2", RequiredMajor = 17 },
            new() { GameVersion = "1.20.1", RequiredMajor = 17 },
            new() { GameVersion = "1.21.1", RequiredMajor = 21 },
            new() { GameVersion = "1.21.4", RequiredMajor = 21 }
        }
    };
}

public sealed class InstancePreset
{
    public string Name { get; set; } = "";
    public string VersionId { get; set; } = "latest-release";
    public string Loader { get; set; } = "vanilla";
    public string Icon { get; set; } = "🎮";
}

public sealed class MirrorPreset
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Official";
    public string Note { get; set; } = "";
}

public sealed class JavaHintPreset
{
    public string GameVersion { get; set; } = "";
    public int RequiredMajor { get; set; }
}
