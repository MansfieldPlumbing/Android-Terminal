using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Terminal.Router;

internal enum WireKind : ushort
{
    Request = 1,
    Completion = 2,
    Quiesce = 5,
    QuiesceAcknowledgement = 6,
    Ready = 7,
    Data = 8,
}

internal enum RouterOperation : ushort
{
    Open = 1,
    Opened = 2,
    Input = 3,
    Output = 4,
    Resize = 5,
    Resized = 6,
    Close = 7,
    Closed = 8,
    Error = 9,
}

internal readonly record struct WireFrame(
    WireKind Kind,
    ulong Correlation,
    ulong Generation,
    byte[] Payload);

internal readonly record struct RouterMessage(
    RouterOperation Operation,
    ulong RouteId,
    uint ValueA,
    uint ValueB,
    byte[] Payload);

internal sealed partial class EndpointTransport : IDisposable
{
    private const uint WireMagic = 0x52454d44;
    private const ushort WireVersion = 1;
    private const ushort WireHeaderSize = 36;
    private const uint MaxWirePayload = 1024 * 1024;
    private readonly nint _endpoint;
    private readonly bool _windows;
    private readonly object _ioGate = new();
    private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();
    private nint _readThread;
    private bool _disposed;

    private EndpointTransport(nint endpoint, bool windows)
    {
        _endpoint = endpoint;
        _windows = windows;
    }

    internal static EndpointTransport Acquire(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            const string prefix = "--remedy-channel-handle=";
            if (args.Length != 1 || !args[0].StartsWith(prefix, StringComparison.Ordinal) ||
                !ulong.TryParse(args[0].AsSpan(prefix.Length), out ulong raw) || raw == 0)
                throw new InvalidOperationException("Invalid inherited endpoint bootstrap.");
            return new EndpointTransport(unchecked((nint)raw), true);
        }

