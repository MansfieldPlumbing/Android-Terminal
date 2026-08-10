using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Android.Runtime;
using Java.Interop;

namespace Subsystem;

public class AdbPairingClient : IDisposable
{
    private const byte CurrentKeyHeaderVersion = 1;
    private const int MaxPeerInfoSize = 8192;
    private const int ExportedKeySize = 64;

    private readonly string _host;
    private readonly int _port;
    private readonly string _pairCode;
    private readonly RSA _rsaKey;

    private Java.Net.Socket? _javaSocket;
    private Javax.Net.Ssl.SSLSocket? _sslSocket;
    private Stream? _stream;
    
    private byte[] _aesKey = null!;
    private ulong _encSequence = 0;
    private ulong _decSequence = 0;

    class TrustAllManager : Java.Lang.Object, Javax.Net.Ssl.IX509TrustManager
    {
        public void CheckClientTrusted(Java.Security.Cert.X509Certificate[]? chain, string? authType) { }
        public void CheckServerTrusted(Java.Security.Cert.X509Certificate[]? chain, string? authType) { }
        public Java.Security.Cert.X509Certificate[] GetAcceptedIssuers() => Array.Empty<Java.Security.Cert.X509Certificate>();
    }

    public AdbPairingClient(string host, int port, string pairCode, RSA rsaKey)
    {
        _host = host;
        _port = port;
        _pairCode = pairCode;
        _rsaKey = rsaKey;
    }

