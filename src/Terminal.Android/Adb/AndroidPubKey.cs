using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Subsystem;

/// <summary>
/// Encodes an RSA public key in Android's adb_keys format, the format adbd stores during pairing
/// and checks a TLS client cert against on connect. Layout (from AOSP android_pubkey.c):
///   struct RSAPublicKey {
///       uint32 modulus_size_words;   // 256/4 = 64
///       uint32 n0inv;                // -1 / N[0] mod 2^32
///       uint8  modulus[256];         // little-endian
///       uint8  rr[256];              // R^2 mod N, little-endian (R = 2^2048)
///       uint32 exponent;             // 65537
///   }
/// Then: base64(struct) + " " + name
/// </summary>
public static class AndroidPubKey
{
    private const int ModulusSize = 256;       // 2048-bit
    private const int ModulusWords = ModulusSize / 4;

    public static string Encode(RSA rsa, string name)
    {
        RSAParameters p = rsa.ExportParameters(false);

        // N (modulus) as a positive BigInteger (RSAParameters are big-endian)
        BigInteger n = new BigInteger(p.Modulus!, isUnsigned: true, isBigEndian: true);
        uint exponent = (uint)new BigInteger(p.Exponent!, isUnsigned: true, isBigEndian: true);

        // n0inv = -1 / N[0] mod 2^32   (N[0] = low 32 bits of N)
        BigInteger r32 = BigInteger.One << 32;
        BigInteger n0 = n % r32;
        BigInteger n0inv = (r32 - ModInverse(n0, r32)) % r32;

        // rr = R^2 mod N, with R = 2^(ModulusSize*8) = 2^2048  ->  rr = 2^4096 mod N
        BigInteger rr = BigInteger.ModPow(2, ModulusSize * 8 * 2, n);

        byte[] buf = new byte[3 * 4 + 2 * ModulusSize]; // 12 + 512 = 524
        int off = 0;
        WriteU32(buf, ref off, ModulusWords);
        WriteU32(buf, ref off, n0inv > uint.MaxValue ? 0u : (uint)n0inv);
        WriteLe(buf, ref off, n, ModulusSize);
        WriteLe(buf, ref off, rr, ModulusSize);
        WriteU32(buf, ref off, exponent);

        return Convert.ToBase64String(buf) + " " + name;
    }

    private static void WriteU32(byte[] buf, ref int off, uint v)
    {
        buf[off++] = (byte)(v & 0xFF);
        buf[off++] = (byte)((v >> 8) & 0xFF);
        buf[off++] = (byte)((v >> 16) & 0xFF);
        buf[off++] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteLe(byte[] buf, ref int off, BigInteger v, int len)
    {
        byte[] le = v.ToByteArray(isUnsigned: true, isBigEndian: false); // little-endian
        for (int i = 0; i < len; i++)
            buf[off + i] = i < le.Length ? le[i] : (byte)0;
        off += len;
    }

    private static BigInteger ModInverse(BigInteger a, BigInteger m)
    {
        BigInteger t = 0, newT = 1, r = m, newR = ((a % m) + m) % m;
        while (newR != 0)
        {
            BigInteger q = r / newR;
            (t, newT) = (newT, t - q * newT);
            (r, newR) = (newR, r - q * newR);
        }
        if (t < 0) t += m;
        return t;
    }
}
