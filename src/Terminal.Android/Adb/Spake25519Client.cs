using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Subsystem;

// Clean-room managed SPAKE25519 client — BoringSSL-compatible (the variant adbd uses).
// VERIFIED: Test-Spake2 confirms byte-for-byte key agreement with the native BoringSSL
// bridge on the S23 (PASS). M/N, scalar/cofactor handling, transcript, and point math all
// match crypto/curve25519/spake25519.cc. This is now the LIVE adb pairing path (AdbPairingClient),
// replacing the native bridge — no BoringSSL dependency required for pairing.
public class Spake25519Client
{
    private static readonly BigInteger p = BigInteger.Parse("57896044618658097711785492504343953926634992332820282019728792003956564819949");
    private static readonly BigInteger l = BigInteger.Parse("7237005577332262213973186563042994240857116359379907606001950938285454250989");
    private static readonly BigInteger d = Mod(BigInteger.Parse("-121665") * FieldInv(121666, p), p);

    private static BigInteger Mod(BigInteger x, BigInteger m) { BigInteger r = x % m; return r < 0 ? r + m : r; }
    private static BigInteger FieldInv(BigInteger a, BigInteger m) => BigInteger.ModPow(a, m - 2, m);

    private class PointExt
    {
        public BigInteger X, Y, Z, T;
        public PointExt(BigInteger x, BigInteger y, BigInteger z, BigInteger t) { X = x; Y = y; Z = z; T = t; }

        public static PointExt Identity() => new PointExt(0, 1, 1, 0);

        public PointExt Add(PointExt other)
        {
            BigInteger A = Mod((Y - X) * (other.Y - other.X), p);
            BigInteger B = Mod((Y + X) * (other.Y + other.X), p);
            BigInteger C = Mod(T * 2 * d * other.T, p);
            BigInteger D = Mod(Z * 2 * other.Z, p);

            BigInteger E = Mod(B - A, p);
            BigInteger F = Mod(D - C, p);
            BigInteger G = Mod(D + C, p);
            BigInteger H = Mod(B + A, p);

            return new PointExt(Mod(E * F, p), Mod(G * H, p), Mod(F * G, p), Mod(E * H, p));
        }

        public PointExt Negate() => new PointExt(Mod(-X, p), Y, Z, Mod(-T, p));

        // BigInteger is not constant-time. Acceptable for one-shot loopback adb pairing; not for network use.
        public PointExt Multiply(BigInteger scalar)
        {
            PointExt Q = Identity();
            PointExt P = this;
            byte[] bits = scalar.ToByteArray(isUnsigned: true, isBigEndian: false);
            for (int i = 0; i < 256; i++)
            {
                int byteIdx = i / 8;
                int bitIdx = i % 8;
                int bit = (byteIdx < bits.Length) ? ((bits[byteIdx] >> bitIdx) & 1) : 0;
                if (bit == 1) Q = Q.Add(P);
                P = P.Add(P);
            }
            return Q;
        }

        public bool IsSmallSubgroup()
        {
            PointExt check = this.Multiply(8);
            return check.X == 0 && check.Y == check.Z;
        }

        public byte[] Encode()
        {
            BigInteger invZ = FieldInv(Z, p);
            BigInteger x = Mod(X * invZ, p);
            BigInteger y = Mod(Y * invZ, p);
            byte[] bytes = new byte[32];
            byte[] yBytes = y.ToByteArray(isUnsigned: true, isBigEndian: false);
            Array.Copy(yBytes, bytes, Math.Min(yBytes.Length, 32));
            if (!x.IsEven) bytes[31] |= 0x80;
            return bytes;
        }
    }

