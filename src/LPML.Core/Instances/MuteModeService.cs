using System.Text;

namespace LPML.Core.Instances;

/// <summary>
/// 一键静音：把实例目录下 options.txt 里的音量项直接设为 0。
/// 不备份原值——按一次静一次，想恢复自己在游戏里调回来。
/// </summary>
public static class MuteModeService
{
    /// <summary>需要归零的音量项。</summary>
    private static readonly string[] VolumeKeys =
    {
        "soundCategory_master",
        "soundCategory_music",
        "soundCategory_record",
        "soundCategory_weather",
        "soundCategory_block",
        "soundCategory_hostile",
        "soundCategory_neutral",
        "soundCategory_player",
        "soundCategory_ambient",
        "soundCategory_voice"
    };

    private const string MuteValue = "0.0";

    /// <summary>取实例目录下 options.txt 的路径。</summary>
    public static string OptionsPath(GameInstance instance, InstanceManager instances)
        => Path.Combine(instances.InstanceDirectory(instance), "options.txt");

    /// <summary>把该实例的音量全部写成 0。返回改动了几项。</summary>
    public static int MuteNow(GameInstance instance, InstanceManager instances)
    {
        var path = OptionsPath(instance, instances);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var map = Parse(lines);

        var changed = 0;
        foreach (var key in VolumeKeys)
        {
            if (map.TryGetValue(key, out var value) && string.Equals(value, MuteValue, StringComparison.Ordinal)) continue;
            map[key] = MuteValue;
            changed++;
        }

        if (changed > 0) Write(path, map, lines);
        return changed;
    }

    /// <summary>该实例当前是否已经是静音（所有音量项都是 0）。</summary>
    public static bool IsMuted(GameInstance instance, InstanceManager instances)
    {
        var path = OptionsPath(instance, instances);
        if (!File.Exists(path)) return false;
        var map = Parse(File.ReadAllLines(path).ToList());
        return VolumeKeys.All(k => map.TryGetValue(k, out var v) && string.Equals(v, MuteValue, StringComparison.Ordinal));
    }

    private static Dictionary<string, string> Parse(List<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var index = line.IndexOf(':');
            if (index <= 0) continue;
            map[line[..index]] = line[(index + 1)..];
        }
        return map;
    }

    /// <summary>写回：保留原文件里非音量项的顺序与内容。</summary>
    private static void Write(string path, Dictionary<string, string> map, List<string> original)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();

        foreach (var line in original)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) { output.Add(line); continue; }
            var index = line.IndexOf(':');
            if (index <= 0) { output.Add(line); continue; }
            var key = line[..index];
            if (!map.TryGetValue(key, out var value)) { output.Add(line); continue; }
            output.Add($"{key}:{value}");
            written.Add(key);
        }

        // 原文件里没有的键补在末尾
        foreach (var pair in map)
        {
            if (written.Contains(pair.Key)) continue;
            output.Add($"{pair.Key}:{pair.Value}");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllLines(path, output, Encoding.UTF8);
    }
}
