namespace LPML.Core.Integration;

/// <summary>整合包格式。不同格式的处理方式差别很大，必须先识别再动手。</summary>
public enum ModpackFormat
{
    Unknown = 0,

    /// <summary>Modrinth 的 .mrpack（本质是 zip，含 modrinth.index.json）。模组需联网下载。</summary>
    Modrinth = 1,

    /// <summary>CurseForge 导出的 zip（含 manifest.json）。模组需 CF API，见下方说明。</summary>
    CurseForge = 2,

    /// <summary>MultiMC / Prism 导出的 zip（含 mmc-pack.json）。模组直接打包在内，无需联网。</summary>
    MultiMC = 3,

    /// <summary>HMCL 导出的 zip（含 .minecraft 目录）。模组直接打包在内。</summary>
    Hmcl = 4,

    /// <summary>通用 zip：内含 .minecraft 或直接是游戏目录结构。</summary>
    PlainZip = 5
}

public static class ModpackFormatEx
{
    public static string DisplayName(this ModpackFormat format) => format switch
    {
        ModpackFormat.Modrinth => "Modrinth (.mrpack)",
        ModpackFormat.CurseForge => "CurseForge (.zip)",
        ModpackFormat.MultiMC => "MultiMC / Prism (.zip)",
        ModpackFormat.Hmcl => "HMCL (.zip)",
        ModpackFormat.PlainZip => L.T("String.Core.Modpack.Format.PlainZip"),
        _ => L.T("String.Msg.Common.Unknown")
    };

    /// <summary>这类包的模组是否已打包在 zip 里（true = 不用联网就能装上）。</summary>
    public static bool ModsIncluded(this ModpackFormat format) => format switch
    {
        ModpackFormat.MultiMC or ModpackFormat.Hmcl or ModpackFormat.PlainZip => true,
        _ => false
    };

    /// <summary>该格式是否需要访问 CurseForge API 才能拿到模组。</summary>
    public static bool NeedsCurseForgeApi(this ModpackFormat format) => format == ModpackFormat.CurseForge;
}