    private static PointExt DecodePoint(byte[] bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("Ed25519 points must be 32 bytes");
        byte[] yBytes = new byte[32];
        Array.Copy(bytes, yBytes, 32);
        bool xIsOdd = (yBytes[31] & 0x80) != 0;
        yBytes[31] &= 0x7F;

        BigInteger y = new BigInteger(yBytes, isUnsigned: true, isBigEndian: false);
        if (y >= p) throw new CryptographicException("Non-canonical Y coordinate.");

        BigInteger y2 = Mod(y * y, p);
        BigInteger u = Mod(y2 - 1, p);
        BigInteger v = Mod(d * y2 + 1, p);

        BigInteger v3 = BigInteger.ModPow(v, 3, p);
        BigInteger v7 = Mod(v3 * BigInteger.ModPow(v, 4, p), p);
        BigInteger pow = BigInteger.ModPow(Mod(u * v7, p), (p - 5) / 8, p);
        BigInteger x = Mod(Mod(u * v3, p) * pow, p);

        BigInteger check = Mod(Mod(v * x, p) * x, p);
        if (check != u)
        {
            if (check != Mod(-u, p)) throw new CryptographicException("Point is not on the curve.");
            BigInteger I = BigInteger.ModPow(2, (p - 1) / 4, p);
            x = Mod(x * I, p);
        }

        if ((x.IsEven ? 0 : 1) != (xIsOdd ? 1 : 0)) x = p - x;
        return new PointExt(x, y, 1, Mod(x * y, p));
    }

    // FIXED: exact Ed25519 base point G (last byte 0x66 — verified l*G == Identity on desktop).
    private static PointExt G = DecodePoint(Convert.FromHexString("5866666666666666666666666666666666666666666666666666666666666666"));

    // M / N — BoringSSL's SPAKE2 constants, each = sha256(seed) (iteration 1) per the genpoint
    // procedure in spake25519.cc. Verified: Test-Spake2 key agreement passes byte-for-byte.
    private static PointExt M = DecodePoint(Convert.FromHexString("5ada7e4bf6ddd9adb6626d32131c6b5c51a1e347a3478f53cfcf441b88eed12e"));
    private static PointExt N = DecodePoint(Convert.FromHexString("10e3df0ae37d8e7a99b5fe74b44672103dbddcbd06af680d71329a11693bc778"));

    private BigInteger _x, _w;
    private byte[] _msgA, _pwdHash;

    public Spake25519Client(byte[] password)
    {
        _pwdHash = SHA512.HashData(password);

        BigInteger wRaw = new BigInteger(_pwdHash, isUnsigned: true, isBigEndian: false);
        _w = wRaw % l;
        BigInteger order = l;
        if ((_w & 1) == 1) { _w += order; }
        order *= 2;
        if ((_w & 2) == 2) { _w += order; }
        order *= 2;
        if ((_w & 4) == 4) { _w += order; }

        byte[] xRand = new byte[64];
        RandomNumberGenerator.Fill(xRand);
        BigInteger xRaw = new BigInteger(xRand, isUnsigned: true, isBigEndian: false);
        BigInteger xRed = xRaw % l;

        byte[] xBytes = new byte[32];
        byte[] tempX = xRed.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Copy(tempX, xBytes, Math.Min(32, tempX.Length));

        byte carry = 0;
        for (int i = 0; i < 32; i++)
        {
            byte nextCarry = (byte)(xBytes[i] >> 5);
            xBytes[i] = (byte)((xBytes[i] << 3) | carry);
            carry = nextCarry;
        }
        _x = new BigInteger(xBytes, isUnsigned: true, isBigEndian: false);

        _msgA = G.Multiply(_x).Add(M.Multiply(_w)).Encode();
    }

    public byte[] GetClientMessage() => _msgA;

    public byte[] ProcessServerMessage(byte[] msgB)
    {
        PointExt Y = DecodePoint(msgB);
        if (Y.IsSmallSubgroup()) throw new CryptographicException("Server sent weak subgroup point.");

        PointExt K = Y.Add(N.Multiply(_w).Negate()).Multiply(_x);
        if (K.X == 0 && K.Y == K.Z) throw new CryptographicException("Shared point collapsed to identity.");

        var ms = new MemoryStream();
        void writeLE(byte[] data)
        {
            ms.Write(BitConverter.GetBytes((ulong)data.Length));
            ms.Write(data);
        }

        writeLE(Encoding.UTF8.GetBytes("adb pair client\0"));
        writeLE(Encoding.UTF8.GetBytes("adb pair server\0"));
        writeLE(_msgA);
        writeLE(msgB);
        writeLE(K.Encode());
        writeLE(_pwdHash);

        return SHA512.HashData(ms.ToArray());
    }
}
