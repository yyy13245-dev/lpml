namespace LPML.Core.Game.Models;

public sealed class AssetIndex
{
    public Dictionary<string, AssetObject> Objects { get; set; } = new();
    public bool MapToResources { get; set; }
    public bool Virtual { get; set; }
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}
