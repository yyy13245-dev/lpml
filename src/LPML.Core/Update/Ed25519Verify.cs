using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace LPML.Core.Update;

/// <summary>
/// Ed25519 验签（RFC 8032）。纯托管实现，不引第三方包：
/// .NET 10 的 System.Security.Cryptography 没有内置 Ed25519 类型，
/// 而更新检查属于安全边界，不能靠 NuGet 还原是否成功来决定能不能验签。
///
/// 验的是「原始消息字节」，不是重新序列化后的 JSON —— 签名覆盖的是发布时的
/// 那一份 update.json，任何字节级改动都会让验签失败。
/// </summary>
public static class Ed25519Verify
{
    private static readonly BigInteger Q = (BigInteger.One << 255) - 19;
    private static readonly BigInteger L = (BigInteger.One << 252)
        + BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);
    private static readonly BigInteger D = Mod(-121665 * ModInv(121666));
    private static readonly BigInteger I = BigInteger.ModPow(2, (Q - 1) / 4, Q);
    private static readonly BigInteger Mask255 = (BigInteger.One << 255) - 1;

    private static readonly Point BasePoint = MakeBasePoint();

    private readonly record struct Point(BigInteger X, BigInteger Y);

    private static BigInteger Mod(BigInteger value)
    {
        var result = value % Q;
        return result.Sign < 0 ? result + Q : result;
    }

    private static BigInteger ModInv(BigInteger value) => BigInteger.ModPow(Mod(value), Q - 2, Q);

    /// <summary>由 y 求 x（固定取偶数根）；点不在曲线上时返回 false。</summary>
    private static bool TryXRecover(BigInteger y, out BigInteger x)
    {
        x = BigInteger.Zero;
        var denominator = Mod(D * y * y + 1);
        if (denominator.IsZero) return false;
        var xx = Mod((y * y - 1) * ModInv(denominator));

        var candidate = BigInteger.ModPow(xx, (Q + 3) / 8, Q);
        if (Mod(candidate * candidate - xx) != 0)
        {
            candidate = Mod(candidate * I);
            if (Mod(candidate * candidate - xx) != 0) return false;
        }
        if (!candidate.IsEven) candidate = Q - candidate;
        x = candidate;
        return true;
    }

    private static Point MakeBasePoint()
    {
        var by = Mod(4 * ModInv(5));
        TryXRecover(by, out var bx);
        return new Point(bx, by);
    }

    /// <summary>Edwards 曲线加法（a = -1）。</summary>
    private static Point Add(Point p, Point q)
    {
        var k = Mod(D * p.X * q.X * p.Y * q.Y);
        var x = Mod((p.X * q.Y + q.X * p.Y) * ModInv(1 + k));
        var y = Mod((p.Y * q.Y + p.X * q.X) * ModInv(1 - k));
        return new Point(x, y);
    }

    /// <summary>标量乘：逐位 double-and-add，避免递归深度。</summary>
    private static Point Multiply(Point point, BigInteger scalar)
    {
        var result = new Point(BigInteger.Zero, BigInteger.One);
        var current = point;
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, current);
            current = Add(current, current);
            scalar >>= 1;
        }
        return result;
    }

    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = default;
        if (encoded.Length != 32) return false;
        var value = new BigInteger(encoded, isUnsigned: true, isBigEndian: false);
        var y = value & Mask255;
        if (y >= Q) return false;
        var sign = (int)(value >> 255);
        if (!TryXRecover(y, out var x)) return false;
        if ((int)(x & BigInteger.One) != sign) x = Q - x;
        point = new Point(x, y);
        return true;
    }

    private static byte[] EncodePoint(Point point)
    {
        var value = point.Y | ((point.X & BigInteger.One) << 255);
        var bytes = new byte[32];
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (raw.Length > 32) return bytes;
        raw.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// 验证签名。publicKey 是裸 32 字节公钥（SubjectPublicKeyInfo 去掉 12 字节 DER 前缀），
    /// signature 是裸 64 字节（R[32] + S[32]）。
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        try
        {
            if (!TryDecodePoint(publicKey, out var a)) return false;
            if (!TryDecodePoint(signature[..32], out var r)) return false;

            var s = new BigInteger(signature[32..], isUnsigned: true, isBigEndian: false);
            if (s >= L) return false;

            var hashInput = new byte[64 + message.Length];
            signature[..32].CopyTo(hashInput.AsSpan(0, 32));
            publicKey.CopyTo(hashInput.AsSpan(32, 32));
            message.CopyTo(hashInput.AsSpan(64));
            var h = new BigInteger(SHA512.HashData(hashInput), isUnsigned: true, isBigEndian: false) % L;

            // [S]B == R + [h]A
            var left = EncodePoint(Multiply(BasePoint, s));
            var right = EncodePoint(Add(r, Multiply(a, h)));
            return CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
