using LPML.Core.Game.Models;

namespace LPML.Core.Game;

/// <summary>Gradle/Maven 坐标解析：group:artifact:version[:classifier@ext]</summary>
public static class MavenHelper
{
    public static string ToPath(string? name, string? classifier = null, string extension = "jar")
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var parts = name.Split(':');
        if (parts.Length < 3) return name.Replace(':', '/');
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var file = $"{artifact}-{version}";
        if (parts.Length >= 4 && string.IsNullOrEmpty(classifier)) classifier = parts[3];
        if (!string.IsNullOrEmpty(classifier)) file += "-" + classifier;
        return $"{group}/{artifact}/{version}/{file}.{extension}";
    }

    /// <summary>把带 @ext 的坐标拆成纯坐标与扩展名。</summary>
    public static (string Name, string Extension) SplitExtension(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ("", "jar");
        var at = name.LastIndexOf('@');
        if (at <= 0) return (name, "jar");
        return (name[..at], name[(at + 1)..]);
    }

    public static string CurrentOsName()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "osx";
        return "linux";
    }

    public static string? NativeClassifier(LibraryNatives natives)
        => CurrentOsName() switch
        {
            "windows" => natives.Windows,
            "osx" => natives.Macos ?? natives.Osx,
            "linux" => natives.Linux,
            _ => null
        };
}
