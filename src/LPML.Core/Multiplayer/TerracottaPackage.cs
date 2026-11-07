using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.Core.Multiplayer;

/// <summary>一次默认路线安装的结果。</summary>
public sealed record TerracottaInstallResult(string Version, string Checksum, string SourceUrl);

/// <summary>
/// 陶瓦联机核心的默认安装路线。
///
///  - 用户填了「核心包下载地址」→ 完全按用户配置走（旧行为保留）；
///  - 没填 → 用与 HMCL 相同的 terracotta.json 元数据 + 国内优先的镜像模板自动下载 v0.4.2；
///  - 元数据拉不到也能用：内置模板与已核实的 SHA-512 足够完成安装；
///  - 发行包是 .pkg.tar.gz（内含可执行文件与 Windows 的 VCRUNTIME140.DLL），解压后落盘。
///
/// 本类只下载未经修改的官方二进制，不静态/动态链接陶瓦代码；
/// 运行时通过本机 HTTP API 交互，符合其 AGPL 例外条款。
/// </summary>
public sealed class TerracottaPackage
{
    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly TerracottaSettings _settings;

    public TerracottaPackage(AppPaths paths, HttpService http, TerracottaSettings settings)
    {
        _paths = paths;
        _http = http;
        _settings = settings;
    }

    /// <summary>日志回调（Core 层不直接写 WPF，交给上层接）。</summary>
    public Action<string>? Log { get; set; }

    public async Task<TerracottaInstallResult> InstallAsync(
        string? sourceUrl,
        string? expectedChecksum,
        IProgress<(long Received, long Total)>? progress,
        CancellationToken token)
    {
        var toolDir = Path.Combine(_paths.AppDirectory, "tools", "terracotta");
        Directory.CreateDirectory(toolDir);
        var target = ExecutablePath();

        // ── 兼容 HMCL：用户可指定 HMCL 已下载的核心目录，直接复用同一份二进制 ──
        var external = TryReuseExternalBinary(target);
        if (external is not null)
        {
            var reuseVersion = TerracottaDefaults.NormalizeVersion(
                string.IsNullOrWhiteSpace(_settings.VersionOverride)
                    ? TerracottaDefaults.Version
                    : _settings.VersionOverride);
            _settings.InstalledVersion = reuseVersion;
            _settings.ExpectedChecksum = ""; // 复用外部二进制时不做本地哈希校验
            _settings.LastUpdatedAt = DateTimeOffset.Now;
            return new TerracottaInstallResult(reuseVersion, "", external);
        }

        var classifier = TerracottaPlatform.Classifier();
        var metadata = await TryLoadMetadataAsync(token).ConfigureAwait(false);

        var version = TerracottaDefaults.NormalizeVersion(
            !string.IsNullOrWhiteSpace(_settings.VersionOverride)
                ? _settings.VersionOverride
                : !string.IsNullOrWhiteSpace(metadata?.version_latest)
                    ? metadata!.version_latest
                    : TerracottaDefaults.Version);
        if (string.IsNullOrWhiteSpace(version)) version = TerracottaDefaults.Version;

        var urls = BuildCandidateUrls(sourceUrl, version, classifier, metadata);
        if (urls.Count == 0)
            throw new InvalidOperationException(L.T("String.Multi.Err.NoPackageUrl"));

        var hash = ResolveExpectedChecksum(expectedChecksum, classifier, metadata);
        var tmp = Path.Combine(_paths.TempDirectory, "terracotta_" + Guid.NewGuid().ToString("N")[..8] + ".bin");
        FileHelper.EnsureDirectory(Path.GetDirectoryName(tmp));

        string? usedUrl = null;
        try
        {
            foreach (var url in urls)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    Log?.Invoke(L.F("String.Multi.TryingSource", url));
                    // 每个源单独限时：GitHub 在国内经常连得上但迟迟不吐数据，
                    // 不能让它一个人把整轮安装拖住。超时只换下一个源。
                    using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    sourceCts.CancelAfter(TimeSpan.FromSeconds(12));
                    await _http.DownloadToFileAsync(url, tmp, progress, sourceCts.Token).ConfigureAwait(false);
                    usedUrl = url;
                    break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Log?.Invoke(L.F("String.Multi.SourceFailed", L.T("String.Multi.SourceTimeout")));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log?.Invoke(L.F("String.Multi.SourceFailed", ex.Message));
                }
            }

