using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LPML.Core.Config;
using LPML.Core.Download;
using LPML.Core.Utils;

namespace LPML.Core.Update;

/// <summary>
/// 在线更新：从固定 HTTPS 端点取 update.json 与签名 → Ed25519 验签 → 取本地化更新说明
/// → 下载两条线路之一的整包 → SHA-256 校验 → 解压到 cache\update-*。
/// 只负责「把新程序安全放到暂存目录」，拉起与替换由 App 层完成。
/// </summary>
public static class UpdateService
{
    public const string BaseUrl = "https://lpml.pages.dev/api/v1/lpml/";
    public const string ManifestName = "update.json";
    public const string StageFlagName = "lpml-update-stage.json";

    /// <summary>发布方给的 Ed25519 公钥（SubjectPublicKeyInfo / RFC 8410）。</summary>
    public const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MCowBQYDK2VwAyEAdGRsJ4pGeLcbQbZQvMMG9t4TMdKsFyj2FNyf5AnaCYM=\n" +
        "-----END PUBLIC KEY-----";

    /// <summary>SPKI 去掉 12 字节 DER 前缀后的裸 32 字节公钥。</summary>
    private static readonly byte[] PublicKeyRaw =
        Convert.FromHexString("74646C278A4678B71B41B650BCC306F6DE1331D2AC1728F614DC9FE409DA0983");

    /// <summary>
    /// 签名文件名按常见约定逐个探测：发布端只要用了其中任意一个就能验过。
    /// 站点若用别的名字，可在设置里指定（SignatureFileName）。
    /// </summary>
    private static readonly string[] SignatureCandidates =
    {
        "update.json.sig", "update.sig", "update.json.signature", "signature.sig", "update.json.minisig"
    };

    /// <summary>所有请求强制 HTTPS，不接受降级。</summary>
    private static string Https(string value, string what)
    {
        var v = (value ?? "").Trim();
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(what + " 使用了不安全的 HTTP 地址");
        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new UpdateException(what + " 不是有效的 HTTPS 地址");
        return v;
    }

    private static string ResolveUrl(string value, string what)
    {
        var v = (value ?? "").Trim();
        if (v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return Https(v, what);
        if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(what + " 使用了不安全的 HTTP 地址");
        if (v.StartsWith("./")) v = v[2..];
        if (Path.IsPathRooted(v) || v.Contains("..", StringComparison.Ordinal))
            throw new UpdateException(what + " 路径不合法");
        return Https(BaseUrl.TrimEnd('/') + "/" + v.TrimStart('/'), what);
    }

    /// <summary>拉取清单并验签；任何一步失败都不允许继续更新。</summary>
    public static async Task<UpdateCheckResult> CheckAsync(HttpService http, bool chinese,
        string? signatureFileName = null, CancellationToken token = default)
    {
        var manifestUrl = Https(BaseUrl + ManifestName, "更新清单");
        var raw = await http.GetBytesAsync(manifestUrl, token).ConfigureAwait(false);
        if (raw.Length == 0) throw new UpdateException("更新清单为空");

        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(signatureFileName)) names.Add(signatureFileName.Trim());
        names.AddRange(SignatureCandidates);

        var verified = false;
        var used = "";
        var failures = new List<string>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Contains("..", StringComparison.Ordinal) || name.Contains('\\')) continue;
            var signatureRaw = await http.TryGetBytesAsync(
                Https(BaseUrl + name, "签名文件"), token).ConfigureAwait(false);
            if (signatureRaw is null)
            {
                failures.Add(name + "：无法获取");
                continue;
            }

