using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LPML.Core.Multiplayer;

/// <summary>
/// 陶瓦联机（Terracotta）邀请码。
///
/// 官方格式：U/NNNN-NNNN-SSSS-SSSS
///  - 字母表为 base-34：0-9、A-H、J-N、P-Z（排除易混的 I 与 O）
///  - 输入时 I 归一化为 1、O 归一化为 0
///  - 按小端序把 16 个字符的值读成整数，该整数应能被 7 整除（错误检测）
///
/// 采用与 HMCL / PCL CE / FCL 相同的码制，因此双方可以直接互连。
/// 参考：Terracotta 官方文档「内部协议」与「HTTP API」（v0.4.2）。
/// </summary>
public static class TerracottaRoomCode
{
    /// <summary>base-34 字母表，顺序即数值顺序。</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public const string Prefix = "U/";

    /// <summary>
    /// 把用户输入的邀请码归一化为 U/XXXX-XXXX-XXXX-XXXX。
    /// 格式不对或含非法字符时返回空字符串。
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";

        var raw = input.Trim().ToUpperInvariant()
            .Replace('I', '1')
            .Replace('O', '0');

        var buffer = new StringBuilder(24);
        foreach (var c in raw)
            if (char.IsAsciiLetterOrDigit(c))
                buffer.Append(c);

        var digits = buffer.ToString();
        // 允许直接粘贴 "U/XXXX..."，也允许只贴 16 位本体
        if (digits.Length > 0 && digits[0] == 'U') digits = digits[1..];
        if (digits.Length != 16) return "";

        foreach (var c in digits)
            if (ValueOf(c) < 0)
                return "";

        return $"{Prefix}{digits[..4]}-{digits[4..8]}-{digits[8..12]}-{digits[12..16]}";
    }

    /// <summary>格式是否合法（不校验校验位）。校验位单独判定，避免误拒第三方启动器生成的码。</summary>
    public static bool IsWellFormed(string? input) => Normalize(input).Length > 0;

    /// <summary>校验位是否成立。仅用于提示，不作为拒绝加入的依据——最终由陶瓦核心判定。</summary>
    public static bool HasValidChecksum(string? input)
    {
        var normalized = Normalize(input);
        if (normalized.Length == 0) return false;

        var digits = normalized[Prefix.Length..].Replace("-", "").AsSpan();
        BigInteger value = 0;
        BigInteger place = 1;
        for (var i = 0; i < digits.Length; i++)
        {
            value += ValueOf(digits[i]) * place;
            place *= Alphabet.Length;
        }
        return value % 7 == 0;
    }

    /// <summary>生成一个满足官方校验规则的邀请码（用于「固定房间码」）。</summary>
    public static string Generate()
    {
        var chars = new char[16];
        for (var i = 0; i < chars.Length - 1; i++)
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];

        // 调整最高位（小端序的第 16 位），使整体能被 7 整除。
        // 34 与 7 互质，因此 0..33 中必然存在解。
        BigInteger low = 0;
        BigInteger place = 1;
        for (var i = 0; i < chars.Length - 1; i++)
        {
            low += ValueOf(chars[i]) * place;
            place *= Alphabet.Length;
        }

        var unit = place;
        for (var candidate = 0; candidate < Alphabet.Length; candidate++)
        {
            if ((low + candidate * unit) % 7 != 0) continue;
            chars[^1] = Alphabet[candidate];
            break;
        }
        if (chars[^1] == '\0') chars[^1] = Alphabet[0];

        var digits = new string(chars);
        return $"{Prefix}{digits[..4]}-{digits[4..8]}-{digits[8..12]}-{digits[12..16]}";
    }

    private static int ValueOf(char c) => Alphabet.IndexOf(c);
}

/// <summary>当前平台对应的陶瓦发行包 classifier，与 HMCL terracotta.json 的键名一致。</summary>
public static class TerracottaPlatform
{
    /// <summary>官方发行包的 web server 固定监听 127.0.0.1:8000。</summary>
    public const int DefaultApiPort = 8000;

