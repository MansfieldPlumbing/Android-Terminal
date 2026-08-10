// ConPTY.cs — ConPTY-Comfy
//
// Win32 CreatePseudoConsole interop.
// Session is a proper RAII wrapper — owns the PTY, the process, and the Job Object.
//
// The Job Object is the dead man's switch.
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE means Python cannot outlive this process
// for any reason — clean exit, crash, OOM, Task Manager kill.
// The kernel enforces it. We do not need to remember to clean up.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ConPtyComfy
{
    // ── P/Invoke declarations ────────────────────────────────────────────────

    internal static partial class NativeMethods
    {
        // ConPTY
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int CreatePseudoConsole(
            COORD size,
            SafeFileHandle hInput,
            SafeFileHandle hOutput,
            uint dwFlags,
            out nint phPC);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial int ResizePseudoConsole(nint hPC, COORD size);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial void ClosePseudoConsole(nint hPC);

        // Process
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool InitializeProcThreadAttributeList(
            nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UpdateProcThreadAttribute(
            nint lpAttributeList, uint dwFlags, nint Attribute,
            nint lpValue, nint cbSize, nint lpPreviousValue, nint lpReturnSize);

        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateProcessW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreateProcess(
            nint lpApplicationName,
            nint lpCommandLine,
            nint lpProcessAttributes, nint lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            nint lpEnvironment,
            nint lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint WaitForSingleObject(SafeHandle hHandle, uint dwMilliseconds);

        // Pipes
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CreatePipe(
            out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

        // Job Objects — the dead man's switch
        [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateJobObjectW")]
        internal static partial SafeFileHandle CreateJobObject(nint lpJobAttributes, nint lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetInformationJobObject(
            SafeFileHandle hJob,
            int JobObjectInformationClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
            int cbJobObjectInformationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AssignProcessToJobObject(
            SafeFileHandle hJob, SafeProcessHandle hProcess);

        // Clipboard
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenClipboard(nint hWndNewOwner);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EmptyClipboard();

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint SetClipboardData(uint uFormat, nint hMem);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseClipboard();

        [LibraryImport("kernel32.dll")]
        internal static partial nint GlobalAlloc(uint uFlags, nint dwBytes);

        [LibraryImport("kernel32.dll")]
        internal static partial nint GlobalLock(nint hMem);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GlobalUnlock(nint hMem);

        // MessageBox
        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int MessageBox(nint hWnd, string text, string caption, uint type);
    }

    // ── Win32 structs ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFO
    {
        public int  cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int  dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int  dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public int  dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long  PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint  LimitFlags;
        public nint  MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint  ActiveProcessLimit;
        public nint  Affinity;
        public uint  PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount,  WriteTransferCount,  OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nint  ProcessMemoryLimit, JobMemoryLimit;
        public nint  PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    // ── constants ────────────────────────────────────────────────────────────

    internal static class Win32
    {
        public const uint  EXTENDED_STARTUPINFO_PRESENT  = 0x00080000;
        public const uint  STARTF_USESTDHANDLES          = 0x00000100;
        public const nint  PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = unchecked((nint)0x00020016);
        public const int   JobObjectExtendedLimitInformation   = 9;
        public const uint  JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        public const uint  STILL_ACTIVE                        = 259;
        public const uint  WAIT_OBJECT_0                       = 0;
        public const uint  INFINITE                            = 0xFFFFFFFF;
        public const uint  CF_UNICODETEXT                      = 13;
        public const uint  GMEM_MOVEABLE                       = 0x0002;
        public const uint  MB_OK                               = 0x00000000;
        public const uint  MB_ICONERROR                        = 0x00000010;
    }

    // ── Session — RAII wrapper ───────────────────────────────────────────────

    internal sealed class PtySession : IDisposable
    {
        // PTY
        private nint             _hPC;
        private SafeFileHandle   _inRead = null!,  _inWrite = null!;
        private SafeFileHandle   _outRead = null!, _outWrite = null!;
        private nint             _attrList;

        // Process
        private SafeProcessHandle _hProcess = null!;
        private SafeHandle        _hThread = null!;

        // Job Object — the dead man's switch
        private SafeFileHandle   _hJob = null!;

        public  SafeProcessHandle ProcessHandle => _hProcess;
        public  int               ProcessId     { get; private set; }
        public  Stream            OutputStream  { get; private set; } = null!;
        public  Stream            InputStream   { get; private set; } = null!;
        public  bool              IsDisposed    { get; private set; }

        public bool HasExited
        {
            get
            {
                if (_hProcess is null || _hProcess.IsInvalid) return true;
                if (!NativeMethods.GetExitCodeProcess(_hProcess, out uint code)) return true;
                return code != Win32.STILL_ACTIVE;
            }
        }

        public uint ExitCode
        {
            get
            {
                if (_hProcess is null || _hProcess.IsInvalid) return 0xFFFFFFFF;
                NativeMethods.GetExitCodeProcess(_hProcess, out uint code);
                return code;
            }
        }

        // ── Start ────────────────────────────────────────────────────────────

        public static PtySession Start(string commandLine, short cols = 220, short rows = 50,
                                       string? workingDirectory = null)
        {
            var session = new PtySession();
            session.StartCore(commandLine, cols, rows, workingDirectory);
            return session;
        }

        private void StartCore(string commandLine, short cols, short rows,
                                string? workingDirectory)
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                bInheritHandle = 1, // TRUE - ConPTY conhost.exe needs to inherit these handles
                lpSecurityDescriptor = nint.Zero
            };

            // pipes: host reads outRead, PTY writes outWrite
            //        PTY reads inRead, host writes inWrite
            if (!NativeMethods.CreatePipe(out _outRead, out _outWrite, ref sa, 0))
                throw new InvalidOperationException($"CreatePipe (out) failed: {Marshal.GetLastWin32Error()}");
            if (!NativeMethods.CreatePipe(out _inRead, out _inWrite, ref sa, 0))
                throw new InvalidOperationException($"CreatePipe (in) failed: {Marshal.GetLastWin32Error()}");

            // create pseudo-console
            var size = new COORD { X = cols, Y = rows };
            int hr = NativeMethods.CreatePseudoConsole(size, _inRead, _outWrite, 0, out _hPC);

            if (hr != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X}");

            // build attribute list with PTY handle
            nint attrSize = nint.Zero;
            NativeMethods.InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attrSize);
            _attrList = Marshal.AllocHGlobal(attrSize);
            if (!NativeMethods.InitializeProcThreadAttributeList(_attrList, 1, 0, ref attrSize))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            // Pointer must survive until CreateProcess completes
            nint pPCValue = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(pPCValue, _hPC);

            var cmdPtr = Marshal.StringToCoTaskMemUni(commandLine);
            var dirPtr = workingDirectory is null ? nint.Zero : Marshal.StringToCoTaskMemUni(workingDirectory);
            
            try
            {
                if (!NativeMethods.UpdateProcThreadAttribute(
                        _attrList, 0, Win32.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        pPCValue, nint.Size, nint.Zero, nint.Zero))
                    throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

                var si = new STARTUPINFOEX();
                si.StartupInfo.cb     = Marshal.SizeOf<STARTUPINFOEX>();
                si.StartupInfo.dwFlags = (int)Win32.STARTF_USESTDHANDLES;
                si.lpAttributeList    = _attrList;

                var pi = new PROCESS_INFORMATION();
                // EXTENDED_STARTUPINFO_PRESENT only — no CREATE_NO_WINDOW.
                // bInheritHandles=false already prevents real console handle inheritance.
                // CREATE_NO_WINDOW was causing pwsh to not attach to the pseudo-console.
                uint creationFlags = Win32.EXTENDED_STARTUPINFO_PRESENT;

                // CreateProcessW receives bInheritHandles = false. This ensures the child
                // does not directly inherit the pipes (it communicates via ConPTY).
                if (!NativeMethods.CreateProcess(
                        nint.Zero, cmdPtr,
                        nint.Zero, nint.Zero, false,
                        creationFlags,
                        nint.Zero,
                        dirPtr,
                        ref si, out pi))
                    throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

                _hProcess = new SafeProcessHandle(pi.hProcess, true);
                _hThread  = new SafeFileHandle(pi.hThread, true);
                ProcessId = pi.dwProcessId;

                // Close PTY-side pipe ends NOW that the child has been created.
                // Must happen after CreateProcess — ConPTY does not duplicate these handles,
                // so closing before CreateProcess leaves ConPTY with an invalid write end and
                // causes ReadFile on the output pipe to block forever with no data.
                _inRead.Dispose();
                _outWrite.Dispose();
            }
            finally
            {
                Marshal.FreeHGlobal(pPCValue);
                Marshal.FreeCoTaskMem(cmdPtr);
                if (dirPtr != nint.Zero) Marshal.FreeCoTaskMem(dirPtr);
            }

            // ── arm the dead man's switch ────────────────────────────────────
            _hJob = NativeMethods.CreateJobObject(nint.Zero, nint.Zero);
            if (_hJob.IsInvalid)
                throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            if (!NativeMethods.SetInformationJobObject(
                    _hJob,
                    Win32.JobObjectExtendedLimitInformation,
                    ref limits,
                    Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                throw new InvalidOperationException($"SetInformationJobObject failed: {Marshal.GetLastWin32Error()}");

            if (!NativeMethods.AssignProcessToJobObject(_hJob, _hProcess))
                throw new InvalidOperationException($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
            // ── dead man's switch armed ──────────────────────────────────────

            // expose streams
            OutputStream = new FileStream(_outRead, FileAccess.Read,  bufferSize: 4096, isAsync: false);
            InputStream  = new FileStream(_inWrite, FileAccess.Write, bufferSize: 4096, isAsync: false);
        }

        public void Resize(short cols, short rows)
        {
            if (_hPC != nint.Zero)
                NativeMethods.ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
        }

        // Call after the child process exits to flush ConPTY's internal buffer
        // and release conhost.exe's hold on the output pipe write end.
        // This unblocks ReadFile with any remaining bytes, then sends EOF.
        public void ClosePty()
        {
            if (_hPC != nint.Zero)
            {
                NativeMethods.ClosePseudoConsole(_hPC);
                _hPC = nint.Zero;
            }
        }

        public void Kill()
        {
            if (_hProcess is { IsInvalid: false })
                NativeMethods.TerminateProcess(_hProcess, 1);
        }

        public Task<uint> WaitForExitAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                NativeMethods.WaitForSingleObject(_hProcess, Win32.INFINITE);
                NativeMethods.GetExitCodeProcess(_hProcess, out uint code);
                return code;
            }, ct);
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            try { OutputStream?.Dispose(); } catch { }
            try { InputStream?.Dispose();  } catch { }

            // close Job Object first — if Python is still running, kernel kills it now
            _hJob?.Dispose();

            if (_attrList != nint.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(_attrList);
                Marshal.FreeHGlobal(_attrList);
                _attrList = nint.Zero;
            }

            if (_hPC != nint.Zero)
            {
                NativeMethods.ClosePseudoConsole(_hPC);
                _hPC = nint.Zero;
            }

            _hProcess?.Dispose();
            _hThread?.Dispose();
            _inWrite?.Dispose();
            _outRead?.Dispose();
        }
    }

    internal static class Win32Clipboard
    {
        public static void SetText(string text)
        {
            if (!NativeMethods.OpenClipboard(nint.Zero))
                throw new InvalidOperationException("OpenClipboard failed");
            try
            {
                NativeMethods.EmptyClipboard();
                var bytes  = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                var hGlobal = NativeMethods.GlobalAlloc(Win32.GMEM_MOVEABLE, bytes.Length);
                var ptr    = NativeMethods.GlobalLock(hGlobal);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                NativeMethods.GlobalUnlock(hGlobal);
                NativeMethods.SetClipboardData(Win32.CF_UNICODETEXT, hGlobal);
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
    }
}
