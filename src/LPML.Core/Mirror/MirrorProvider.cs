using LPML.Core.Config;

namespace LPML.Core.Mirror;

/// <summary>
/// 镜像源实现。每个源提供：基础地址、分类地址构建、以及对官方 URL 的重写规则。
/// 官方源 = 恒等变换；BMCLAPI = 域名 + 路径前缀；光速镜像按域名分发。
/// MCBBS 已停止服务，枚举值只为兼容旧配置保留，MirrorManager 会把它重定向到 BMCLAPI。
/// </summary>
public sealed class MirrorProvider
{
    public MirrorKind Kind { get; }
    public string Name => Kind switch
    {
        MirrorKind.Official => L.T("String.Mirror.Official"),
        MirrorKind.BMCLAPI => L.T("String.Mirror.Bmclapi"),
        MirrorKind.MCBBS => L.T("String.Mirror.Mcbbs"),
        MirrorKind.FastMCMirror => L.T("String.Mirror.FastMCMirror"),
        _ => L.T("String.Msg.Common.Unknown")
    };

    public string BaseUrl { get; }

    /// <summary>官方源不需要替换规则。</summary>
    public IReadOnlyList<(string From, string To)> Replacements { get; }

    public MirrorProvider(MirrorKind kind, string? customBaseUrl = null)
    {
        Kind = kind;
        BaseUrl = string.IsNullOrWhiteSpace(customBaseUrl) ? DefaultBaseUrl(kind) : customBaseUrl.TrimEnd('/');
        Replacements = BuildReplacements();
    }

    public static string DefaultBaseUrl(MirrorKind kind) => kind switch
    {
        MirrorKind.BMCLAPI => "https://bmclapi2.bangbang93.com",
        MirrorKind.MCBBS => "https://download.mcbbs.net",
        // 光速镜像按域名分发，主域名承担 launcher-meta（版本清单 / Java 运行时索引）
        MirrorKind.FastMCMirror => "https://launchermeta.fastmcmirror.org",
        _ => "https://launchermeta.mojang.com"
    };

    /// <summary>
    /// 光速镜像的域名映射表（依据 fastmcmirror.org 官方公布的替换规则）。
    /// 它不使用统一路径前缀，而是每个官方域名对应一个专属域名。
    /// 未列出的服务（NeoForge / Quilt / authlib-injector）官方源未被其镜像，保持原样。
    /// </summary>
    private static IReadOnlyList<(string, string)> FastMCMirrorReplacements() => new List<(string, string)>
    {
        ("https://piston-meta.mojang.com", "https://launchermeta.fastmcmirror.org"),
        ("http://piston-meta.mojang.com", "https://launchermeta.fastmcmirror.org"),
        ("https://launchermeta.mojang.com", "https://launchermeta.fastmcmirror.org"),
        ("http://launchermeta.mojang.com", "https://launchermeta.fastmcmirror.org"),
        ("https://launcher.mojang.com", "https://launcher.fastmcmirror.org"),
        ("http://launcher.mojang.com", "https://launcher.fastmcmirror.org"),
        ("https://libraries.minecraft.net", "https://libraries.fastmcmirror.org"),
        ("http://libraries.minecraft.net", "https://libraries.fastmcmirror.org"),
        ("https://libraries.mojang.com", "https://libraries.fastmcmirror.org"),
        ("http://libraries.mojang.com", "https://libraries.fastmcmirror.org"),
        ("https://resources.download.minecraft.net", "https://resources.fastmcmirror.org"),
        ("http://resources.download.minecraft.net", "https://resources.fastmcmirror.org"),
        ("https://files.minecraftforge.net/maven", "https://forge.fastmcmirror.org"),
        ("https://maven.minecraftforge.net", "https://forge.fastmcmirror.org"),
        ("https://maven.fabricmc.net", "https://fabric.fastmcmirror.org"),
        ("https://meta.fabricmc.net", "https://fabricmeta.fastmcmirror.org"),
        ("https://repo.mumfrey.com", "https://liteloader.fastmcmirror.org"),
        ("http://dl.liteloader.com/versions", "https://liteloader.fastmcmirror.org/versions")
    };

