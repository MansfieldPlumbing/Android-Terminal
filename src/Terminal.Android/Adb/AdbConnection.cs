using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace Subsystem;

public class AdbException : Exception
{
    public AdbException(string message) : base(message) { }
}

public class AdbMessage
{
    public uint Command;
    public uint Arg0;
    public uint Arg1;
    public uint DataLength;
    public uint DataCrc32;
    public uint Magic;
    public byte[] Data = Array.Empty<byte>();

    public static uint GetCommandMask(string cmd)
    {
        if (cmd.Length != 4) throw new ArgumentException("Command must be 4 characters");
        byte[] bytes = Encoding.ASCII.GetBytes(cmd);
        return BitConverter.ToUInt32(bytes, 0);
    }
}

// The ADB device-transport protocol core — PORTABLE (pure System.* + Stream). The socket and the TLS
// upgrade are the per-head seam (IAdbTransport: AndroidAdbTransport / WindowsAdbTransport); this class
// only speaks the wire protocol (CNXN/STLS/AUTH/OPEN/WRTE framing) over the duplex Streams the transport
// hands back. This is the one impl both heads share behind the `ss adb` broker.
public class AdbConnection : IDisposable
{
    public static readonly uint CMD_SYNC = AdbMessage.GetCommandMask("SYNC");
    public static readonly uint CMD_CNXN = AdbMessage.GetCommandMask("CNXN");
    public static readonly uint CMD_AUTH = AdbMessage.GetCommandMask("AUTH");
    public static readonly uint CMD_OPEN = AdbMessage.GetCommandMask("OPEN");
    public static readonly uint CMD_OKAY = AdbMessage.GetCommandMask("OKAY");
    public static readonly uint CMD_CLSE = AdbMessage.GetCommandMask("CLSE");
    public static readonly uint CMD_WRTE = AdbMessage.GetCommandMask("WRTE");
    public static readonly uint CMD_STLS = AdbMessage.GetCommandMask("STLS");

    public const uint AUTH_TOKEN = 1;
    public const uint AUTH_SIGNATURE = 2;
    public const uint AUTH_RSAPUBLICKEY = 3;

    public const uint MAX_PAYLOAD = 1024 * 1024;
    public const uint VERSION = 0x01000000;
    public const uint A_STLS_VERSION = 0x01000000;

    private readonly IAdbTransport _transport;
    private Stream _stream = null!;
    private readonly RSA _rsaKey;
    private bool _isConnected;

    private ConcurrentDictionary<uint, TaskCompletionSource<AdbMessage>> _pendingStreams = new();
    private ConcurrentDictionary<uint, BlockingCollection<AdbMessage>> _streamQueues = new();
    private uint _nextLocalId = 1;

