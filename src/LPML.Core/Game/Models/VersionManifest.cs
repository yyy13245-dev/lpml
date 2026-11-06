using System.Text.Json;

namespace LPML.Core.Game.Models;

public sealed class VersionManifest
{
    public ManifestLatest? Latest { get; set; }
    public List<ManifestVersion> Versions { get; set; } = new();
}

public sealed class ManifestLatest
{
    public string? Release { get; set; }
    public string? Snapshot { get; set; }
}

public sealed class ManifestVersion
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public string Url { get; set; } = "";
    public string? Time { get; set; }
    public string? ReleaseTime { get; set; }
    public string? Sha1 { get; set; }
    public int? ComplianceLevel { get; set; }
}