    private IReadOnlyList<(string, string)> BuildReplacements()
    {
        if (Kind == MirrorKind.Official) return Array.Empty<(string, string)>();
        if (Kind == MirrorKind.FastMCMirror) return FastMCMirrorReplacements();

        var b = BaseUrl;
        return new List<(string, string)>
        {
            ("https://piston-meta.mojang.com", b),
            ("http://piston-meta.mojang.com", b),
            ("https://launchermeta.mojang.com", b),
            ("http://launchermeta.mojang.com", b),
            ("https://launcher.mojang.com", b),
            ("http://launcher.mojang.com", b),
            ("https://piston-data.mojang.com", b),
            ("https://libraries.minecraft.net", b + "/maven"),
            ("http://libraries.minecraft.net", b + "/maven"),
            ("https://resources.download.minecraft.net", b + "/assets"),
            ("http://resources.download.minecraft.net", b + "/assets"),
            ("https://files.minecraftforge.net/maven", b + "/maven"),
            ("https://maven.minecraftforge.net", b + "/maven"),
            ("https://maven.neoforged.net/releases", b + "/maven"),
            ("https://maven.fabricmc.net", b + "/maven"),
            ("https://meta.fabricmc.net", b + "/fabric-meta"),
            ("https://meta.quiltmc.org", b + "/quilt-meta"),
            ("https://maven.quiltmc.org/repository/release", b + "/maven"),
            ("https://authlib-injector.yushi.moe", b + "/mirrors/authlib-injector"),
            ("https://dl.liteloader.com/versions", b + "/maven/com/mumfrey/liteloader/versions")
        };
    }

    // ── 分类地址 ───────────────────────────────────────────
    // 三种风格：
    //   Official      → 直接返回官方 URL
    //   BMCLAPI/MCBBS → BaseUrl + BMCLAPI 风格路径前缀
    //   FastMCMirror  → 官方 URL 经域名替换（它没有统一路径前缀）

    /// <summary>是否走 BMCLAPI 风格的路径拼接。</summary>
    private bool IsBmclapiStyle => Kind is MirrorKind.BMCLAPI or MirrorKind.MCBBS;

    /// <summary>按源的风格解析地址：光速镜像一律用「官方地址 + 域名替换」。</summary>
    private string Resolve(string officialUrl, string bmclapiPath) => Kind switch
    {
        MirrorKind.Official => officialUrl,
        MirrorKind.FastMCMirror => Rewrite(officialUrl),
        _ => BaseUrl + bmclapiPath
    };

    public string VersionManifestUrl() =>
        Resolve("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
                "/mc/game/version_manifest_v2.json");

    public string JavaRuntimeIndexUrl() =>
        Resolve("https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json",
                "/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json");

    public string FabricGameVersionsUrl() =>
        Resolve("https://meta.fabricmc.net/v2/versions/game", "/fabric-meta/v2/versions/game");

    public string FabricLoaderVersionsUrl(string? mcVersion = null)
    {
        var tail = string.IsNullOrWhiteSpace(mcVersion)
            ? "/v2/versions/loader"
            : $"/v2/versions/loader/{mcVersion}";
        return Resolve("https://meta.fabricmc.net" + tail, "/fabric-meta" + tail);
    }

    public string FabricInstallerVersionsUrl() =>
        Resolve("https://meta.fabricmc.net/v2/versions/installer", "/fabric-meta/v2/versions/installer");

    /// <summary>Fabric 版本 profile json（直接作为独立版本使用）。</summary>
    public string FabricProfileUrl(string mcVersion, string loaderVersion) =>
        Resolve($"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json",
                $"/fabric-meta/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json");

    /// <summary>Quilt 版本 profile json。</summary>
    public string QuiltProfileUrl(string mcVersion, string loaderVersion) =>
        Resolve($"https://meta.quiltmc.org/v3/versions/loader/{mcVersion}/{loaderVersion}/profile/json",
                $"/quilt-meta/v3/versions/loader/{mcVersion}/{loaderVersion}/profile/json");

    public string ForgeListUrl(string? mcVersion = null) =>
        Resolve("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json",
                "/forge/list/" + (mcVersion ?? ""));

    public string NeoForgeListUrl(string mcVersion) =>
        Resolve($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{mcVersion}/maven-metadata.xml",
                "/neoforge/list/" + mcVersion);

