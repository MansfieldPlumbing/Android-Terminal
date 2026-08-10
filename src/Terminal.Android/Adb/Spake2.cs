using System;
using System.Security.Cryptography;

namespace Subsystem;

// adb pairing key derivation. The SPAKE2 exchange itself is now pure-managed
// (Spake25519Client, verified byte-for-byte against BoringSSL); this holds only the
// HKDF step that turns the SPAKE shared secret into the AES-128-GCM session key.
// No native dependency — BoringSSL is gone from the pairing path.
public static class Spake2
{
    /// <summary>
    /// HKDF-derives the AES-128-GCM key from the SPAKE2 shared secret.
    /// Matches adb's "adb pairing_auth aes-128-gcm key" info, SHA-256, no salt.
    /// </summary>
    public static byte[] DeriveAesKey(byte[] spakeKeyMaterial)
    {
        var info = System.Text.Encoding.UTF8.GetBytes("adb pairing_auth aes-128-gcm key");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, spakeKeyMaterial, 16, Array.Empty<byte>(), info);
    }
}
