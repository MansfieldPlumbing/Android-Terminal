using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Terminal.Router;

internal sealed partial class WindowsPtySession : IPtySession
{
    private nint _pseudoConsole;
    private SafeFileHandle? _inputRead;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private SafeFileHandle? _outputWrite;
    private SafeProcessHandle? _process;
    private SafeFileHandle? _thread;
    private SafeFileHandle? _job;
    private FileStream? _outputStream;
    private bool _closed;
    private uint _exitCode = uint.MaxValue;

    public uint ProcessId { get; private set; }

    private WindowsPtySession() { }

    internal static WindowsPtySession Start(string command, ushort columns, ushort rows)
    {
        var session = new WindowsPtySession();
        try
        {
            session.StartCore(command, columns, rows);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void StartCore(string command, ushort columns, ushort rows)
    {
        if (!CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, 0, 0))
            ThrowLastError("CreatePipe(output)");
        _outputRead = outputRead;
        _outputWrite = outputWrite;
        if (!CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, 0, 0))
        {
            _outputWrite.Dispose();
            _outputWrite = null;
            inputRead?.Dispose();
            ThrowLastError("CreatePipe(input)");
        }
        _inputRead = inputRead;
        _inputWrite = inputWrite;

        int createResult = CreatePseudoConsole(
            new Coord { X = checked((short)columns), Y = checked((short)rows) },
            inputRead!,
            outputWrite!,
            0,
            out _pseudoConsole);
        if (createResult != 0) throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{createResult:x8}");

        int attachResult = AttachConPty(
            _pseudoConsole, command, out nuint processHandle, out nuint threadHandle, out uint processId);
        if (attachResult != 0 || processHandle == 0 || threadHandle == 0 || processId == 0)
            throw new InvalidOperationException($"ConPTY process attachment failed: {attachResult}");
        _process = new SafeProcessHandle(checked((nint)processHandle), true);
        _thread = new SafeFileHandle(checked((nint)threadHandle), true);
        ProcessId = processId;

        _job = CreateJobObject(0, 0);
        if (_job.IsInvalid) ThrowLastError("CreateJobObject");
        var limits = new JobObjectExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnClose;
        if (!SetInformationJobObject(
                _job,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                Marshal.SizeOf<JobObjectExtendedLimitInformation>())) ThrowLastError("SetInformationJobObject");
        if (!AssignProcessToJobObject(_job, _process)) ThrowLastError("AssignProcessToJobObject");

        _outputStream = new FileStream(_outputRead, FileAccess.Read, 4096, false);
    }

    public int Read(byte[] buffer)
    {
        if (_outputStream is null) throw new ObjectDisposedException(nameof(WindowsPtySession));
        return _outputStream.Read(buffer, 0, buffer.Length);
    }

    public unsafe void Write(byte[] buffer)
    {
        if (_inputWrite is null || _inputWrite.IsInvalid)
            throw new ObjectDisposedException(nameof(WindowsPtySession));
        bool addedRef = false;
        try
        {
            _inputWrite.DangerousAddRef(ref addedRef);
            int completed = 0;
            while (completed < buffer.Length)
            {
                fixed (byte* pointer = &buffer[completed])
                {
                    if (!WriteFile(
                            _inputWrite.DangerousGetHandle(),
                            (nint)pointer,
                            checked((uint)(buffer.Length - completed)),
                            out uint transferred,
                            0) || transferred == 0) ThrowLastError("WriteFile(ConPTY input)");
                    completed += checked((int)transferred);
                }
            }
        }
        finally
        {
            if (addedRef) _inputWrite.DangerousRelease();
        }
    }

    public void Resize(ushort columns, ushort rows)
    {
        if (_closed || _pseudoConsole == 0) throw new ObjectDisposedException(nameof(WindowsPtySession));
        int result = ResizePseudoConsole(
            _pseudoConsole,
            new Coord { X = checked((short)columns), Y = checked((short)rows) });
        if (result != 0) throw new InvalidOperationException($"ResizePseudoConsole failed: 0x{result:x8}");
    }

    public uint Close()
    {
        if (_closed) return _exitCode;
        _closed = true;

        _inputWrite?.Dispose();
        _inputWrite = null;
        _job?.Dispose();
        _job = null;

        if (_process is { IsInvalid: false })
        {
            uint waitResult = WaitForSingleObject(_process, 5000);
            if (waitResult == WaitTimeout)
            {
                if (!TerminateProcess(_process, 1)) ThrowLastError("TerminateProcess");
                waitResult = WaitForSingleObject(_process, 5000);
            }
            if (waitResult != WaitObject0) throw new InvalidOperationException("PTY child did not terminate.");
            if (!GetExitCodeProcess(_process, out _exitCode)) ThrowLastError("GetExitCodeProcess");
        }

        if (_pseudoConsole != 0)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = 0;
        }
        _inputRead?.Dispose();
        _inputRead = null;
        _outputWrite?.Dispose();
        _outputWrite = null;
        _outputStream?.Dispose();
        _outputStream = null;
        return _exitCode;
    }

    public void Dispose()
    {
        if (!_closed)
            _ = Close();
        _outputStream?.Dispose();
        _job?.Dispose();
        _process?.Dispose();
        _thread?.Dispose();
        _inputWrite?.Dispose();
        _outputRead?.Dispose();
        _inputRead?.Dispose();
        _outputWrite?.Dispose();
        if (_pseudoConsole != 0)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = 0;
        }
    }

    private static void ThrowLastError(string operation) =>
        throw new InvalidOperationException($"{operation} failed: {Marshal.GetLastPInvokeError()}");

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnClose = 0x00002000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord { internal short X; internal short Y; }


    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreatePseudoConsole(
        Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int ResizePseudoConsole(nint pseudoConsole, Coord size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint pseudoConsole);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out SafeFileHandle readPipe, out SafeFileHandle writePipe, nint attributes, int size);

    [LibraryImport("terminal_router_pty", EntryPoint = "terminal_router_attach_conpty", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AttachConPty(
        nint pseudoConsole, string command, out nuint process, out nuint thread, out uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, ref JobObjectExtendedLimitInformation information, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(nint handle, nint buffer, uint length, out uint transferred, nint overlapped);
}