    public async Task<bool> PairAsync()
    {
        try
        {
            // Create a self-signed cert and package it into a PKCS12 KeyStore for Java
            var certReq = new CertificateRequest("CN=subsystem@localhost", _rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var selfSignedCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            
            var pkcs12 = selfSignedCert.Export(X509ContentType.Pkcs12, "password");
            using var ms = new MemoryStream(pkcs12);
            var javaKeyStore = Java.Security.KeyStore.GetInstance("PKCS12");
            javaKeyStore.Load(ms, "password".ToCharArray());

            var kmf = Javax.Net.Ssl.KeyManagerFactory.GetInstance(Javax.Net.Ssl.KeyManagerFactory.DefaultAlgorithm);
            kmf.Init(javaKeyStore, "password".ToCharArray());

            var trustAllCerts = new Javax.Net.Ssl.ITrustManager[] { new TrustAllManager() };

            var sslContext = Javax.Net.Ssl.SSLContext.GetInstance("TLSv1.3");
            sslContext.Init(kmf.GetKeyManagers(), trustAllCerts, new Java.Security.SecureRandom());

            _javaSocket = new Java.Net.Socket(_host, _port);
            _sslSocket = (Javax.Net.Ssl.SSLSocket)sslContext.SocketFactory!.CreateSocket(_javaSocket, _host, _port, true)!;
            _sslSocket.StartHandshake();

            _stream = _sslSocket.InputStream;

            // Export Keying Material via JNI reflection on Conscrypt
            IntPtr conscryptClass = JNIEnv.FindClass("com/android/org/conscrypt/Conscrypt");
            IntPtr exportMethod = JNIEnv.GetStaticMethodID(conscryptClass, "exportKeyingMaterial", "(Ljavax/net/ssl/SSLSocket;Ljava/lang/String;[BI)[B");
            
            IntPtr labelStr = JNIEnv.NewString("adb-label\0");
            IntPtr resultBytes = JNIEnv.CallStaticObjectMethod(conscryptClass, exportMethod, new JValue(_sslSocket), new JValue(labelStr), new JValue(IntPtr.Zero), new JValue(ExportedKeySize));
            byte[] keyMaterial = JNIEnv.GetArray<byte>(resultBytes);

            byte[] pairCodeBytes = Encoding.UTF8.GetBytes(_pairCode);
            byte[] password = new byte[pairCodeBytes.Length + keyMaterial.Length];
            Buffer.BlockCopy(pairCodeBytes, 0, password, 0, pairCodeBytes.Length);
            Buffer.BlockCopy(keyMaterial, 0, password, pairCodeBytes.Length, keyMaterial.Length);

            // Home-rolled managed SPAKE25519 — verified byte-for-byte against BoringSSL by
            // Test-Spake2 (managed client <-> native bridge key agreement, PASS on S23). Replaces
            // the native SPAKE2 bridge so this path carries no BoringSSL dependency. DeriveAesKey
            // below is pure-managed HKDF and stays.
            var spake = new Spake25519Client(password);
            byte[] ourSpakeMsg = spake.GetClientMessage();

            await WritePacketAsync(0, ourSpakeMsg);

            var (type, theirSpakeMsg) = await ReadPacketAsync();
            if (type != 0) throw new Exception("Expected SPAKE2_MSG");

            byte[] spakeKeyMaterial = spake.ProcessServerMessage(theirSpakeMsg);
            _aesKey = Spake2.DeriveAesKey(spakeKeyMaterial);

            byte[] peerInfoBuf = new byte[MaxPeerInfoSize];
            peerInfoBuf[0] = 0; // ADB_RSA_PUB_KEY
            // adbd expects the Android adb_keys format ("base64(struct) name"), NOT
            // SubjectPublicKeyInfo. Wrong format => garbage keystore entry + failed connect.
            byte[] pubKeyBytes = Encoding.UTF8.GetBytes(AndroidPubKey.Encode(_rsaKey, "Subsystem") + "\0");
            Buffer.BlockCopy(pubKeyBytes, 0, peerInfoBuf, 1, Math.Min(pubKeyBytes.Length, MaxPeerInfoSize - 1));

            byte[] encPeerInfo = Encrypt(peerInfoBuf);
            await WritePacketAsync(1, encPeerInfo);

            var (peerType, theirEncPeerInfo) = await ReadPacketAsync();
            if (peerType != 1) throw new Exception("Expected PEER_INFO");

            byte[] theirPeerInfo = Decrypt(theirEncPeerInfo);
            return true;
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("SubsystemDebug", $"Pairing exception: {ex}");
            throw;
        }
    }

    private async Task WritePacketAsync(byte type, byte[] payload)
    {
        byte[] header = new byte[6];
        header[0] = CurrentKeyHeaderVersion;
        header[1] = type;
        
        byte[] sizeBytes = BitConverter.GetBytes(payload.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(sizeBytes);
        Buffer.BlockCopy(sizeBytes, 0, header, 2, 4);

        var os = _sslSocket!.OutputStream!;
        os.Write(header, 0, 6);
        os.Write(payload, 0, payload.Length);
        os.Flush();
        await Task.CompletedTask;
    }

    private async Task<(byte Type, byte[] Payload)> ReadPacketAsync()
    {
        byte[] header = new byte[6];
        await ReadExactlyAsync(_stream!, header, 6);

        if (header[0] != CurrentKeyHeaderVersion) throw new Exception("Version mismatch");
        byte type = header[1];

        byte[] sizeBytes = new byte[4];
        Buffer.BlockCopy(header, 2, sizeBytes, 0, 4);
        if (BitConverter.IsLittleEndian) Array.Reverse(sizeBytes);
        int payloadSize = BitConverter.ToInt32(sizeBytes, 0);

        byte[] payload = new byte[payloadSize];
        await ReadExactlyAsync(_stream!, payload, payloadSize);

        return (type, payload);
    }

    private async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, totalRead, count - totalRead);
            if (read == 0) throw new EndOfStreamException();
            totalRead += read;
        }
    }

    private byte[] Encrypt(byte[] input)
    {
        using var aes = new AesGcm(_aesKey, 16);
        byte[] nonce = new byte[12];
        BitConverter.TryWriteBytes(nonce, _encSequence++);
        
        byte[] ciphertext = new byte[input.Length];
        byte[] tag = new byte[16];
        
        aes.Encrypt(nonce, input, ciphertext, tag);
        
        byte[] result = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, tag.Length);
        return result;
    }

    private byte[] Decrypt(byte[] input)
    {
        using var aes = new AesGcm(_aesKey, 16);
        byte[] nonce = new byte[12];
        BitConverter.TryWriteBytes(nonce, _decSequence++);
        
        int ciphertextLen = input.Length - 16;
        byte[] ciphertext = new byte[ciphertextLen];
        byte[] tag = new byte[16];
        
        Buffer.BlockCopy(input, 0, ciphertext, 0, ciphertextLen);
        Buffer.BlockCopy(input, ciphertextLen, tag, 0, 16);

        byte[] plaintext = new byte[ciphertextLen];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _sslSocket?.Close();
        _javaSocket?.Close();
    }
}
