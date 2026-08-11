using System;

namespace Terminal.Router;

internal interface IPtySession : IDisposable
{
    uint ProcessId { get; }
    int Read(byte[] buffer);
    void Write(byte[] buffer);
    void Resize(ushort columns, ushort rows);
    uint Close();
}

internal static class PtySessionFactory
{
    internal static IPtySession Start(string command, ushort columns, ushort rows)
    {
        if (columns == 0 || rows == 0) throw new InvalidOperationException("PTY dimensions must be non-zero.");
        return OperatingSystem.IsWindows()
            ? WindowsPtySession.Start(command, columns, rows)
            : AndroidPtySession.Start(command, columns, rows);
    }
}