        if (args.Length != 0) throw new InvalidOperationException("Android bootstrap accepts no arguments.");
        return new EndpointTransport(3, false);
    }

    internal WireFrame ReadFrame()
    {
        byte[] header = new byte[WireHeaderSize];
        ReadExact(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != WireMagic ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != WireVersion ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != WireHeaderSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10)) != 0)
            throw new InvalidOperationException("Invalid Remedy frame header.");

        ushort rawKind = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (rawKind < 1 || rawKind > (ushort)WireKind.Data || payloadLength > MaxWirePayload)
            throw new InvalidOperationException("Invalid Remedy frame bounds.");

        ulong correlation = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
        ulong generation = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24));
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
        byte[] payload = new byte[payloadLength];
        if (payload.Length != 0) ReadExact(payload);
        uint expected = payload.Length == 0 ? 0 : Adler32(payload);
        if (checksum != expected) throw new InvalidOperationException("Invalid Remedy frame checksum.");
        return new WireFrame((WireKind)rawKind, correlation, generation, payload);
    }

    internal void WriteFrame(WireKind kind, ulong correlation, ulong generation, byte[] payload)
    {
        WriteEncodedFrame(kind, correlation, generation, payload);
    }

    internal void WriteFrameFromProducer(WireKind kind, ulong correlation, ulong generation, byte[] payload)
    {
        if (!_windows)
        {
            WriteEncodedFrame(kind, correlation, generation, payload);
            return;
        }

        var pending = new PendingWrite(kind, correlation, generation, payload);
        lock (_ioGate)
        {
            _pendingWrites.Enqueue(pending);
            if (_readThread != 0 && !CancelSynchronousIo(_readThread))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != ErrorNotFound)
                    throw new InvalidOperationException($"Endpoint read cancellation failed: {error}.");
            }
        }

        if (!pending.Completed.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Endpoint producer write did not complete.");
        if (pending.Failure is not null)
            throw new InvalidOperationException("Endpoint producer write failed.", pending.Failure);
    }

    private void WriteEncodedFrame(WireKind kind, ulong correlation, ulong generation, byte[] payload)
    {
        byte[] header = new byte[WireHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, WireMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), WireVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), WireHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), correlation);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), generation);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), payload.Length == 0 ? 0 : Adler32(payload));
        WriteExact(header);
        if (payload.Length != 0) WriteExact(payload);
    }

    internal static RouterMessage DecodeRouterMessage(byte[] bytes)
    {
        if (bytes.Length < RouterCodec.HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != RouterCodec.Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != RouterCodec.Version ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) != RouterCodec.HeaderSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)) != 0)
            throw new InvalidOperationException("Invalid router message header.");
        ushort rawOperation = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        if (rawOperation < 1 || rawOperation > (ushort)RouterOperation.Error ||
            payloadLength > RouterCodec.MaxPayload || bytes.Length != RouterCodec.HeaderSize + payloadLength)
            throw new InvalidOperationException("Invalid router message bounds.");
        byte[] payload = new byte[payloadLength];
        if (payload.Length != 0) bytes.AsSpan(RouterCodec.HeaderSize).CopyTo(payload);
        return new RouterMessage(
            (RouterOperation)rawOperation,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)),
            payload);
    }

    internal static byte[] EncodeRouterMessage(RouterMessage message)
    {
        if (message.Payload.Length > RouterCodec.MaxPayload)
            throw new InvalidOperationException("Router payload exceeds the bounded message size.");
        byte[] bytes = new byte[RouterCodec.HeaderSize + message.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, RouterCodec.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), RouterCodec.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)message.Operation);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), RouterCodec.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)message.Payload.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), message.RouteId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), message.ValueA);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), message.ValueB);
        if (message.Payload.Length != 0) message.Payload.CopyTo(bytes, RouterCodec.HeaderSize);
        return bytes;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1;
        uint b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private void ReadExact(byte[] buffer)
    {
        int completed = 0;
        while (completed < buffer.Length)
        {
            int transferred;
            unsafe
            {
                fixed (byte* pointer = &buffer[completed])
                {
                    transferred = _windows
                        ? ReadWindows((nint)pointer, buffer.Length - completed)
                        : ReadAndroid((nint)pointer, buffer.Length - completed);
                }
            }
            if (transferred == ReadInterrupted)
            {
                DrainPendingWrites();
                continue;
            }
            if (transferred <= 0) throw new InvalidOperationException("Inherited endpoint reached terminal closure.");
            completed += transferred;
        }
    }

    private void DrainPendingWrites()
    {
        while (_pendingWrites.TryDequeue(out PendingWrite? pending))
        {
            try { WriteEncodedFrame(pending.Kind, pending.Correlation, pending.Generation, pending.Payload); }
            catch (Exception ex) { pending.Failure = ex; }
            finally { pending.Completed.Set(); }
        }
    }

    private void WriteExact(byte[] buffer)
    {
        int completed = 0;
        while (completed < buffer.Length)
        {
            int transferred;
            unsafe
            {
                fixed (byte* pointer = &buffer[completed])
                {
                    transferred = _windows
                        ? WriteWindows((nint)pointer, buffer.Length - completed)
                        : WriteAndroid((nint)pointer, buffer.Length - completed);
                }
            }
            if (transferred <= 0) throw new InvalidOperationException("Inherited endpoint write failed.");
            completed += transferred;
        }
    }

    private int ReadWindows(nint buffer, int length)
    {
        nint thread = OpenThread(ThreadTerminate, false, GetCurrentThreadId());
        if (thread == 0) throw new InvalidOperationException($"OpenThread failed: {Marshal.GetLastPInvokeError()}.");
        lock (_ioGate)
        {
            if (!_pendingWrites.IsEmpty)
            {
                if (!CloseHandle(thread)) throw new InvalidOperationException("Read-thread handle close failed.");
                return ReadInterrupted;
            }
            if (_readThread != 0) throw new InvalidOperationException("Concurrent endpoint reads are forbidden.");
            _readThread = thread;
        }

        bool success = ReadFile(_endpoint, buffer, checked((uint)length), out uint transferred, 0);
        int error = success ? 0 : Marshal.GetLastPInvokeError();
        lock (_ioGate)
        {
            if (_readThread != thread) throw new InvalidOperationException("Endpoint read ownership was corrupted.");
            _readThread = 0;
        }
        if (!CloseHandle(thread)) throw new InvalidOperationException("Read-thread handle close failed.");
        if (success) return checked((int)transferred);
        return error == ErrorOperationAborted ? ReadInterrupted : -1;
    }

    private int WriteWindows(nint buffer, int length)
    {
        return WriteFile(_endpoint, buffer, checked((uint)length), out uint transferred, 0)
            ? checked((int)transferred)
            : -1;
    }

    private int ReadAndroid(nint buffer, int length)
    {
        nint result;
        do { result = ReadUnix(checked((int)_endpoint), buffer, checked((nuint)length)); }
        while (result == -1 && Marshal.GetLastPInvokeError() == 4);
        return checked((int)result);
    }

    private int WriteAndroid(nint buffer, int length)
    {
        nint result;
        do { result = WriteUnix(checked((int)_endpoint), buffer, checked((nuint)length)); }
        while (result == -1 && Marshal.GetLastPInvokeError() == 4);
        return checked((int)result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_windows)
        {
            if (!CloseHandle(_endpoint)) throw new InvalidOperationException("Inherited endpoint close failed.");
        }
        else if (CloseUnix(checked((int)_endpoint)) != 0)
        {
            throw new InvalidOperationException("Inherited endpoint close failed.");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(nint handle, nint buffer, uint length, out uint transferred, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(nint handle, nint buffer, uint length, out uint transferred, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelSynchronousIo(nint thread);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadUnix(int fd, nint buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint WriteUnix(int fd, nint buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseUnix(int fd);

    private const int ReadInterrupted = int.MinValue;
    private const int ErrorOperationAborted = 995;
    private const int ErrorNotFound = 1168;
    private const uint ThreadTerminate = 0x0001;

    private sealed class PendingWrite
    {
        internal WireKind Kind { get; }
        internal ulong Correlation { get; }
        internal ulong Generation { get; }
        internal byte[] Payload { get; }
        internal ManualResetEventSlim Completed { get; } = new(false);
        internal Exception? Failure { get; set; }

        internal PendingWrite(WireKind kind, ulong correlation, ulong generation, byte[] payload)
        {
            Kind = kind;
            Correlation = correlation;
            Generation = generation;
            Payload = payload;
        }
    }
}

internal static class RouterCodec
{
    internal const uint Magic = 0x52545254;
    internal const ushort Version = 1;
    internal const ushort HeaderSize = 32;
    internal const uint MaxPayload = 8192;
}