    public AdbConnection(RSA rsaKey, IAdbTransport transport)
    {
        _rsaKey = rsaKey;
        _transport = transport;
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        // The transport owns the socket + TLS (the per-head seam); this class owns the wire protocol.
        // adb wireless StartTLS (STLS), CLEARTEXT first:
        //   client -> CNXN ;  device -> STLS ;  client -> STLS ;  THEN the TLS handshake.
        var plain = await _transport.ConnectAsync(host, port, cancellationToken);

        var systemIdentity = Encoding.ASCII.GetBytes("host::Subsystem\0");
        await WriteMessageAsync(plain, CMD_CNXN, VERSION, MAX_PAYLOAD, systemIdentity, cancellationToken);

        var reply = await ReadMessageAsync(plain, cancellationToken);
        AdbLog.Debug("adb", $"after cleartext CNXN, device sent: {FormatCommand(reply.Command)} (arg0=0x{reply.Arg0:x8})");
        if (reply.Command != CMD_STLS)
            throw new AdbException($"Expected STLS, got {FormatCommand(reply.Command)}");

        await WriteMessageAsync(plain, CMD_STLS, A_STLS_VERSION, 0, Array.Empty<byte>(), cancellationToken);

        // TLS upgrade over the SAME socket (StartTLS). adbd authenticates by the client cert's public key,
        // matched against the keys stored at pairing — so the cert must carry the SAME RSA key.
        _stream = await _transport.UpgradeToTlsAsync(_rsaKey, host, port, cancellationToken);
        _ = Task.Factory.StartNew(
            () => ReadLoopAsync(cancellationToken).GetAwaiter().GetResult(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Post-TLS: per adbd source (adbd_wifi_secure_connect), after a successful TLS handshake the DAEMON
        // proactively send_connect()s its "device::..." CNXN once it has verified our client cert against the
        // paired keystore. We just receive it — we must NOT send our own CNXN (that would re-enter
        // handle_new_connection and fire another STLS). No banner => cert rejected (daemon Kicked the transport).
        AdbMessage? banner = null;
        if (_handshakeQueue.TryTake(out var m, 8000)) banner = m;
        if (banner == null) throw new AdbException("post-TLS: no CNXN from device (cert rejected / kicked?)");
        AdbLog.Debug("adb", $"post-TLS device sent: {FormatCommand(banner.Command)} banner='{Encoding.ASCII.GetString(banner.Data).Replace('\0', '.')}'");
        if (banner.Command != CMD_CNXN)
            throw new AdbException($"post-TLS expected CNXN, got {FormatCommand(banner.Command)}");

        _isConnected = true;
        AdbLog.Debug("adb", "ADB elevated channel ESTABLISHED");
    }

    // A dedicated queue for the initial handshake, before full routing starts.
    private BlockingCollection<AdbMessage> _handshakeQueue = new();
    private async Task<AdbMessage> ReadFromLoopAsync(CancellationToken ct)
    {
        return _handshakeQueue.Take(ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var headerBuffer = new byte[24];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await _stream.ReadAtLeastAsync(headerBuffer, 24, false, ct);
                if (bytesRead < 24) break;

                var msg = new AdbMessage
                {
                    Command = BitConverter.ToUInt32(headerBuffer, 0),
                    Arg0 = BitConverter.ToUInt32(headerBuffer, 4),
                    Arg1 = BitConverter.ToUInt32(headerBuffer, 8),
                    DataLength = BitConverter.ToUInt32(headerBuffer, 12),
                    DataCrc32 = BitConverter.ToUInt32(headerBuffer, 16),
                    Magic = BitConverter.ToUInt32(headerBuffer, 20)
                };

                if (msg.Magic != (msg.Command ^ 0xFFFFFFFF))
                {
                    throw new AdbException("ADB Magic mismatch");
                }

                if (msg.DataLength > 0)
                {
                    msg.Data = new byte[msg.DataLength];
                    await _stream.ReadAtLeastAsync(msg.Data, (int)msg.DataLength, false, ct);
                }

                if (!_isConnected)
                {
                    _handshakeQueue.Add(msg);
                }
                else
                {
                    RouteMessage(msg);
                }
            }
        }
        catch (Exception ex)
        {
            AdbLog.Debug("adb", $"ReadLoop ended: {ex.Message}");
            // Handle disconnect
        }
    }

    private void RouteMessage(AdbMessage msg)
    {
        uint localId = msg.Arg1; // For OPEN/WRTE/CLSE/OKAY, arg1 is usually our localId
        if (msg.Command == CMD_OPEN) localId = msg.Arg0; // Should not receive OPEN from device usually

        if (_streamQueues.TryGetValue(localId, out var queue))
        {
            queue.Add(msg);
        }
    }

    public Task SendMessageAsync(uint command, uint arg0, uint arg1, byte[] data, CancellationToken ct = default)
        => WriteMessageAsync(_stream, command, arg0, arg1, data, ct);

    // Write a 24-byte adb message (+ payload) to an arbitrary stream. Used both on the plaintext duplex
    // during the cleartext STLS handshake and on the TLS duplex afterwards.
    private static async Task WriteMessageAsync(Stream stream, uint command, uint arg0, uint arg1, byte[] data, CancellationToken ct = default)
    {
        var header = new byte[24];
        BitConverter.TryWriteBytes(header.AsSpan(0), command);
        BitConverter.TryWriteBytes(header.AsSpan(4), arg0);
        BitConverter.TryWriteBytes(header.AsSpan(8), arg1);
        BitConverter.TryWriteBytes(header.AsSpan(12), (uint)(data?.Length ?? 0));

        uint crc = 0; // adb's "crc32" is just a byte sum (checked only by pre-v2 peers)
        if (data != null) foreach (var b in data) crc += b;
        BitConverter.TryWriteBytes(header.AsSpan(16), crc);
        BitConverter.TryWriteBytes(header.AsSpan(20), command ^ 0xFFFFFFFF);

        await stream.WriteAsync(header, ct);
        if (data != null && data.Length > 0) await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    // Read one full adb message (header + payload) from an arbitrary stream — for the cleartext STLS
    // handshake, where the async ReadLoop isn't running yet.
    private async Task<AdbMessage> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[24];
        await ReadExactlyAsync(stream, header, 24);
        var msg = new AdbMessage
        {
            Command = BitConverter.ToUInt32(header, 0),
            Arg0 = BitConverter.ToUInt32(header, 4),
            Arg1 = BitConverter.ToUInt32(header, 8),
            DataLength = BitConverter.ToUInt32(header, 12),
            DataCrc32 = BitConverter.ToUInt32(header, 16),
            Magic = BitConverter.ToUInt32(header, 20)
        };
        if (msg.Magic != (msg.Command ^ 0xFFFFFFFF)) throw new AdbException("ADB Magic mismatch");
        if (msg.DataLength > 0)
        {
            msg.Data = new byte[msg.DataLength];
            await ReadExactlyAsync(stream, msg.Data, (int)msg.DataLength);
        }
        return msg;
    }

    private readonly object _idLock = new();

    // Open a shell: stream over the elevated channel, run a command, and collect its stdout.
    // adb stream lifecycle: OPEN(local,0,"shell:cmd\0") -> device OKAY(remote,local) -> device
    // WRTE(remote,local,data)+ (we OKAY each) -> device CLSE -> we CLSE. shell: (v1) returns raw output.
    public async Task<string> ExecuteShellAsync(string command, CancellationToken ct = default)
    {
        if (!_isConnected) throw new AdbException("ADB connection not established.");

        uint localId;
        lock (_idLock) { localId = _nextLocalId++; }
        var queue = new BlockingCollection<AdbMessage>();
        _streamQueues[localId] = queue;
        try
        {
            var payload = Encoding.UTF8.GetBytes("shell:" + command + "\0");
            await SendMessageAsync(CMD_OPEN, localId, 0, payload, ct);

            var sb = new StringBuilder();
            uint remoteId = 0;
            while (true)
            {
                var msg = queue.Take(ct);
                if (msg.Command == CMD_OKAY)
                {
                    remoteId = msg.Arg0;
                }
                else if (msg.Command == CMD_WRTE)
                {
                    if (msg.Data.Length > 0) sb.Append(Encoding.UTF8.GetString(msg.Data));
                    await SendMessageAsync(CMD_OKAY, localId, msg.Arg0, Array.Empty<byte>(), ct); // ack each WRTE
                }
                else if (msg.Command == CMD_CLSE)
                {
                    await SendMessageAsync(CMD_CLSE, localId, msg.Arg0, Array.Empty<byte>(), ct);
                    break;
                }
            }
            return sb.ToString();
        }
        finally
        {
            _streamQueues.TryRemove(localId, out _);
            queue.Dispose();
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private byte[] GetPublicKeyFormat(RSA rsa)
    {
        // Android adb_keys format: base64(packed RSAPublicKey struct) + " name\0".
        return Encoding.ASCII.GetBytes(AndroidPubKey.Encode(rsa, "Subsystem") + "\0");
    }

    private string FormatCommand(uint cmd)
    {
        var bytes = BitConverter.GetBytes(cmd);
        return Encoding.ASCII.GetString(bytes);
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch (Exception ex) { AdbLog.Debug("adb", ex.Message); }
        try { _transport?.Dispose(); } catch (Exception ex) { AdbLog.Debug("adb", ex.Message); }
    }
}
