using System;
using System.Runtime.InteropServices;

namespace Terminal.Router;

internal sealed partial class AndroidPtySession : IPtySession
{
    private int _master = -1;
    private int _pid = -1;
    private bool _closed;
    private uint _exitCode = uint.MaxValue;

    public uint ProcessId => checked((uint)_pid);

    private AndroidPtySession() { }

    internal static AndroidPtySession Start(string executable, ushort columns, ushort rows)
    {
        var session = new AndroidPtySession();
        int result = SpawnPty(executable, columns, rows, out session._master, out session._pid);
        if (result != 0 || session._master < 0 || session._pid <= 0)
            throw new InvalidOperationException($"forkpty spawn failed: {result}");
        return session;
    }

    public int Read(byte[] buffer)
    {
        if (_closed || _master < 0) throw new ObjectDisposedException(nameof(AndroidPtySession));
        nint result;
        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                do { result = ReadUnix(_master, (nint)pointer, checked((nuint)buffer.Length)); }
                while (result == -1 && Marshal.GetLastPInvokeError() == 4);
            }
        }
        if (result == -1 && Marshal.GetLastPInvokeError() == 5) return 0;
        if (result < 0) throw new InvalidOperationException($"PTY read failed: {Marshal.GetLastPInvokeError()}");
        return checked((int)result);
    }

    public void Write(byte[] buffer)
    {
        if (_closed || _master < 0) throw new ObjectDisposedException(nameof(AndroidPtySession));
        int completed = 0;
        while (completed < buffer.Length)
        {
            nint result;
            unsafe
            {
                fixed (byte* pointer = &buffer[completed])
                {
                    do { result = WriteUnix(_master, (nint)pointer, checked((nuint)(buffer.Length - completed))); }
                    while (result == -1 && Marshal.GetLastPInvokeError() == 4);
                }
            }
            if (result <= 0) throw new InvalidOperationException($"PTY write failed: {Marshal.GetLastPInvokeError()}");
            completed += checked((int)result);
        }
    }

    public void Resize(ushort columns, ushort rows)
    {
        if (_closed || _master < 0) throw new ObjectDisposedException(nameof(AndroidPtySession));
        int result = ResizePty(_master, columns, rows);
        if (result != 0) throw new InvalidOperationException($"PTY resize failed: {result}");
    }

    public uint Close()
    {
        if (_closed) return _exitCode;
        _closed = true;
        int result = ClosePty(_master, _pid, out int exitCode);
        _master = -1;
        if (result != 0) throw new InvalidOperationException($"PTY close failed: {result}");
        _exitCode = unchecked((uint)exitCode);
        return _exitCode;
    }

    public void Dispose()
    {
        if (!_closed) _ = Close();
    }

    [LibraryImport("terminal_router_pty", EntryPoint = "terminal_router_spawn_pty", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SpawnPty(string executable, ushort columns, ushort rows, out int master, out int pid);

    [LibraryImport("terminal_router_pty", EntryPoint = "terminal_router_resize_pty")]
    private static partial int ResizePty(int master, ushort columns, ushort rows);

    [LibraryImport("terminal_router_pty", EntryPoint = "terminal_router_close_pty")]
    private static partial int ClosePty(int master, int pid, out int exitCode);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadUnix(int fd, nint buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint WriteUnix(int fd, nint buffer, nuint length);
}
