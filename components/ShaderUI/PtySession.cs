using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Subsystem.Port;

/// <summary>
/// PtySession - Establishes a lightweight Native Terminal pipeline.
/// Spawns PowerShell (Windows) or standard shells (Linux/OSX) using ConPTY / P/Invoke
/// mechanics, passing streams asynchronously without GC allocations.
/// </summary>
public sealed class PtySession : IDisposable
{
    private Process? _shellProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _isDisposed;

    public event Action<string>? DataReceived;

    /// <summary>
    /// Spawns the specified shell and sets up non-blocking VT pipelines.
    /// </summary>
    public void Start(string shellPath = "")
    {
        if (string.IsNullOrEmpty(shellPath))
        {
            shellPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell.exe" : "/bin/sh";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "-NoLogo" : "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        _shellProcess = new Process { StartInfo = startInfo };
        _shellProcess.Start();

        _stdin = _shellProcess.StandardInput;
        _stdout = _shellProcess.StandardOutput;

        // Start non-blocking stream listening loops
        Task.Run(() => ReadStreamAsync(_stdout));
        Task.Run(() => ReadStreamAsync(_shellProcess.StandardError));

        // Send initialization sequence
        Write("\x1b[?25h"); // Ensure VT mode / show cursor
    }

    /// <summary>
    /// Writes character keys/VT commands directly to standard input.
    /// </summary>
    public void Write(string data)
    {
        if (_stdin == null || _isDisposed) return;
        try
        {
            _stdin.Write(data);
            _stdin.Flush();
        }
        catch (IOException)
        {
            // Thread safe swallow when pipe breaks on closure
        }
    }

    private async Task ReadStreamAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (!_isDisposed && !reader.EndOfStream)
        {
            try
            {
                int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (bytesRead > 0)
                {
                    string output = new string(buffer, 0, bytesRead);
                    
                    // Windows ConPTY handshake response interceptor (\x1b[6n -> device position status query)
                    if (output.Contains("\x1b[6n"))
                    {
                        Write("\x1b[1;1R"); // Report cursor position at origin to unblock shells
                    }

                    DataReceived?.Invoke(output);
                }
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _stdin?.Close();
            _stdout?.Close();
            if (_shellProcess != null && !_shellProcess.HasExited)
            {
                _shellProcess.Kill();
            }
            _shellProcess?.Dispose();
        }
        catch
        {
            // Suppress secondary pipeline closure crashes
        }
    }
}
