using LPML.Core.Auth;
using LPML.Core.Game;
using LPML.Core.Game.Models;
using LPML.Core.Utils;
using System.IO.Compression;
using System.Text.Json;

namespace LPML.Core.Launch;

/// <summary>
/// 启动参数构建器：还原官方启动器行为（规则过滤、classpath、natives 解压、
/// 占位符替换、authlib-injector 注入），并叠加用户自定义 JVM / 游戏参数。
/// </summary>
public sealed class LaunchArgumentBuilder
{
    private readonly AppPaths _paths;

    public LaunchArgumentBuilder(AppPaths paths)
    {
        _paths = paths;
    }

    public string NativesDirectory(string versionId)
        => Path.Combine(_paths.VersionDirectory(versionId), "natives");

    public List<string> BuildClasspath(VersionJson version, string versionId, out List<string> missing)
    {
        missing = new List<string>();
        var entries = new List<string>();
        var jar = _paths.VersionJarPath(versionId);
        if (File.Exists(jar)) entries.Add(jar);
        else missing.Add(jar);

        if (version.Libraries is null) return entries;

        foreach (var lib in version.Libraries)
        {
            if (lib is null) continue;
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;
            if (string.IsNullOrWhiteSpace(lib.Name)) continue;

            var (pureName, ext) = MavenHelper.SplitExtension(lib.Name);
            var artifact = lib.Downloads?.Artifact;
            var path = artifact?.Path ?? MavenHelper.ToPath(pureName, null, ext);
            var full = _paths.LibraryPath(path);
            if (File.Exists(full)) entries.Add(full);
            else if (lib.SkipMissing != true) missing.Add(full);

            if (lib.Natives is not null)
            {
                var classifier = MavenHelper.NativeClassifier(lib.Natives);
                if (!string.IsNullOrEmpty(classifier) &&
                    lib.Downloads?.Classifiers?.TryGetValue(classifier, out var native) == true && native is not null)
                {
                    var nativePath = native.Path ?? MavenHelper.ToPath(pureName, classifier, "jar");
                    var nativeFull = _paths.LibraryPath(nativePath);
                    if (File.Exists(nativeFull)) entries.Add(nativeFull);
                    else missing.Add(nativeFull);
                }
            }
        }
        return entries;
    }