    public static string Classifier()
    {
        // 用字符串比较而不是枚举常量，避免不同 .NET 版本枚举成员差异影响编译。
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var isArm64 = arch.Contains("Arm64", StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
            return isArm64 ? "windows-arm64" : "windows-x86_64";
        if (OperatingSystem.IsMacOS())
            return isArm64 ? "macos-arm64" : "macos-x86_64";
        if (OperatingSystem.IsFreeBSD())
            return "freebsd-x86_64";

        if (arch.Contains("RiscV", StringComparison.OrdinalIgnoreCase)) return "linux-riscv64";
        if (arch.Contains("LoongArch", StringComparison.OrdinalIgnoreCase)) return "linux-loongarch64";
        return isArm64 ? "linux-arm64" : "linux-x86_64";
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>陶瓦数据目录：macOS 为 ~/terracotta，其余为系统临时目录下的 terracotta。</summary>
    public static string DataDirectory()
        => OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "terracotta")
            : Path.Combine(Path.GetTempPath(), "terracotta");

    /// <summary>锁文件：2 字节大端端口号，由陶瓦写入，多个进程共享同一端口。</summary>
    public static string LockFilePath => Path.Combine(DataDirectory(), "terracotta.lock");

    /// <summary>发行包内的可执行文件名（HMCL terracotta.json 的 files 键名）。</summary>
    public static string ExecutableName(string version, string classifier, bool windows)
        => windows
            ? $"terracotta-{version}-{classifier}.exe"
            : $"terracotta-{version}-{classifier}";
}

/// <summary>
/// 默认路线：官方 / 国内镜像下载地址与版本。
/// URL 模板与 SHA-512 校验口径与 HMCL 的 terracotta.json 保持一致，便于互通与复用镜像。
/// 用户仍可在「配置」里覆盖下载地址、版本与校验和。
/// </summary>
public static class TerracottaDefaults
{
    public const string Version = "0.4.2";

    /// <summary>启动器标识，作为 vendor 传给陶瓦，显示在房间成员列表里。</summary>
    public const string Vendor = "LPML/1.0";

    /// <summary>版权标注：AGPL 例外条款要求在通过 HTTP API 集成时于用户界面明显处标识。</summary>
    public const string Copyright =
        "Terracotta | 陶瓦联机 © Burning_TNT 及贡献者 · AGPL-3.0-or-later（含例外条款）；基于 EasyTier。";

    public const string CopyrightEn =
        "Terracotta © Burning_TNT and contributors · AGPL-3.0-or-later (with exception); built on EasyTier.";

    public const string ProjectUrl = "https://burningtnt.github.io/Terracotta/";
    public const string EasyTierUrl = "https://easytier.cn/";

    /// <summary>发行包 URL 模板。{version} 与 {classifier} 由代码替换；国内源在前。</summary>
    public static readonly string[] DownloadTemplates =
    {
        "https://gitee.com/burningtnt/Terracotta/releases/download/v{version}/terracotta-{version}-{classifier}-pkg.tar.gz",
        "https://cnb.cool/HMCL-Terracotta/Terracotta/-/releases/download/v{version}/terracotta-{version}-{classifier}-pkg.tar.gz",
        "https://alist.8mi.tech/d/mirror/HMCL-Terracotta/Auto/v{version}/terracotta-{version}-{classifier}-pkg.tar.gz",
        "https://github.com/burningtnt/Terracotta/releases/download/v{version}/terracotta-{version}-{classifier}-pkg.tar.gz"
    };

    /// <summary>可选的在线元数据（HMCL 同款 terracotta.json）。拉不到就回退内置常量，不影响默认路线。</summary>
    public static readonly string[] MetadataUrls =
    {
        "https://raw.githubusercontent.com/HMCL-dev/HMCL/main/HMCL/src/main/resources/assets/terracotta.json",
        "https://gitee.com/burningtnt/Terracotta/raw/main/terracotta.json"
    };

    /// <summary>
    /// 已核实的包级 SHA-512（来自 HMCL terracotta.json）。
    /// 未列出的平台不硬编码哈希：改由在线元数据提供，取不到则跳过校验并记日志。
    /// </summary>
    public static readonly Dictionary<string, string> KnownPackageSha512 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windows-x86_64"] =
            "6a98f524d4f00373696517306af8aa50d01d55ce4eadb27e9e4bc2f882707a0b5f20d5d4c33371d1459dcf5bf144ffed9beb414202d9ccf32b11dbbfcf19d650"
    };

