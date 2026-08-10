using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Subsystem;

// The per-head socket + TLS seam under the (shared) ADB protocol core. AdbConnection speaks the wire
// protocol (CNXN/STLS/AUTH/OPEN/WRTE framing) over the duplex Streams this returns; the transport owns
// the actual socket and the StartTLS upgrade — the ONE thing that differs per head:
//   Android : Java.Net.Socket + Conscrypt SSLSocket (the .NET SslStream won't put a self-signed client
//             cert on the wire for TLS 1.3 on Android — adbd kicks with PEER_DID_NOT_RETURN_A_CERTIFICATE).
//   Windows : System.Net.Sockets.TcpClient + System.Net.Security.SslStream (no such limitation).
// One protocol implementation, two transport bindings — the same seam discipline as the rest of the heads.
public interface IAdbTransport : IDisposable
{
    // Open a plaintext TCP connection; return a duplex stream for the cleartext STLS handshake.
    Task<Stream> ConnectAsync(string host, int port, CancellationToken ct = default);

    // StartTLS: layer TLS 1.3 over the SAME connected socket, presenting a self-signed client cert that
    // carries clientKey (adbd authenticates by the cert's public key against the paired keystore). Returns
    // the encrypted duplex stream the protocol continues on.
    Task<Stream> UpgradeToTlsAsync(RSA clientKey, string host, int port, CancellationToken ct = default);
}
