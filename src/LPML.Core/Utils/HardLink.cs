using System.Runtime.InteropServices;

namespace LPML.Core.Utils;

/// <summary>
/// 硬链接复用：把已有的同名文件"链接"到目标位置。
/// 同卷下是零拷贝、零额外磁盘占用（两个目录项指向同一份数据），
/// 比复制快一个数量级，也不会让库文件多占一份空间。
/// </summary>
public static class HardLink
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>是否支持硬链接（Windows NTFS 支持，FAT32/exFAT 不支持）。</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>
    /// 尝试把 source 硬链接为 target。成功返回 true。
    /// 跨卷、文件系统不支持、目标已存在等情况会返回 false，调用方应回退到复制。
    /// </summary>
    public static bool TryCreate(string source, string target)
    {
        if (!Supported) return false;
        if (!File.Exists(source)) return false;

        try
        {
            if (File.Exists(target)) File.Delete(target);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (CreateHardLinkW(target, source, IntPtr.Zero)) return true;

            // 链接失败（多数是跨卷）就复制，别让流程断在这里
            File.Copy(source, target, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
