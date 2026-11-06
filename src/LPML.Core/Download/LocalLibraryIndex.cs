using LPML.Core.Utils;

namespace LPML.Core.Download;

/// <summary>索引里的一条：某个 sha1 对应的本地文件在哪。</summary>
public sealed class LibraryIndexEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
}

/// <summary>
/// 本地库索引：把已有的版本目录当缓存用。
///
/// 思路：MC 的 libraries 本身就是内容寻址的（同一个库文件 sha1 一定相同），
/// 装过 1.20 再装 1.21 时，两者共用的 log4j / gson / lwjgl 完全没必要再下一遍。
/// 所以不去单独复制一份缓存，而是把已有目录里的 jar 按 sha1 建索引，
/// 命中就硬链接过去（零拷贝、不多占磁盘），没命中才真的下载。
///
/// 索引会持久化，靠「大小 + 修改时间」判断文件是否被替换过。
/// </summary>
public sealed class LocalLibraryIndex
{
    private readonly string _indexPath;
    private readonly object _sync = new();
    private Dictionary<string, LibraryIndexEntry> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>扫描过的文件数（用于日志）。</summary>
    public int ScannedCount { get; private set; }

    public LocalLibraryIndex(string configDirectory)
    {
        _indexPath = Path.Combine(configDirectory, "library-index.json");
        Load();
    }

    private void Load()
    {
        try
        {
            var loaded = Json.Load<Dictionary<string, LibraryIndexEntry>>(_indexPath);
            if (loaded is not null)
                _map = new Dictionary<string, LibraryIndexEntry>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception) { /* 索引损坏就当没有，重新扫 */ }
    }

    public void Save()
    {
        try
        {
            Dictionary<string, LibraryIndexEntry> snapshot;
            lock (_sync) snapshot = new(_map, StringComparer.OrdinalIgnoreCase);
            Json.Save(_indexPath, snapshot);
        }
        catch (Exception) { /* 存不下不影响使用，只是下次要重扫 */ }
    }

    /// <summary>
    /// 扫描一批根目录（通常是游戏目录、实例文件夹、第三方启动器目录），
    /// 把里面 jar 的 sha1 记进索引。已索引且文件没变过的会跳过。
    /// </summary>
    public int Scan(IEnumerable<string> roots, CancellationToken token = default)
    {
        var scanned = 0;
        var added = 0;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            // 兼容直接给 libraries 目录或给游戏根目录
            var libDir = Path.Combine(root, "libraries");
            var target = Directory.Exists(libDir) ? libDir : root;
            if (!Directory.Exists(target)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(target, "*.jar", SearchOption.AllDirectories);
            }
            catch (Exception) { continue; }

            foreach (var file in files)
            {
                if (token.IsCancellationRequested) break;
                scanned++;
                // 规模保护：几万个 jar 全算哈希会很慢
                if (scanned > 8000) break;

                try
                {
                    var info = new FileInfo(file);
                    if (info.Length <= 0) continue;

                    // 已索引且没变过 → 跳过算哈希（这一步最贵）
                    var knownKey = FindByPath(file);
                    if (knownKey is not null)
                    {
                        var known = _map[knownKey];
                        if (known.Size == info.Length && known.ModifiedTicks == info.LastWriteTimeUtc.Ticks)
                            continue;
                    }

                    var sha1 = HashHelper.Sha1File(file);
                    if (string.IsNullOrEmpty(sha1) || sha1.Length != 40) continue;

                    lock (_sync)
                    {
                        _map[sha1] = new LibraryIndexEntry
                        {
                            Path = file,
                            Size = info.Length,
                            ModifiedTicks = info.LastWriteTimeUtc.Ticks
                        };
                    }
                    added++;
                }
                catch (Exception) { /* 单个文件读不了就跳过 */ }
            }
        }

        ScannedCount = scanned;
        if (added > 0) Save();
        return added;
    }

    private string? FindByPath(string path)
    {
        lock (_sync)
        {
            foreach (var (key, entry) in _map)
                if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                    return key;
        }
        return null;
    }

    /// <summary>查某个 sha1 在本地是否已有可用文件。</summary>
    public bool TryGetPath(string sha1, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(sha1) || sha1.Length != 40) return false;

        LibraryIndexEntry? entry;
        lock (_sync) _map.TryGetValue(sha1, out entry);
        if (entry is null) return false;

        try
        {
            // 文件可能被删或被换过，用一次
            if (!File.Exists(entry.Path)) return false;
            var info = new FileInfo(entry.Path);
            if (info.Length != entry.Size || info.LastWriteTimeUtc.Ticks != entry.ModifiedTicks) return false;
        }
        catch (Exception) { return false; }

        path = entry.Path;
        return true;
    }

    /// <summary>新下载的文件直接登记进索引（sha1 已知，不用重算）。</summary>
    public void Register(string sha1, string path)
    {
        if (string.IsNullOrWhiteSpace(sha1) || sha1.Length != 40) return;
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            lock (_sync)
            {
                _map[sha1] = new LibraryIndexEntry
                {
                    Path = path,
                    Size = info.Length,
                    ModifiedTicks = info.LastWriteTimeUtc.Ticks
                };
            }
        }
        catch (Exception) { }
    }

    public int Count { get { lock (_sync) return _map.Count; } }
}
