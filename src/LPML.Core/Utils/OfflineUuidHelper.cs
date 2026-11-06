using System.Text.RegularExpressions;

namespace LPML.Core.Utils;

/// <summary>
/// 离线 UUID 的自定义支持。
/// 格式 8-4-4-4-12，其中两个位置是协议固定的，不允许改：
///   第 15 个字符（第 3 段首位）= 版本号（离线固定为 '3'，即 name-based MD5）
///   第 20 个字符（第 4 段首位）= 变体位（8/9/a/b，RFC 4122）
/// 其余 30 个十六进制字符用户可以随意改。
/// </summary>
public static class OfflineUuidHelper
{
    /// <summary>第 3 段首位：UUID 版本（离线固定 v3）。</summary>
    public const char VersionChar = '3';

    /// <summary>第 4 段首位：变体位默认值。</summary>
    public const char VariantChar = 'a';

    private static readonly Regex HexPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);
    private static readonly Regex DashedPattern =
        new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    /// <summary>两个固定位在标准带连字符格式（36 字符）中的下标。</summary>
    public static int VersionIndex => 14;
    public static int VariantIndex => 19;

    /// <summary>是否为合法的 UUID 文本（32 位纯十六进制或 36 位带连字符）。</summary>
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        return HexPattern.IsMatch(value) || DashedPattern.IsMatch(value);
    }

    /// <summary>格式化为 8-4-4-4-12 并强制修正两个固定位。</summary>
    public static string Normalize(string text)
    {
        var hex = (text ?? "").Trim().Replace("-", "").ToLowerInvariant();
        if (hex.Length != 32) return text ?? "";

        var chars = hex.ToCharArray();
        // 下标换算：36 字符格式里的 14 / 19 → 32 字符纯 hex 里的 12 / 16
        if (chars.Length > 12) chars[12] = VersionChar;
        if (chars.Length > 16) chars[16] = VariantChar;

        var s = new string(chars);
        return $"{s[..8]}-{s[8..12]}-{s[12..16]}-{s[16..20]}-{s[20..32]}";
    }

    /// <summary>把固定位替换成占位符，用于界面提示哪些位置不可改。</summary>
    public static string MaskFixed(string uuid)
    {
        if (string.IsNullOrEmpty(uuid)) return "";
        var chars = uuid.Trim().ToCharArray();
        if (chars.Length == 36)
        {
            if (chars.Length > VersionIndex) chars[VersionIndex] = '_';
            if (chars.Length > VariantIndex) chars[VariantIndex] = '_';
        }
        return new string(chars);
    }
}