    public string QuiltLoaderVersionsUrl() =>
        Resolve("https://meta.quiltmc.org/v3/versions/loader", "/quilt-meta/v3/versions/loader");

    public string QuiltInstallerVersionsUrl() =>
        Resolve("https://meta.quiltmc.org/v3/versions/installer", "/quilt-meta/v3/versions/installer");

    /// <summary>
    /// OptiFine 版本列表。光速镜像用自己的格式（/versionList），
    /// 与 BMCLAPI 的 /optifine/{mc} 不同，因此单独分支。
    /// </summary>
    public string OptiFineListUrl(string mcVersion) => Kind switch
    {
        MirrorKind.FastMCMirror => "https://optifine.fastmcmirror.org/versionList",
        _ => BaseUrl + "/optifine/" + mcVersion
    };

    /// <summary>
    /// OptiFine 安装器 jar 的下载地址。
    /// BMCLAPI 用 /optifine/{mc}/{type}/{patch}；光速镜像用 /OptiFine_{mc}_{type}_{patch}.jar。
    /// </summary>
    public string OptiFineDownloadUrl(string mcVersion, string type, string patch) => Kind switch
    {
        MirrorKind.FastMCMirror =>
            $"https://optifine.fastmcmirror.org/OptiFine_{mcVersion}_{type}_{patch}.jar",
        _ => BaseUrl + $"/optifine/{mcVersion}/{type}/{patch}"
    };

    /// <summary>Forge 安装器 jar（官方 maven → 镜像）。</summary>
    public string ForgeInstallerUrl(string mcVersion, string forgeVersion)
    {
        var path = $"/net/minecraftforge/forge/{mcVersion}-{forgeVersion}/forge-{mcVersion}-{forgeVersion}-installer.jar";
        return Resolve("https://maven.minecraftforge.net" + path, "/maven" + path);
    }

    /// <summary>NeoForge 安装器 jar。光速镜像未镜像 NeoForge，Rewrite 会自动回退官方地址。</summary>
    public string NeoForgeInstallerUrl(string mcVersion, string version)
    {
        var path = $"/net/neoforged/neoforge/{mcVersion}-{version}/neoforge-{mcVersion}-{version}-installer.jar";
        return Resolve("https://maven.neoforged.net/releases" + path, "/maven" + path);
    }

    /// <summary>NeoForge 列表给出的相对 maven 路径 → 绝对地址。</summary>
    public string NeoForgeMavenUrl(string mavenPath) =>
        Resolve("https://maven.neoforged.net/releases" + mavenPath.Replace("/maven", ""),
                mavenPath);

    /// <summary>光速镜像的 OptiFine 域名，供列表解析时识别来源。</summary>
    internal static string OptiFineMirrorBase => "https://optifine.fastmcmirror.org";

    public string AuthlibInjectorLatestUrl() =>
        Resolve("https://authlib-injector.yushi.moe/artifact/latest.json",
                "/mirrors/authlib-injector/artifact/latest.json");

    public string ModrinthApiUrl() => "https://api.modrinth.com/v2";
    public string CurseForgeApiUrl() => "https://api.curseforge.com";

    /// <summary>Mojang / Fabric 官方资源服务器地址（皮肤、纹理等）。</summary>
    public string TextureBaseUrl() => "https://textures.minecraft.net";


    public string Rewrite(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        if (Kind == MirrorKind.Official) return url;

        // 按 Uri 的 host 精确匹配，再做路径前缀匹配。
        // 纯字符串 StartsWith 会把 "piston-meta.mojang.com.evil.com" 也当成官方域名命中。
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        foreach (var (from, to) in Replacements)
        {
            if (!Uri.TryCreate(from, UriKind.Absolute, out var rule)) continue;
            if (!string.Equals(uri.Host, rule.Host, StringComparison.OrdinalIgnoreCase)) continue;
            var rulePath = rule.AbsolutePath;
            if (rulePath == "/") rulePath = "";
            if (!uri.AbsolutePath.StartsWith(rulePath, StringComparison.Ordinal)) continue;
            var tail = uri.PathAndQuery.Length >= rulePath.Length
                ? uri.PathAndQuery[rulePath.Length..]
                : "";
            return to + tail;
        }
        return url;
    }
}