    /// <summary>解压 natives 到版本目录下的 natives 文件夹。</summary>
    public void ExtractNatives(VersionJson version, string versionId)
    {
        using var _perf = PhaseTimer.Measure(L.F("String.Core.Perf.ExtractNatives", versionId));
        var target = NativesDirectory(versionId);
        FileHelper.EnsureDirectory(target);
        if (version.Libraries is null) return;

        foreach (var lib in version.Libraries)
        {
            if (lib?.Natives is null) continue;
            if (!RuleEvaluator.IsAllowed(lib.Rules)) continue;
            var classifier = MavenHelper.NativeClassifier(lib.Natives);
            if (string.IsNullOrEmpty(classifier)) continue;
            if (lib.Downloads?.Classifiers?.TryGetValue(classifier, out var native) != true || native is null) continue;

            var nativePath = native.Path ?? MavenHelper.ToPath(lib.Name, classifier, "jar");
            var full = _paths.LibraryPath(nativePath);
            if (!File.Exists(full)) continue;

            try
            {
                using var archive = ZipFile.OpenRead(full);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith('/')) continue;
                    if (lib.Extract?.Exclude?.Any(ex => entry.FullName.StartsWith(ex, StringComparison.OrdinalIgnoreCase)) == true)
                        continue;
                    var destination = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    FileHelper.EnsureDirectory(Path.GetDirectoryName(destination));
                    try { entry.ExtractToFile(destination, true); }
                    catch (IOException) { }
                }
            }
            catch (InvalidDataException)
            {
                Log.Warn("Launch", L.T("String.Core.Log.Launch.NativesFailed") + "：" + full);
            }
        }
    }

    public List<string> BuildJvmArguments(VersionJson version, string versionId, Account? account,
        LaunchOptions options, string classpath, string nativesDirectory)
    {
        var args = new List<string>
        {
            $"-Xms{options.MinMemoryMb}M",
            $"-Xmx{options.MaxMemoryMb}M"
        };

        // 版本 JSON 自带的 JVM 参数（含规则）
        if (version.Arguments?.Jvm is not null)
        {
            foreach (var element in version.Arguments.Jvm)
            {
                var value = ResolveArgumentElement(element, options, out var rules);
                if (value is null) continue;
                if (!RuleEvaluator.IsAllowed(rules, BuildFeatures(options))) continue;
                foreach (var part in value) args.Add(ReplaceTokens(part, version, versionId, account, options, classpath, nativesDirectory));
            }
        }
        else
        {
            args.Add("-Djava.library.path=" + nativesDirectory);
            args.Add("-cp");
            args.Add(classpath);
        }

        // authlib-injector（外置登录 / LittleSkin）
        if (account is not null && account.RequiresAuthlibInjector && !string.IsNullOrWhiteSpace(options.AuthlibInjectorPath)
            && File.Exists(options.AuthlibInjectorPath) && !string.IsNullOrWhiteSpace(options.AuthServerUrl))
        {
            var agentArg = $"-javaagent:{options.AuthlibInjectorPath}={options.AuthServerUrl}";
            if (!string.IsNullOrWhiteSpace(options.AuthPrefetched))
                args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + options.AuthPrefetched);
            args.Add(agentArg);
        }

        if (!string.IsNullOrWhiteSpace(options.ExtraJvmArguments))
            args.AddRange(SplitArguments(options.ExtraJvmArguments));

        args.Add($"-Dminecraft.launcher.brand=LPML");
        args.Add($"-Dminecraft.launcher.version={BuildInfo.Version}");
        return args;
    }

    public List<string> BuildGameArguments(VersionJson version, string versionId, Account? account,
        LaunchOptions options, string classpath, string nativesDirectory)
    {
        var args = new List<string>();
        var features = BuildFeatures(options);

        if (version.Arguments?.Game is not null)
        {
            foreach (var element in version.Arguments.Game)
            {
                var value = ResolveArgumentElement(element, options, out var rules);
                if (value is null) continue;
                if (!RuleEvaluator.IsAllowed(rules, features)) continue;
                foreach (var part in value) args.Add(ReplaceTokens(part, version, versionId, account, options, classpath, nativesDirectory));
            }
        }
        else if (!string.IsNullOrWhiteSpace(version.MinecraftArguments))
        {
            foreach (var part in version.MinecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                args.Add(ReplaceTokens(part, version, versionId, account, options, classpath, nativesDirectory));
        }

        if (options.Fullscreen) args.Add("--fullscreen");
        if (!string.IsNullOrWhiteSpace(options.ServerAddress)) { args.Add("--server"); args.Add(options.ServerAddress); }
        if (!string.IsNullOrWhiteSpace(options.AutoJoinAddress))
            args.Add($"--server={options.AutoJoinAddress.Split(':')[0]}");
        if (!string.IsNullOrWhiteSpace(options.AutoJoinAddress) && options.AutoJoinAddress.Contains(':'))
        {
            var p = options.AutoJoinAddress.Split(':');
            if (int.TryParse(p[1], out var port) && port > 0) args.Add($"--port={port}");
        }
        if (!string.IsNullOrWhiteSpace(options.WorldName)) { args.Add("--world"); args.Add(options.WorldName); }
        if (options.EnableCheats) args.Add("--enable-cheats");
        // 窗口标题：Minecraft 客户端原生参数 --title=TITLE。
        // 之前写成 JVM 属性 -Dlpml.window.title，游戏根本不读它，所以一直不生效。
        if (!string.IsNullOrWhiteSpace(options.CustomWindowTitle))
            args.Add("--title=" + options.CustomWindowTitle);

        if (!string.IsNullOrWhiteSpace(options.ExtraGameArguments))
            args.AddRange(SplitArguments(options.ExtraGameArguments));

        return args;
    }

    private static Dictionary<string, bool> BuildFeatures(LaunchOptions options)
    {
        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = options.WindowWidth > 0 && options.WindowHeight > 0
        };
        if (!string.IsNullOrWhiteSpace(options.ServerAddress))
        {
            features["has_quick_plays_support"] = true;
            features["is_quick_play_multiplayer"] = true;
        }
        if (!string.IsNullOrWhiteSpace(options.WorldName))
        {
            features["has_quick_plays_support"] = true;
            features["is_quick_play_singleplayer"] = true;
        }
        return features;
    }

    /// <summary>
    /// 参数元素既可能是字符串，也可能是 { rules: [...], value: string | string[] }。
    /// </summary>
    private static List<string>? ResolveArgumentElement(JsonElement element, LaunchOptions options, out List<Rule>? rules)
    {
        rules = null;
        if (element.ValueKind == JsonValueKind.String) return new List<string> { element.GetString() ?? "" };
        if (element.ValueKind != JsonValueKind.Object) return null;

        if (element.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            rules = Json.Deserialize<List<Rule>>(rulesElement.GetRawText());

        if (!element.TryGetProperty("value", out var valueElement)) return null;
        if (valueElement.ValueKind == JsonValueKind.String) return new List<string> { valueElement.GetString() ?? "" };
        if (valueElement.ValueKind == JsonValueKind.Array)
            return valueElement.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        return null;
    }

    private string ReplaceTokens(string input, VersionJson version, string versionId, Account? account,
        LaunchOptions options, string classpath, string nativesDirectory)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var workingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? _paths.GameDirectory : options.WorkingDirectory;
        var assetsIndex = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        var account0 = account;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["${auth_player_name}"] = account0?.UserName ?? "Player",
            ["${auth_uuid}"] = account0?.ShortUuid ?? HashHelper.OfflineUuid("Player"),
            ["${auth_access_token}"] = account0?.AccessToken ?? "0",
            ["${auth_session}"] = account0?.AccessToken ?? "0",
            ["${user_type}"] = account0?.UserType ?? "legacy",
            ["${user_properties}"] = "{}",
            ["${version_name}"] = versionId,
            ["${version_type}"] = version.Type ?? "release",
            ["${game_directory}"] = workingDirectory,
            ["${game_assets}"] = _paths.AssetsLegacyDirectory,
            ["${assets_root}"] = _paths.AssetsRootDirectory,
            ["${assets_index_name}"] = assetsIndex,
            ["${natives_directory}"] = nativesDirectory,
            ["${launcher_name}"] = "LPML",
            ["${launcher_version}"] = BuildInfo.Version,
            ["${classpath}"] = classpath,
            ["${classpath_separator}"] = Path.PathSeparator.ToString(),
            ["${library_directory}"] = _paths.LibrariesDirectory,
            ["${resolution_width}"] = options.WindowWidth.ToString(),
            ["${resolution_height}"] = options.WindowHeight.ToString(),
            ["${primary_jar}"] = _paths.VersionJarPath(versionId),
            ["${profile_name}"] = versionId,
            ["${quickPlayRealms}"] = "",
            ["${quickPlaySingleplayer}"] = options.WorldName ?? "",
            ["${quickPlayMultiplayer}"] = options.ServerAddress ?? "",
            // MC 1.19+ 新增：正版账户的 clientId / xuid。
            // 离线、外置账户没有这两个值——之前漏了替换，字面量 ${clientid} 被直接传给游戏。
            ["${clientid}"] = account0?.ClientId ?? "",
            ["${auth_xuid}"] = account0?.Xuid ?? ""
        };

        var result = input;
        foreach (var (token, value) in map)
            if (result.Contains(token, StringComparison.OrdinalIgnoreCase))
                result = result.Replace(token, value, StringComparison.OrdinalIgnoreCase);

        // 兜底：版本 JSON 若还带别的占位符，清成空串，别把 ${...} 原样丢给游戏
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\$\{[^}]+\}", "");
        return result;
    }

    public static IEnumerable<string> SplitArguments(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { list.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) list.Add(current.ToString());
        return list;
    }

    public string BuildCommandLine(VersionJson version, string versionId, Account? account, LaunchOptions options)
    {
        var classpath = string.Join(Path.PathSeparator, BuildClasspath(version, versionId, out _));
        var natives = NativesDirectory(versionId);
        var jvm = BuildJvmArguments(version, versionId, account, options, classpath, natives);
        var game = BuildGameArguments(version, versionId, account, options, classpath, natives);
        return string.Join(" ", jvm.Concat(new[] { version.MainClass ?? "net.minecraft.client.main.Main" }).Concat(game));
    }
}
