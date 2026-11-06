using System.Text;
using System.Security.Cryptography;

namespace LPML.Core.Utils;

public static class HashHelper
{
    /// <summary>计算文件的 SHA-256（大写十六进制）。</summary>
    public static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, false);
        var hash = sha.ComputeHash(fs);
        return ConvertToHex(hash);
    }

    /// <summary>计算字符串的 SHA-256（UTF-8，大写十六进制）。</summary>
    public static string Sha256String(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = sha.ComputeHash(bytes);
        return ConvertToHex(hash);
    }

    private static string ConvertToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) builder.Append(b.ToString("X2"));
        return builder.ToString();
    }

    public static string Sha1File(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static string Sha1Bytes(byte[] data)
        => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

    public static string Sha1String(string text)
        => Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>离线账号 UUID（v3，遵循 Mojang 的离线 UUID 生成规则）。</summary>
    public static string OfflineUuid(string playerName)
    {
        var seed = "OfflinePlayer:" + playerName;
        var bytes = System.Text.Encoding.UTF8.GetBytes(seed);
        var hash = MD5.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex.Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-");
    }

    public static string ShortUuid(string uuid) => uuid.Replace("-", "");

    /// <summary>把无横线的 UUID 还原为标准 8-4-4-4-12 形式。</summary>
    public static string FormatUuid(string uuid)
    {
        var s = uuid.Replace("-", "");
        if (s.Length != 32) return uuid;
        return s.Insert(8, "-").Insert(13, "-").Insert(18, "-").Insert(23, "-");
    }
}