            if (usedUrl is null)
                throw new InvalidOperationException(L.T("String.Multi.Err.AllSourcesFailed"));

            var actual = await ComputeHashAsync(tmp, hash, token).ConfigureAwait(false);
            if (_settings.VerifyChecksum && !string.IsNullOrWhiteSpace(hash) &&
                !actual.Equals(hash.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(L.F("String.Multi.Err.ChecksumMismatch", actual, hash));
            }

            await ExtractAsync(tmp, toolDir, target, token).ConfigureAwait(false);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            // 设置里存的是「已安装可执行文件」的哈希，而不是压缩包的哈希：
            // IsInstalled 每次会拿它校验 target，两者必须指向同一个文件。
            var installedHash = Sha512Hex(target);
            _settings.InstalledVersion = version;
            _settings.ExpectedChecksum = installedHash;
            _settings.LastUpdatedAt = DateTimeOffset.Now;
            return new TerracottaInstallResult(version, installedHash, usedUrl);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private string ExecutablePath()
    {
        var dir = Path.Combine(_paths.AppDirectory, "tools", "terracotta");
        var name = OperatingSystem.IsWindows()
            ? "terracotta.exe"
            : OperatingSystem.IsMacOS() ? "terracotta-macos" : "terracotta-linux";
        return Path.Combine(dir, name);
    }

    /// <summary>
    /// 若用户配置了「复用 HMCL 的核心目录」，且目录内有官方可执行文件，则复制过来。
    /// 这样 HMCL 与 LPML 用同一份未经修改的二进制，也免去重复下载。
    /// </summary>
    private string? TryReuseExternalBinary(string target)
    {
        var dir = _settings.ExternalCorePath?.Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

        try
        {
            var candidate = Directory.EnumerateFiles(dir)
                .Where(p => Path.GetFileName(p).StartsWith("terracotta-", StringComparison.OrdinalIgnoreCase))
                .Where(p => OperatingSystem.IsWindows()
                    ? p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    : !p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();
            if (candidate is null) return null;

            File.Copy(candidate, target, true);
            CopyRuntimeDll(dir, Path.GetDirectoryName(target)!);
            Log?.Invoke(L.F("String.Multi.ReusedHmclCore", candidate));
            return candidate;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void CopyRuntimeDll(string sourceDir, string targetDir)
    {
        const string dll = "VCRUNTIME140.DLL";
        var source = Path.Combine(sourceDir, dll);
        if (!File.Exists(source)) return;
        try { File.Copy(source, Path.Combine(targetDir, dll), true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<TerracottaMetadata?> TryLoadMetadataAsync(CancellationToken token)
    {
        if (!_settings.UseOnlineMetadata) return null;

        foreach (var url in TerracottaDefaults.MetadataUrls)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var result = await _http.GetJsonAsync<TerracottaMetadata>(url, cts.Token).ConfigureAwait(false);
                if (result is not null && !string.IsNullOrWhiteSpace(result.version_latest))
                {
                    Log?.Invoke(L.F("String.Multi.MetadataLoaded", result.version_latest));
                    return result;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { /* 超时换下一个 */ }
            catch (Exception ex) { Log?.Invoke(L.F("String.Multi.MetadataFailed", ex.Message)); }
        }
        return null;
    }

    private List<string> BuildCandidateUrls(
        string? sourceUrl, string version, string classifier, TerracottaMetadata? metadata)
    {
        var list = new List<string>();
        var custom = (sourceUrl ?? _settings.PackageUrl ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(custom))
        {
            // 用户配置优先；模板占位符照样替换，方便填模板而不是完整地址。
            list.Add(TerracottaDefaults.BuildUrl(custom, version, classifier));
        }
        else if (metadata is not null)
        {
            foreach (var template in metadata.Templates())
                list.Add(TerracottaDefaults.BuildUrl(template, version, classifier));
        }

        // 内置模板兜底（去重），保证离线元数据时默认路线仍然可用。
        foreach (var template in TerracottaDefaults.DownloadTemplates)
        {
            var url = TerracottaDefaults.BuildUrl(template, version, classifier);
            if (!list.Contains(url, StringComparer.OrdinalIgnoreCase)) list.Add(url);
        }
        return list;
    }

    private string ResolveExpectedChecksum(
        string? expected, string classifier, TerracottaMetadata? metadata)
    {
        var custom = (expected ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            // 上一次安装写回设置的是「可执行文件」哈希，不是压缩包哈希。
            // 重装时若它仍等于本地 exe，就当成历史值忽略，改由元数据/内置表提供包哈希。
            try
            {
                if (File.Exists(ExecutablePath()) &&
                    Sha512Hex(ExecutablePath()).Equals(custom, StringComparison.OrdinalIgnoreCase))
                    custom = "";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (!string.IsNullOrWhiteSpace(custom)) return custom;

        if (metadata is not null && metadata.packages is not null &&
            metadata.packages.TryGetValue(classifier, out var package) &&
            !string.IsNullOrWhiteSpace(package.hash))
            return package.hash.Trim();

        return TerracottaDefaults.TryGetKnownSha512(classifier, out var known) ? known : "";
    }

    /// <summary>
    /// 校验和：128 位十六进制按 SHA-512，64 位按 SHA-256（兼容旧配置里填的 SHA-256）。
    /// 没有期望值时也算出实际值，供安装后写回设置。
    /// </summary>
    private static async Task<string> ComputeHashAsync(string path, string expected, CancellationToken token)
    {
        var useSha512 = expected.Length == 128 || expected.Length == 0;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, true);
        var bytes = useSha512
            ? await SHA512.HashDataAsync(stream, token).ConfigureAwait(false)
            : await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(bytes);
    }

    private static string Sha512Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream));
    }

    /// <summary>把 .pkg.tar.gz 解开，找出可执行文件与运行库；若不是 gzip 则按裸可执行文件处理。</summary>
    private async Task ExtractAsync(string archive, string toolDir, string target, CancellationToken token)
    {
        await using var probe = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        var isGzip = probe.Length >= 2 && probe.ReadByte() == 0x1F && probe.ReadByte() == 0x8B;

        if (!isGzip)
        {
            File.Copy(archive, target, true);
            return;
        }

        var staging = Path.Combine(_paths.TempDirectory, "terracotta_extract_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var fs = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1 << 16, true))
            await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var reader = new TarReader(gz))
            {
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(false, token).ConfigureAwait(false)) is not null)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                        entry.DataStream is null) continue;
                    var name = Path.GetFileName(entry.Name);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var destination = Path.Combine(staging, name);
                    await using var output = new FileStream(destination, FileMode.Create,
                        FileAccess.Write, FileShare.None, 1 << 16, true);
                    await entry.DataStream.CopyToAsync(output, token).ConfigureAwait(false);
                }
            }

            var exe = Directory.EnumerateFiles(staging)
                .Where(p => Path.GetFileName(p).StartsWith("terracotta-", StringComparison.OrdinalIgnoreCase))
                .Where(p => OperatingSystem.IsWindows()
                    ? p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    : !p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();

            if (exe is null)
                throw new InvalidOperationException(L.T("String.Multi.Err.NoExecutableInPackage"));

            File.Copy(exe, target, true);
            CopyRuntimeDll(staging, toolDir);
        }
        finally
        {
            try { Directory.Delete(staging, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
