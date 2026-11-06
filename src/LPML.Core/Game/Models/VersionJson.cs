using System.Text.Json;

namespace LPML.Core.Game.Models;

/// <summary>Mojang 版本 JSON（1.13+ 的 arguments 结构用 JsonElement 保留原样，启动时再展开）。</summary>
public sealed class VersionJson
{
    public string? Id { get; set; }
    public string? InheritsFrom { get; set; }
    public string? MainClass { get; set; }
    public string? Assets { get; set; }
    public AssetIndexInfo? AssetIndex { get; set; }
    public Dictionary<string, Artifact>? Downloads { get; set; }
    public JavaVersionInfo? JavaVersion { get; set; }
    public List<Library>? Libraries { get; set; }
    public ArgumentList? Arguments { get; set; }
    public string? MinecraftArguments { get; set; }
    public string? Type { get; set; }
    public string? Time { get; set; }
    public string? ReleaseTime { get; set; }
    public int? MinimumLauncherVersion { get; set; }
    public LoggingInfo? Logging { get; set; }

    /// <summary>LPML 附加：记录该版本安装了哪些加载器。</summary>
    public Dictionary<string, string>? LpmlLoaders { get; set; }
}

public sealed class ArgumentList
{
    public List<JsonElement>? Game { get; set; }
    public List<JsonElement>? Jvm { get; set; }
}

public sealed class Artifact
{
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
    public string? Path { get; set; }
    public string? Id { get; set; }
}

public sealed class AssetIndexInfo
{
    public string? Id { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string? Url { get; set; }
}

public sealed class JavaVersionInfo
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class Library
{
    public string? Name { get; set; }

    /// <summary>自定义 Maven 根地址（Forge / Fabric 常用）。</summary>
    public string? Url { get; set; }

    public LibraryDownloads? Downloads { get; set; }
    public LibraryNatives? Natives { get; set; }
    public LibraryExtract? Extract { get; set; }
    public List<Rule>? Rules { get; set; }

    /// <summary>PCL / HMCL 风格的校验字段。</summary>
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public bool? SkipMissing { get; set; }
    public string? Md5 { get; set; }
}

public sealed class LibraryDownloads
{
    public Artifact? Artifact { get; set; }
    public Dictionary<string, Artifact>? Classifiers { get; set; }
}

public sealed class LibraryNatives
{
    public string? Windows { get; set; }
    public string? Linux { get; set; }
    public string? Osx { get; set; }
    public string? Macos { get; set; }
}

public sealed class LibraryExtract
{
    public List<string>? Exclude { get; set; }
}

public sealed class Rule
{
    public string? Action { get; set; }
    public OsCondition? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsCondition
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Arch { get; set; }
}

public sealed class LoggingInfo
{
    public LoggingClient? Client { get; set; }
}

public sealed class LoggingClient
{
    public string? Argument { get; set; }
    public Artifact? File { get; set; }
    public string? Type { get; set; }
}