            var signature = ParseSignature(signatureRaw);
            if (signature is null) { failures.Add(name + "：签名格式无法识别"); continue; }
            if (Ed25519Verify.Verify(PublicKeyRaw, raw, signature))
            {
                verified = true;
                used = name;
                break;
            }
            failures.Add(name + "：验签不通过");
        }

        if (!verified)
            throw new UpdateException("更新清单未通过 Ed25519 验签，已中止更新。"
                + (failures.Count > 0 ? " 明细：" + string.Join("；", failures.Take(3)) : ""));

        var manifest = Json.Deserialize<UpdateManifest>(Encoding.UTF8.GetString(raw))
            ?? throw new UpdateException("更新清单无法解析");
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Num <= 0)
            throw new UpdateException("更新清单缺少 id 或 num");

        Log.Info("Update", $"验签通过（{used}）：id={manifest.Id} num={manifest.Num} important={manifest.Important}");

        var introduce = "";
        var introPath = chinese ? manifest.UpdateIntroduceZh : manifest.UpdateIntroduceEn;
        if (!string.IsNullOrWhiteSpace(introPath))
        {
            try
            {
                var introUrl = ResolveUrl(introPath, "更新说明");
                var text = await http.GetStringAsync(introUrl, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("main", out var main)
                    && main.ValueKind == JsonValueKind.String)
                    introduce = main.GetString() ?? "";
            }
            catch (Exception ex)
            {
                // 说明文档缺失只影响展示，不阻断安全更新
                Log.Warn("Update", "更新说明读取失败：" + ex.Message);
            }
        }

        return new UpdateCheckResult
        {
            Manifest = manifest,
            Raw = raw,
            SignatureName = used,
            Introduce = introduce
        };
    }

    /// <summary>签名兼容裸 64 字节、base64 文本、hex 文本、PEM 块、以及 {"signature": "..."} 壳。</summary>
    private static byte[]? ParseSignature(byte[] raw)
    {
        if (raw.Length == 64) return raw;
        string text;
        try { text = Encoding.UTF8.GetString(raw); }
        catch { return null; }
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            var body = string.Concat(trimmed.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("-----", StringComparison.Ordinal)));
            return DecodeSignature(body);
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var key in new[] { "signature", "sig", "base64", "hex" })
                {
                    if (!doc.RootElement.TryGetProperty(key, out var value)
                        || value.ValueKind != JsonValueKind.String) continue;
                    var parsed = DecodeSignature(value.GetString() ?? "");
                    if (parsed is not null) return parsed;
                }
            }
            catch (JsonException) { /* 不是 JSON 壳，按纯文本处理 */ }
        }

        return DecodeSignature(trimmed);
    }

    private static byte[]? DecodeSignature(string value)
    {
        var compact = Regex.Replace(value ?? "", @"\s+", "");
        if (compact.Length == 0) return null;
        if (compact.Length == 128 && compact.All(Uri.IsHexDigit))
        {
            try { return Convert.FromHexString(compact); }
            catch (FormatException) { }
        }
        try
        {
            var bytes = Convert.FromBase64String(compact);
            return bytes.Length == 64 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    private static bool IsProxyLine(string url)
        => url.Contains("gh-proxy", StringComparison.OrdinalIgnoreCase)
           || url.Contains("/proxy/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectLine(string url)
        => url.Contains("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析实际要下的包类型：0 自动跟随当前安装，1 强制带运行时，2 强制不带运行时。
    /// 界面提示也用这个方法，保证「显示要下什么」与「真的下什么」一致。
    /// </summary>
    public static bool ResolvePackageKind(UpdateSettings settings, bool runtimeBundled)
        => settings.PackageKind switch
        {
            1 => true,
            2 => false,
            _ => runtimeBundled
        };

    /// <summary>按「更新包类型 + 下载线路」排出 URL 顺序，并给出该包对应的 SHA-256。</summary>
    public static (List<string> Urls, string Sha256) PlanDownload(
        UpdateManifest manifest, UpdateSettings settings, bool runtimeBundled)
    {
        var withRuntime = ResolvePackageKind(settings, runtimeBundled);

        var urls = (withRuntime ? manifest.HaveRuntimeDown : manifest.NoRuntimeDown)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => Https(u, "更新包地址"))
            .ToList();
        var hash = withRuntime ? manifest.HaveRuntimeSha256 : manifest.NoRuntimeSha256;

        // 选中的一类没地址时，回退到另一类（并换成对应的哈希）
        if (urls.Count == 0)
        {
            urls = (withRuntime ? manifest.NoRuntimeDown : manifest.HaveRuntimeDown)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => Https(u, "更新包地址"))
                .ToList();
            hash = withRuntime ? manifest.NoRuntimeSha256 : manifest.HaveRuntimeSha256;
        }

        // 用户自选线路优先；其余线路仍保留作失败兜底，避免选错就完全下不了
        urls = settings.DownloadLine switch
        {
            1 => urls.OrderBy(u => IsDirectLine(u) ? 0 : 1).ToList(),  // GitHub 直连优先
            2 => urls.OrderBy(u => IsProxyLine(u) ? 0 : 1).ToList(),   // 代理优先
            _ => urls                                                   // 自动：按清单原顺序
        };

        return (urls, hash?.Trim() ?? "");
    }

    /// <summary>清空旧的暂存目录，建一个新的 cache\update-&lt;num&gt;-&lt;随机&gt;。</summary>
    public static string CreateStageDirectory(string cacheDirectory, int num)
    {
        Directory.CreateDirectory(cacheDirectory);
        try
        {
            foreach (var old in Directory.EnumerateDirectories(cacheDirectory, "update-*"))
            {
                try { Directory.Delete(old, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }

        var path = Path.Combine(cacheDirectory, "update-" + num + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>逐条线路下载 → SHA-256 → 解压，返回暂存包信息。</summary>
    public static async Task<UpdateStagePackage> PrepareStageAsync(HttpService http, UpdateManifest manifest,
        UpdateSettings settings, bool runtimeBundled, string cacheDirectory,
        IProgress<(long Received, long Total)>? progress = null, CancellationToken token = default)
    {
        var (urls, expected) = PlanDownload(manifest, settings, runtimeBundled);
        if (urls.Count == 0) throw new UpdateException("更新清单里没有可用的下载地址");
        if (expected.Length == 0) throw new UpdateException("更新清单里没有对应的 SHA-256");

        var stage = CreateStageDirectory(cacheDirectory, manifest.Num);
        var zipPath = Path.Combine(stage, "package.zip");
        var appDir = Path.Combine(stage, "app");
        var errors = new List<string>();

        foreach (var url in urls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Log.Info("Update", "下载更新包：" + url);
                await http.DownloadToFileAsync(url, zipPath, progress, token).ConfigureAwait(false);

                var actual = HashHelper.Sha256(zipPath);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("SHA-256 不符：" + url);
                    FileHelper.TryDelete(zipPath);
                    continue;
                }
                Log.Info("Update", "SHA-256 校验通过：" + actual);

                var source = ExtractPackage(zipPath, appDir);
                var exe = FindExecutable(source);
                if (exe is null)
                {
                    errors.Add("更新包里找不到启动器可执行文件：" + url);
                    TryDeleteDirectory(appDir);
                    continue;
                }
                return new UpdateStagePackage
                {
                    StageDirectory = stage,
                    SourceDirectory = source,
                    ExecutableName = exe
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                errors.Add(url + "：" + ex.Message);
                FileHelper.TryDelete(zipPath);
                TryDeleteDirectory(appDir);
            }
        }

        throw new UpdateException("所有下载线路均失败：" + string.Join("；", errors.Take(3)));
    }

    /// <summary>解压并防 zip slip；若包内只有一个顶层目录，把它提升为程序根。</summary>
    private static string ExtractPackage(string zipPath, string appDir)
    {
        Directory.CreateDirectory(appDir);
        var root = Path.GetFullPath(appDir).TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var segments = name.Split('/');
            if (name.StartsWith('/') || segments.Any(s => s == ".."))
                throw new UpdateException("更新包内含有非法路径：" + name);

            var destination = Path.GetFullPath(Path.Combine(root, name));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("更新包内路径越界：" + name);

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            entry.ExtractToFile(destination, true);
        }

        var files = Directory.EnumerateFiles(appDir).ToList();
        var dirs = Directory.EnumerateDirectories(appDir).ToList();
        if (files.Count == 0 && dirs.Count == 1 && FindExecutable(dirs[0]) is not null)
            return dirs[0];
        return appDir;
    }

    /// <summary>优先 LPML.exe，其次任意 exe。</summary>
    public static string? FindExecutable(string directory)
    {
        var preferred = Path.Combine(directory, "LPML.exe");
        if (File.Exists(preferred)) return "LPML.exe";
        return Directory.EnumerateFiles(directory, "*.exe")
            .Select(Path.GetFileName)
            .FirstOrDefault();
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
