using System.IO.Compression;

namespace LPML.Core.Utils;

public static class FileHelper
{
    public static void EnsureDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(path);
    }

    public static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return 0;
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { size += file.Length; } catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return size;
    }

    /// <summary>安全文件名：把非法字符替换为下划线，并限制长度。</summary>
    public static string SafeFileName(string name, int maxLength = 80)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result)) result = "unnamed";
        if (result.Length > maxLength) result = result[..maxLength];
        return result;
    }

    public static string HumanSize(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public static string HumanSpeed(double bytesPerSecond)
        => HumanSize((long)bytesPerSecond) + "/s";

    public static string FormatTime(TimeSpan span)
    {
        if (span.TotalSeconds < 0) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    /// <summary>从 zip 中提取指定条目到目标路径。</summary>
    public static void ExtractEntry(ZipArchive archive, string entryName, string destination)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) throw new FileNotFoundException(L.F("String.Core.Err.Zip.EntryMissing", entryName));
        EnsureDirectory(Path.GetDirectoryName(destination));
        entry.ExtractToFile(destination, true);
    }

    public static void ExtractDirectory(ZipArchiveEntry entry, string targetRoot)
    {
        var full = Path.GetFullPath(Path.Combine(targetRoot, entry.FullName));
        if (!full.StartsWith(Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase)) return;
        EnsureDirectory(Path.GetDirectoryName(full));
        entry.ExtractToFile(full, true);
    }

    public static async Task<string> ReadAllTextAsync(string path, CancellationToken token = default)
        => await File.ReadAllTextAsync(path, token).ConfigureAwait(false);

    public static async Task WriteAllTextAsync(string path, string content, CancellationToken token = default)
    {
        EnsureDirectory(Path.GetDirectoryName(path));
        await File.WriteAllTextAsync(path, content, token).ConfigureAwait(false);
    }

    public static async Task WriteAllBytesAsync(string path, byte[] content, CancellationToken token = default)
    {
        EnsureDirectory(Path.GetDirectoryName(path));
        await File.WriteAllBytesAsync(path, content, token).ConfigureAwait(false);
    }
}
