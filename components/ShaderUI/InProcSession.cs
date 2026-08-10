using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TuiDwm.Port;

/// <summary>
/// In-process virtual PTY session emulator.
/// Uses in-memory blocking byte streams and PowerShell RunspacePool
/// to completely replace the native OS process-spawning PtySession.cs.
/// </summary>
public sealed class InProcSession : IDisposable
{
    private readonly BlockingByteStream _inStream;
    private readonly BlockingByteStream _outStream;
    private readonly CancellationTokenSource _cts;
    private readonly Task _pumpTask;
    private bool _isDisposed;

    public Stream OutputStream => _outStream;
    public Stream InputStream => _inStream;

    public int ProcessId => 4242; // Virtual constant process ID
    public bool IsDisposed => _isDisposed;
    public bool HasExited => _isDisposed;

    private InProcSession()
    {
        _inStream = new BlockingByteStream();
        _outStream = new BlockingByteStream();
        _cts = new CancellationTokenSource();
        _pumpTask = Task.Run(RunInputPumpAsync);
    }

    public static InProcSession Start(string commandLine, short cols, short rows, string? workingDirectory = null)
    {
        // Ignore native commandLine/directory arguments, we run in-process PowerShell!
        return new InProcSession();
    }

    public void Resize(short cols, short rows)
    {
        // Stateless, no-op for virtual in-memory console
    }

    public void Kill()
    {
        Dispose();
    }

    private async Task RunInputPumpAsync()
    {
        var commandBuilder = new StringBuilder();
        var readBuf = new byte[1];

        // Write initial branding and console prompt to terminal output stream
        WriteOutput("Windows PowerShell (In-Process Multiplexed)\r\n");
        WriteOutput("Copyright (C) Microsoft Corporation. All rights reserved.\r\n\r\n");
        WriteOutput("PS C:\\> ");

        try
        {
            while (!_cts.IsCancellationRequested && !_isDisposed)
            {
                // Non-blocking read from virtual stdin
                int bytesRead = await Task.Run(() => _inStream.Read(readBuf, 0, 1), _cts.Token).ConfigureAwait(false);
                if (bytesRead == 0) break;

                byte b = readBuf[0];

                if (b == 0x0D || b == 0x0A) // Enter / Return
                {
                    WriteOutput("\r\n");
                    string cmd = commandBuilder.ToString().Trim();
                    commandBuilder.Clear();

                    if (!string.IsNullOrEmpty(cmd))
                    {
                        // Execute command asynchronously using the RunspacePool multiplexer
                        string result = await PwshMultiplexer.Instance.ExecuteCommandAsync(cmd).ConfigureAwait(false);
                        WriteOutput(result);
                    }

                    WriteOutput("PS C:\\> ");
                }
                else if (b == 0x08 || b == 0x7F) // Backspace / Delete
                {
                    if (commandBuilder.Length > 0)
                    {
                        commandBuilder.Remove(commandBuilder.Length - 1, 1);
                        WriteOutput("\b \b"); // Erase character from terminal display
                    }
                }
                else
                {
                    char c = (char)b;
                    commandBuilder.Append(c);
                    _outStream.WriteByteDirect(b); // Local character echo
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            WriteOutput($"\r\nVirtual Console Thread Fault: {ex.Message}\r\n");
        }
    }

    private void WriteOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        _outStream.WriteBytesDirect(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _cts.Cancel();
        _inStream.Dispose();
        _outStream.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// A high-performance, thread-safe blocking circular buffer wrapped as a .NET Stream.
/// Perfect for in-process terminal stream IPC without external handles.
/// </summary>
public sealed class BlockingByteStream : Stream
{
    private readonly BlockingCollection<byte> _queue = new();
    private bool _isDisposed;

    public void WriteByteDirect(byte b)
    {
        if (!_isDisposed)
        {
            try { _queue.Add(b); } catch (InvalidOperationException) { }
        }
    }

    public void WriteBytesDirect(byte[] bytes, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (_isDisposed) break;
            try { _queue.Add(bytes[offset + i]); } catch (InvalidOperationException) { }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_isDisposed) return 0;
        int bytesRead = 0;
        try
        {
            while (bytesRead < count)
            {
                if (_queue.TryTake(out byte b, bytesRead == 0 ? Timeout.Infinite : 10))
                {
                    buffer[offset + bytesRead] = b;
                    bytesRead++;
                }
                else
                {
                    break;
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteBytesDirect(buffer, offset, count);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _isDisposed = true;
        _queue.CompleteAdding();
        _queue.Dispose();
        base.Dispose(disposing);
    }
}