    /// <summary>
    /// 展开下载地址模板。
    /// HMCL 的 terracotta.json 用 ${version} / ${classifier}（带 $），
    /// 内置模板与用户自定义地址用 {version} / {classifier}（不带 $）。
    /// 必须先替换带 $ 的形式，否则 ${version} 会被替换成 $0.4.2，留下一个多余的 $。
    /// </summary>
    public static string BuildUrl(string template, string version, string classifier)
        => template
            .Replace("${version}", version, StringComparison.OrdinalIgnoreCase)
            .Replace("${classifier}", classifier, StringComparison.OrdinalIgnoreCase)
            .Replace("{version}", version, StringComparison.OrdinalIgnoreCase)
            .Replace("{classifier}", classifier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 规范版本号：在线元数据若返回 v0.4.2，去掉前缀 v，
    /// 否则会拼出 vv0.4.2 这样的地址（发行包文件名里不含 v）。
    /// </summary>
    public static string NormalizeVersion(string? version)
    {
        var s = (version ?? "").Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsAsciiDigit(s[1]))
            s = s[1..];
        return s;
    }

    public static bool TryGetKnownSha512(string classifier, out string hash)
        => KnownPackageSha512.TryGetValue(classifier, out hash!);
}

/// <summary>HMCL terracotta.json 的精简模型（只取用得到的字段）。</summary>
public sealed class TerracottaMetadata
{
    public string version_latest { get; set; } = "";
    public List<string> downloads { get; set; } = new();
    public List<string> downloads_CN { get; set; } = new();
    public Dictionary<string, TerracottaMetadataPackage> packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按「国内优先」合并下载模板。</summary>
    public IEnumerable<string> Templates()
    {
        foreach (var url in downloads_CN ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(url)) yield return url;
        foreach (var url in downloads ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(url)) yield return url;
    }
}

public sealed class TerracottaMetadataPackage
{
    public string hash { get; set; } = "";
    public Dictionary<string, string> files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>GET /state 返回的房间状态。</summary>
public enum TerracottaStateKind
{
    Unknown,
    Waiting,
    HostScanning,
    HostStarting,
    HostOk,
    GuestConnecting,
    GuestStarting,
    GuestOk,
    Exception,
    /// <summary>本地状态：正在拉起陶瓦进程并等待本地服务就绪（不由 /state 上报）。</summary>
    CoreStarting
}

/// <summary>一次 /state 轮询的快照。</summary>
public sealed record TerracottaStateSnapshot(
    TerracottaStateKind Kind,
    string RawState,
    long Index,
    string? RoomCode,
    string? Url,
    IReadOnlyList<TerracottaProfile> Profiles,
    string? Error);

public sealed record TerracottaProfile(string MachineId, string Name, string Vendor, string Kind);

public static class TerracottaStateParser
{
    public static TerracottaStateSnapshot Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var raw = GetString(root, "state") ?? "";
            var index = GetLong(root, "index");
            var room = GetString(root, "room");
            var url = GetString(root, "url");
            if (url is null && root.TryGetProperty("url", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) { url = item.GetString(); break; }
                }
            }

            var error = GetString(root, "error") ?? GetString(root, "message");
            var profiles = new List<TerracottaProfile>();
            if (root.TryGetProperty("profiles", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in list.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    profiles.Add(new TerracottaProfile(
                        GetString(p, "machine_id") ?? "",
                        GetString(p, "name") ?? "",
                        GetString(p, "vendor") ?? "",
                        GetString(p, "kind") ?? ""));
                }
            }

            return new TerracottaStateSnapshot(ParseKind(raw), raw, index, room, url, profiles, error);
        }
        catch (JsonException)
        {
            return new TerracottaStateSnapshot(TerracottaStateKind.Unknown, "", 0, null, null,
                Array.Empty<TerracottaProfile>(), json);
        }
    }

    private static TerracottaStateKind ParseKind(string raw) => raw switch
    {
        // 官方 v0.4.2 的 /state 实际返回这四个值；以前只认带连字符的分支，
        // 于是 hosting / guesting 全被解析成 Unknown，房间码和地址永远读不到。
        "waiting" => TerracottaStateKind.Waiting,
        "scanning" => TerracottaStateKind.HostScanning,
        "hosting" => TerracottaStateKind.HostOk,
        "guesting" => TerracottaStateKind.GuestOk,
        // 兼容部分第三方分支/旧版本的多阶段状态名
        "host-scanning" => TerracottaStateKind.HostScanning,
        "host-starting" => TerracottaStateKind.HostStarting,
        "host-ok" => TerracottaStateKind.HostOk,
        "guest-connecting" => TerracottaStateKind.GuestConnecting,
        "guest-starting" => TerracottaStateKind.GuestStarting,
        "guest-ok" => TerracottaStateKind.GuestOk,
        "exception" => TerracottaStateKind.Exception,
        _ => TerracottaStateKind.Unknown
    };

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return 0;
    }
}
