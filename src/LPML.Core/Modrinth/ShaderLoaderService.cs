using LPML.Core.Instances;

namespace LPML.Core.Modrinth;

/// <summary>光影加载器种类。</summary>
public enum ShaderLoaderKind
{
    None = 0,

    /// <summary>Iris Shaders（Fabric / Forge / NeoForge 上的现代方案）。</summary>
    Iris = 1,

    /// <summary>OptiFine（自带光影，但对加载器和版本挑剔）。</summary>
    OptiFine = 2,

    /// <summary>Oculus（Forge 上的 Iris 移植版）。</summary>
    Oculus = 3
}

/// <summary>实例的加载器（用于判断该推荐哪个光影加载器）。</summary>
public enum InstanceLoaderKind
{
    Vanilla = 0,
    Fabric = 1,
    Forge = 2,
    NeoForge = 3,
    Quilt = 4,
    OptiFine = 5,
    Unknown = 9
}

/// <summary>
/// 光影加载器检测与推荐。
/// 下载光影前必须先有加载器——没有 Iris/OptiFine，shaderpacks 里的东西不会被读取。
/// </summary>
public static class ShaderLoaderService
{
    private static readonly (string[] JarPatterns, ShaderLoaderKind Kind)[] Known =
    {
        (new[] { "iris", "iris-mc" }, ShaderLoaderKind.Iris),
        (new[] { "oculus" }, ShaderLoaderKind.Oculus),
        (new[] { "optifine", "optifabric" }, ShaderLoaderKind.OptiFine)
    };

    /// <summary>从实例名/加载器名推断加载器种类。</summary>
    public static InstanceLoaderKind ParseLoader(string? loaderName)
    {
        var name = (loaderName ?? "").Trim().ToLowerInvariant();
        if (name.Contains("fabric")) return InstanceLoaderKind.Fabric;
        if (name.Contains("neoforge")) return InstanceLoaderKind.NeoForge;
        if (name.Contains("forge")) return InstanceLoaderKind.Forge;
        if (name.Contains("quilt")) return InstanceLoaderKind.Quilt;
        if (name.Contains("optifine")) return InstanceLoaderKind.OptiFine;
        if (name.Length == 0) return InstanceLoaderKind.Vanilla;
        return InstanceLoaderKind.Unknown;
    }

    /// <summary>
    /// 扫描实例的 mods 目录，判断是否已装光影加载器。
    /// 用文件名匹配（jar 内部解析成本高且不可靠）。
    /// </summary>
    public static ShaderLoaderKind DetectInstalled(string? instanceModsDirectory)
    {
        if (string.IsNullOrWhiteSpace(instanceModsDirectory) || !Directory.Exists(instanceModsDirectory))
            return ShaderLoaderKind.None;

        try
        {
            var files = Directory.EnumerateFiles(instanceModsDirectory!, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.ToLowerInvariant())
                .Where(n => n.EndsWith(".jar") || n.EndsWith(".jar.disabled"))
                .ToList();

            foreach (var (patterns, kind) in Known)
                foreach (var pattern in patterns)
                    if (files.Any(f => f.Contains(pattern)))
                        return kind;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return ShaderLoaderKind.None;
    }

    /// <summary>该加载器下推荐用哪个光影加载器。</summary>
    public static ShaderLoaderKind Recommend(InstanceLoaderKind loader) => loader switch
    {
        InstanceLoaderKind.Fabric or InstanceLoaderKind.Quilt => ShaderLoaderKind.Iris,
        InstanceLoaderKind.Forge or InstanceLoaderKind.NeoForge => ShaderLoaderKind.Oculus,
        InstanceLoaderKind.OptiFine => ShaderLoaderKind.OptiFine,
        // 原版没有加载器，得先装 Fabric + Iris
        _ => ShaderLoaderKind.Iris
    };

    /// <summary>光影加载器在 Modrinth 上的 slug（用于一键跳转搜索）。</summary>
    public static string ModrinthSlug(ShaderLoaderKind kind) => kind switch
    {
        ShaderLoaderKind.Iris => "iris",
        ShaderLoaderKind.Oculus => "oculus",
        ShaderLoaderKind.OptiFine => "optifine",
        _ => "iris"
    };

    public static string DisplayName(ShaderLoaderKind kind) => kind switch
    {
        ShaderLoaderKind.Iris => "Iris Shaders",
        ShaderLoaderKind.Oculus => "Oculus",
        ShaderLoaderKind.OptiFine => "OptiFine",
        _ => L.T("String.Content.Shader.None")
    };

    /// <summary>
    /// 检查是否可以装光影。返回 null 表示可以，否则返回需要提示用户的文案。
    /// </summary>
    public static string? Check(GameInstance instance, string? instanceModsDirectory)
    {
        var installed = DetectInstalled(instanceModsDirectory);
        if (installed != ShaderLoaderKind.None) return null;

        var loader = ParseLoader(instance.LoaderName);
        var recommended = Recommend(loader);

        // 原版没加载器，光装 Iris 也跑不起来，得先装 Fabric
        if (loader is InstanceLoaderKind.Vanilla or InstanceLoaderKind.Unknown)
            return L.T("String.Content.Shader.NeedLoaderFirst");

        return L.F("String.Content.Shader.NeedShaderLoader", DisplayName(recommended));
    }

    /// <summary>是否已具备条件。</summary>
    public static bool IsReady(GameInstance instance, string? instanceModsDirectory) =>
        DetectInstalled(instanceModsDirectory) != ShaderLoaderKind.None;
}
