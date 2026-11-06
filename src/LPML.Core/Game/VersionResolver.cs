using LPML.Core;
using LPML.Core.Game.Models;
using LPML.Core.Mirror;
using LPML.Core.Utils;

namespace LPML.Core.Game;

/// <summary>
/// 版本解析：读取 versions\&lt;id&gt;\&lt;id&gt;.json，沿 inheritsFrom 链向上合并
/// （Forge / Fabric / OptiFine 都是基于原版 JSON 的补丁）。
/// </summary>
public sealed class VersionResolver
{
    private readonly AppPaths _paths;

    public VersionResolver(AppPaths paths)
    {
        _paths = paths;
    }

    public VersionJson? Load(string versionId)
    {
        var path = _paths.VersionJsonPath(versionId);
        if (!File.Exists(path))
        {
            // 记下真实路径：以前只报"找不到版本 X 的 JSON"，看不出到底去哪找的
            Log.Warn("Version", L.F("String.Core.Log.Version.JsonMissing", versionId, path));
            return null;
        }
        var json = Json.Load<VersionJson>(path);
        if (json is null)
        {
            Log.Warn("Version", L.F("String.Core.Log.Version.JsonBad", path));
            return null;
        }

        var chain = new List<VersionJson> { json };
        var guard = 0;
        var current = json;
        while (!string.IsNullOrWhiteSpace(current.InheritsFrom) && guard++ < 16)
        {
            var parentPath = _paths.VersionJsonPath(current.InheritsFrom!);
            if (!File.Exists(parentPath)) break;
            var parent = Json.Load<VersionJson>(parentPath);
            if (parent is null) break;
            chain.Add(parent);
            current = parent;
        }
        chain.Reverse();
        return Merge(chain);
    }

    public static VersionJson Merge(List<VersionJson> chain)
    {
        if (chain.Count == 1) return chain[0];
        var result = chain[0];
        for (var i = 1; i < chain.Count; i++)
        {
            var child = chain[i];
            var merged = new VersionJson
            {
                Id = child.Id ?? result.Id,
                InheritsFrom = null,
                MainClass = child.MainClass ?? result.MainClass,
                Assets = child.Assets ?? result.Assets,
                AssetIndex = child.AssetIndex ?? result.AssetIndex,
                Downloads = MergeDownloads(result.Downloads, child.Downloads),
                JavaVersion = child.JavaVersion ?? result.JavaVersion,
                Libraries = MergeLibraries(result.Libraries, child.Libraries),
                Arguments = MergeArguments(result.Arguments, child.Arguments),
                MinecraftArguments = child.MinecraftArguments ?? result.MinecraftArguments,
                Type = child.Type ?? result.Type,
                Time = child.Time ?? result.Time,
                ReleaseTime = child.ReleaseTime ?? result.ReleaseTime,
                MinimumLauncherVersion = child.MinimumLauncherVersion ?? result.MinimumLauncherVersion,
                Logging = child.Logging ?? result.Logging,
                LpmlLoaders = child.LpmlLoaders ?? result.LpmlLoaders
            };
            result = merged;
        }
        return result;
    }

    private static Dictionary<string, Artifact>? MergeDownloads(
        Dictionary<string, Artifact>? parent, Dictionary<string, Artifact>? child)
    {
        if (parent is null) return child;
        if (child is null) return parent;
        var result = new Dictionary<string, Artifact>(parent);
        foreach (var (key, value) in child) result[key] = value;
        return result;
    }

    private static List<Library>? MergeLibraries(List<Library>? parent, List<Library>? child)
    {
        if (parent is null) return child;
        if (child is null) return parent;
        var result = new List<Library>(child);
        var existing = new HashSet<string>(child.Select(l => l.Name ?? ""), StringComparer.OrdinalIgnoreCase);
        foreach (var lib in parent)
            if (!existing.Contains(lib.Name ?? "")) result.Add(lib);
        return result;
    }

    private static ArgumentList? MergeArguments(ArgumentList? parent, ArgumentList? child)
    {
        if (parent is null) return child;
        if (child is null) return parent;
        // 子版本的 game 参数接在父版本后面（Forge 依赖此顺序），jvm 参数同理
        var game = new List<System.Text.Json.JsonElement>();
        if (parent.Game is not null) game.AddRange(parent.Game);
        if (child.Game is not null) game.AddRange(child.Game);
        var jvm = new List<System.Text.Json.JsonElement>();
        if (parent.Jvm is not null) jvm.AddRange(parent.Jvm);
        if (child.Jvm is not null) jvm.AddRange(child.Jvm);
        return new ArgumentList { Game = game, Jvm = jvm };
    }
}
