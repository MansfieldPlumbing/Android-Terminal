using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Subsystem;

// Conscrypt binding for the ADB transport seam (IAdbTransport) — the Android head's socket+TLS. Plaintext
// Java socket + Conscrypt SSLSocket for the StartTLS upgrade: .NET-on-Android won't reliably put a
// self-signed client cert on the wire for TLS 1.3 (adbd kicks with PEER_DID_NOT_RETURN_A_CERTIFICATE);
// Conscrypt does — the same TLS path the working pairing client uses. Named for the MECHANISM (Conscrypt),
// not the platform (SS012). Java.* interop, never compiled into the Windows head; the Windows binding is
// SslStreamAdbTransport (System.Net.Security.SslStream).
public sealed class ConscryptAdbTransport : IAdbTransport
{
    private Java.Net.Socket? _socket;
    private Javax.Net.Ssl.SSLSocket? _sslSocket;

    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _socket = new Java.Net.Socket(host, port);
        AdbLog.Debug("adb", $"TCP connected to {host}:{port}");
        return new DuplexStream(_socket.InputStream!, _socket.OutputStream!);
    }

    public async Task<Stream> UpgradeToTlsAsync(RSA clientKey, string host, int port, CancellationToken ct = default)
    {
        if (_socket == null) throw new AdbException("transport not connected (call ConnectAsync first)");

        // The client cert carries clientKey; adbd matches its public key against the paired keystore.
        var certReq = new CertificateRequest("CN=Subsystem", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var clientCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pkcs12 = clientCert.Export(X509ContentType.Pkcs12, "password");

        string alias;
        Javax.Net.Ssl.IX509KeyManager baseKm;
        using (var ms = new MemoryStream(pkcs12))
        {
            var keyStore = Java.Security.KeyStore.GetInstance("PKCS12");
            keyStore.Load(ms, "password".ToCharArray());
            var kmf = Javax.Net.Ssl.KeyManagerFactory.GetInstance(Javax.Net.Ssl.KeyManagerFactory.DefaultAlgorithm!);
            kmf.Init(keyStore, "password".ToCharArray());

            alias = "";
            var aliasEnum = keyStore.Aliases();
            if (aliasEnum != null && aliasEnum.HasMoreElements) alias = aliasEnum.NextElement()!.ToString()!;
            baseKm = (Javax.Net.Ssl.IX509KeyManager)kmf.GetKeyManagers()![0];
            AdbLog.Debug("adb", $"client cert alias='{alias}'");
        }

        var sslContext = Javax.Net.Ssl.SSLContext.GetInstance("TLSv1.3");
        sslContext.Init(
            new Javax.Net.Ssl.IKeyManager[] { new ForcingKeyManager(baseKm, alias) },
            new Javax.Net.Ssl.ITrustManager[] { new TrustAllManager() },
            new Java.Security.SecureRandom());

        // Layer an SSLSocket over the already-connected plaintext socket (autoClose=true).
        _sslSocket = (Javax.Net.Ssl.SSLSocket)sslContext.SocketFactory!.CreateSocket(_socket, host, port, true)!;
        _sslSocket.UseClientMode = true;
        _sslSocket.StartHandshake();
        AdbLog.Debug("adb", $"TLS OK (Conscrypt): {_sslSocket.Session?.Protocol} {_sslSocket.Session?.CipherSuite}");

        return new DuplexStream(_sslSocket.InputStream!, _sslSocket.OutputStream!);
    }

    public void Dispose()
    {
        try { _sslSocket?.Close(); } catch (Exception ex) { AdbLog.Debug("adb", "ssl close: " + ex.Message); }
        try { _socket?.Close(); }    catch (Exception ex) { AdbLog.Debug("adb", "socket close: " + ex.Message); }
    }

    // Accept any server cert: adbd's TLS cert is self-signed/ephemeral; authentication is by the client
    // cert's key against the paired keystore, not by validating the server chain.
    private sealed class TrustAllManager : Java.Lang.Object, Javax.Net.Ssl.IX509TrustManager
    {
        public void CheckClientTrusted(Java.Security.Cert.X509Certificate[]? chain, string? authType) { }
        public void CheckServerTrusted(Java.Security.Cert.X509Certificate[]? chain, string? authType) { }
        public Java.Security.Cert.X509Certificate[] GetAcceptedIssuers() => Array.Empty<Java.Security.Cert.X509Certificate>();
    }

    // Forces our client cert: chooseClientAlias always returns our alias regardless of the server's
    // acceptable-issuer list (adbd doesn't list our self-signed issuer, so the default KeyManager would
    // return null and send nothing). cert chain + private key delegate to the real KeyManager.
    private sealed class ForcingKeyManager : Java.Lang.Object, Javax.Net.Ssl.IX509KeyManager
    {
        private readonly Javax.Net.Ssl.IX509KeyManager _inner;
        private readonly string _alias;
        public ForcingKeyManager(Javax.Net.Ssl.IX509KeyManager inner, string alias) { _inner = inner; _alias = alias; }
        public string? ChooseClientAlias(string[]? keyType, Java.Security.IPrincipal[]? issuers, Java.Net.Socket? socket) => _alias;
        public string? ChooseServerAlias(string? keyType, Java.Security.IPrincipal[]? issuers, Java.Net.Socket? socket) => _inner.ChooseServerAlias(keyType, issuers, socket);
        public Java.Security.Cert.X509Certificate[]? GetCertificateChain(string? alias) => _inner.GetCertificateChain(_alias);
        public string[]? GetClientAliases(string? keyType, Java.Security.IPrincipal[]? issuers) => _inner.GetClientAliases(keyType, issuers);
        public Java.Security.IPrivateKey? GetPrivateKey(string? alias) => _inner.GetPrivateKey(_alias);
        public string[]? GetServerAliases(string? keyType, Java.Security.IPrincipal[]? issuers) => _inner.GetServerAliases(keyType, issuers);
    }

    // A Java socket exposes separate read (InputStream) and write (OutputStream) .NET Streams; the adb
    // message loop wants one duplex stream. This stitches them together.
    private sealed class DuplexStream : Stream
    {
        private readonly Stream _in;
        private readonly Stream _out;
        public DuplexStream(Stream input, Stream output) { _in = input; _out = output; }
        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _in.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _in.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _out.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _out.WriteAsync(buffer, ct);
        public override void Flush() => _out.Flush();
        public override Task FlushAsync(CancellationToken ct) => _out.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { try { _in.Dispose(); } catch (Exception ex) { AdbLog.Warning("adb-tls", ex); } try { _out.Dispose(); } catch (Exception ex) { AdbLog.Warning("adb-tls", ex); } } }
    }
}
