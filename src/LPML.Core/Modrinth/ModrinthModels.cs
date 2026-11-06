using System.Text.Json.Serialization;

namespace LPML.Core.Modrinth;

/// <summary>
/// Modrinth（Labrinth）API v2 的数据模型。
/// 字段全部用可空 + 宽松解析，API 结构变化时不会崩（Json.Options 已开启大小写不敏感与尾逗号容忍）。
/// </summary>

/// <summary>项目类型（对应 Modrinth 的 project_type）。</summary>
public enum ModrinthProjectType
{
    /// <summary>模组。</summary>
    Mod = 0,

    /// <summary>资源包。</summary>
    ResourcePack = 1,

    /// <summary>光影。</summary>
    Shader = 2,

    /// <summary>整合包。</summary>
    Modpack = 3,

    /// <summary>数据包。</summary>
    DataPack = 4
}

public static class ModrinthProjectTypeEx
{
    /// <summary>API 里的 project_type 值。</summary>
    public static string ApiName(this ModrinthProjectType type) => type switch
    {
        ModrinthProjectType.ResourcePack => "resourcepack",
        ModrinthProjectType.Shader => "shader",
        ModrinthProjectType.Modpack => "modpack",
        ModrinthProjectType.DataPack => "datapack",
        _ => "mod"
    };

    /// <summary>界面显示名（走语言包）。</summary>
    public static string DisplayName(this ModrinthProjectType type) => type switch
    {
        ModrinthProjectType.ResourcePack => L.T("String.Content.Type.ResourcePack"),
        ModrinthProjectType.Shader => L.T("String.Content.Type.Shader"),
        ModrinthProjectType.Modpack => L.T("String.Content.Type.Modpack"),
        ModrinthProjectType.DataPack => L.T("String.Content.Type.DataPack"),
        _ => L.T("String.Content.Type.Mod")
    };

    /// <summary>实例内相对存放目录（官方结构，PCL / HMCL 通用）。</summary>
    public static string RelativeDirectory(this ModrinthProjectType type) => type switch
    {
        ModrinthProjectType.ResourcePack => "resourcepacks",
        ModrinthProjectType.Shader => "shaderpacks",
        ModrinthProjectType.DataPack => "datapacks",
        _ => "mods"
    };
}

/// <summary>搜索返回的单个项目。</summary>
public sealed class ModrinthProject
{
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("versions")] public List<string>? GameVersions { get; set; }
    [JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("date_published")] public string? DatePublished { get; set; }
    [JsonPropertyName("date_modified")] public string? DateModified { get; set; }

    /// <summary>下载量显示文本。</summary>
    public string DownloadsText => L.F("String.Content.DownloadsCount", Downloads);

    /// <summary>Modrinth 项目页（用类型 + slug 拼）。</summary>
    public string WebUrl => string.IsNullOrWhiteSpace(Slug)
        ? "https://modrinth.com"
        : $"https://modrinth.com/{ProjectType ?? "mod"}/{Slug}";
}

/// <summary>/search 的响应。</summary>
public sealed class ModrinthSearchResponse
{
    [JsonPropertyName("hits")] public List<ModrinthProject>? Hits { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
}

/// <summary>/project 的详情（比搜索结果多成员与正文）。</summary>
public sealed class ModrinthProjectDetail
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("follows")] public long Follows { get; set; }
    [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("versions")] public List<string>? GameVersions { get; set; }
    [JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
    [JsonPropertyName("client_side")] public string? ClientSide { get; set; }
    [JsonPropertyName("server_side")] public string? ServerSide { get; set; }

    /// <summary>团队成员，由 /members 单独拉取后回填。</summary>
    public List<ModrinthMember>? Members { get; set; }
}

/// <summary>项目成员。</summary>
public sealed class ModrinthMember
{
    [JsonPropertyName("user")] public ModrinthUser? User { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
}

public sealed class ModrinthUser
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>版本文件（一个版本可能有多个文件，主文件优先）。</summary>
public sealed class ModrinthFile
{
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>文件的 sha1（顶层字段，Modrinth 直接给）。</summary>
    [JsonPropertyName("sha1")] public string? Sha1 { get; set; }

    /// <summary>hashes 结构里的 sha1，顶层没有时从这里取。</summary>
    [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
}

/// <summary>单个版本。</summary>
public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version_number")] public string? VersionNumber { get; set; }
    [JsonPropertyName("version_type")] public string? VersionType { get; set; }
    [JsonPropertyName("game_versions")] public List<string>? GameVersions { get; set; }
    [JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
    [JsonPropertyName("files")] public List<ModrinthFile>? Files { get; set; }
    [JsonPropertyName("dependencies")] public List<ModrinthDependency>? Dependencies { get; set; }
    [JsonPropertyName("date_published")] public string? DatePublished { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("featured")] public bool Featured { get; set; }

    /// <summary>主文件：primary 优先，其次第一个。</summary>
    [JsonIgnore]
    public ModrinthFile? PrimaryFile =>
        Files?.FirstOrDefault(f => f.Primary) ?? Files?.FirstOrDefault();

    /// <summary>发布通道（正式 / 测试 / 预览）。</summary>
    public string VersionTypeText => (VersionType ?? "") switch
    {
        "release" => L.T("String.Content.Channel.Release"),
        "beta" => L.T("String.Content.Channel.Beta"),
        "alpha" => L.T("String.Content.Channel.Alpha"),
        _ => L.T("String.Msg.Common.Unknown")
    };

    /// <summary>发布日期（API 给的是 ISO 串，解析失败就原样显示）。</summary>
    public string PublishedText =>
        DateTimeOffset.TryParse(DatePublished, out var d)
            ? d.LocalDateTime.ToString("yyyy-MM-dd")
            : (DatePublished ?? "");

    /// <summary>支持的加载器，如 "fabric, quilt"。</summary>
    public string LoadersText =>
        Loaders is { Count: > 0 }
            ? string.Join(", ", Loaders)
            : L.T("String.Content.AnyLoader");
}

/// <summary>按 MC 版本分组后的版本集合（界面上“每个 MC 版本一个下拉”）。</summary>
public sealed class ModrinthVersionGroup
{
    public string GameVersion { get; set; } = "";
    public List<ModrinthVersion> Versions { get; set; } = new();

    /// <summary>与当前实例版本一致的那组，界面上标「推荐」并置顶。</summary>
    public bool IsRecommended { get; set; }

    public string DisplayName => IsRecommended
        ? L.F("String.Content.Recommended", GameVersion)
        : GameVersion;

    public string CountText => L.F("String.Content.VersionCount", Versions.Count);
}

/// <summary>版本依赖。</summary>
public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("dependency_type")] public string? DependencyType { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }

    /// <summary>反查出来的依赖项目名（界面显示用，非 API 字段）。</summary>
    public string? Name { get; set; }

    public string TypeText => (DependencyType ?? "") switch
    {
        "required" => L.T("String.Content.Dep.Required"),
        "optional" => L.T("String.Content.Dep.Optional"),
        "incompatible" => L.T("String.Content.Dep.Incompatible"),
        "embedded" => L.T("String.Content.Dep.Embedded"),
        _ => L.T("String.Msg.Common.Unknown")
    };
}
