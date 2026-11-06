using LPML.Core.Config;
using LPML.Core.Utils;

namespace LPML.Core.Download;

/// <summary>
/// 临时文件策略：
/// - 默认完全不写系统 TEMP（绿色要求），只用 <root>\cache\temp
/// - 用户在设置里开启后，才按"单文件上限 / 总量上限"写入系统 TEMP
/// - 退出时按设置清理，绝不留垃圾
/// </summary>
public sealed class TempFileManager
{
    private readonly AppPaths _paths;
    private readonly TempSettings _settings;
    private readonly object _sync = new();
    private long _systemTempUsage = -1;

    public TempFileManager(AppPaths paths, TempSettings settings)
    {
        _paths = paths;
        _settings = settings;
    }

    public string LocalTempRoot
    {
        get
        {
            FileHelper.EnsureDirectory(_paths.TempDirectory);
            return _paths.TempDirectory;
        }
    }

    public string SystemTempRoot => _paths.SystemTempDirectory;

    public bool SystemTempEnabled => _settings.Mode != TempWriteMode.Never;

    public long SystemTempUsage
    {
        get
        {
            lock (_sync)
            {
                if (_systemTempUsage < 0) _systemTempUsage = FileHelper.GetDirectorySize(SystemTempRoot);
                return _systemTempUsage;
            }
        }
    }

    /// <summary>判断某个文件是否允许写入系统 TEMP。</summary>
    public bool CanUseSystemTemp(long fileSize)
    {
        if (_settings.Mode == TempWriteMode.Never) return false;
        if (_settings.Mode == TempWriteMode.SmallFilesOnly &&
            fileSize > _settings.SingleFileTempLimitMb * 1024L * 1024L) return false;
        if (_settings.MaxTempSizeMb > 0)
        {
            var limit = _settings.MaxTempSizeMb * 1024L * 1024L;
            if (SystemTempUsage + fileSize > limit) return false;
        }
        return true;
    }

    /// <summary>为分片/临时文件分配路径，优先系统 TEMP（若允许），否则根目录缓存。</summary>
    public string Allocate(string fileName, long expectedSize)
    {
        var safe = FileHelper.SafeFileName(fileName);
        if (CanUseSystemTemp(expectedSize))
        {
            var dir = Path.Combine(SystemTempRoot, "dl");
            FileHelper.EnsureDirectory(dir);
            return Path.Combine(dir, safe);
        }
        var local = Path.Combine(LocalTempRoot, "dl");
        FileHelper.EnsureDirectory(local);
        return Path.Combine(local, safe);
    }

    public void NoteBytes(long bytes)
    {
        lock (_sync) { if (_systemTempUsage >= 0) _systemTempUsage += bytes; }
    }

    /// <summary>退出清理。</summary>
    public void Cleanup()
    {
        if (_settings.ClearOnExit) FileHelper.DeleteDirectory(SystemTempRoot);
        if (_settings.ClearLocalTempOnExit) FileHelper.DeleteDirectory(LocalTempRoot);
        lock (_sync) _systemTempUsage = 0;
    }

    /// <summary>本地缓存容量上限维护（0 = 不限）。</summary>
    public void TrimCacheIfNeeded()
    {
        if (_settings.MaxCacheSizeMb <= 0) return;
        var limit = _settings.MaxCacheSizeMb * 1024L * 1024L;
        var size = FileHelper.GetDirectorySize(_paths.CacheDirectory);
        if (size <= limit) return;
        var files = new DirectoryInfo(_paths.CacheDirectory)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        foreach (var file in files)
        {
            if (size <= limit) break;
            try
            {
                size -= file.Length;
                file.Delete();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
