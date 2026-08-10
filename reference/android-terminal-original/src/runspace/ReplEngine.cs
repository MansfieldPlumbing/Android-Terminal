using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;

namespace TerminalApp;

public class ReplEngine
{
    private readonly TerminalSession _session;
    private readonly AndroidSubsystemHost _host;
    private readonly Runspace _runspace;
    private Thread _replThread = null!;
    private bool _shouldExit = false;
    private readonly List<string> _history = new();
    private PowerShell? _activePowerShell;
    private readonly object _psLock = new object();

    public bool IsRunning => _activePowerShell != null;

    public void StopActiveCommand()
    {
        lock (_psLock)
        {
            try { _activePowerShell?.Stop(); } catch { }
        }
    }

    public ReplEngine(TerminalSession session, AndroidSubsystemHost host, Runspace runspace)
    {
        _session = session; _host = host; _runspace = runspace;
    }

    public void Start()
    {
        _replThread = new Thread(RunLoop) { IsBackground = true, Name = $"PowerShell-REPL-{_session.TabId}" };
        _replThread.Start();
    }

    public void Stop()
    {
        _shouldExit = true;
        lock (_psLock)
        {
            try { _activePowerShell?.Stop(); } catch { }
        }
        try
        {
            var rawUi = (AndroidSubsystemRawUserInterface)_host.UI.RawUI;
            rawUi.InputQueue.Add(new KeyInfo(0, '\0', (ControlKeyStates)0, true));
        }
        catch { }
    }

    private void RunLoop()
    {
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            _session.FeedTerminal(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H"));

            while (!_shouldExit)
            {
                try
                {
                    string pText = "PS> ";
                    try {
                        ps.Commands.Clear(); ps.Streams.ClearStreams();
                        ps.AddScript("if (Test-Path Function:prompt) { (prompt).TrimEnd() } else { \"PS $($PWD.Path)> \" }");
                        var pResult = ps.Invoke();
                        if (pResult != null && pResult.Count > 0 && pResult[0] != null) pText = pResult[0].ToString() ?? "PS> ";
                    } catch { pText = "PS> "; }
                    pText = pText.Replace("\r", "").Replace("\n", "");
                    _session.FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[0m{pText}"));

                    string command = ReadLineWithHistory();
                    if (string.IsNullOrWhiteSpace(command)) continue;

                    if (_history.Count == 0 || _history[^1] != command) _history.Add(command);

                    ps.Commands.Clear(); ps.Streams.ClearStreams();
                    ps.AddScript(command); ps.AddCommand("Out-Default"); 
                    lock (_psLock)
                    {
                        if (_shouldExit) return;
                        _activePowerShell = ps;
                    }
                    try
                    {
                        ps.Invoke();
                    }
                    catch (PipelineStoppedException)
                    {
                        _session.FeedTerminal(Encoding.UTF8.GetBytes("^C\r\n"));
                    }
                    finally
                    {
                        lock (_psLock)
                        {
                            _activePowerShell = null;
                        }
                    }

                    if (ps.HadErrors)
                        foreach (var error in ps.Streams.Error) _session.FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[31m{error}\x1b[0m\r\n"));
                }
                catch (Exception ex) { _session.FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[31mFatal Exec Error: {ex.Message}\x1b[0m\r\n")); }
            }
        }
        finally
        {
            try { _runspace?.Close(); } catch { }
            try { _runspace?.Dispose(); } catch { }
        }
    }

    private string ReadLineWithHistory()
    {
        var rawUi = (AndroidSubsystemRawUserInterface)_host.UI.RawUI;
        var builder = new StringBuilder();
        int historyIndex = _history.Count;
        int cursorIndex = 0;
        string uncommittedBuffer = "";

        while (true)
        {
            var keyInfo = rawUi.ReadKey(ReadKeyOptions.IncludeKeyDown);

            if (keyInfo.Character == '\x03') {
                _session.FeedTerminal(Encoding.UTF8.GetBytes("^C\r\n"));
                builder.Clear();
                return "";
            }
            if (keyInfo.VirtualKeyCode == (int)ConsoleKey.Enter || keyInfo.Character == '\r' || keyInfo.Character == '\n') {
                _session.FeedTerminal(Encoding.UTF8.GetBytes("\r\n"));
                return builder.ToString();
            }
            else if (keyInfo.VirtualKeyCode == (int)ConsoleKey.LeftArrow) {
                if (cursorIndex > 0) { cursorIndex--; _session.FeedTerminal(Encoding.UTF8.GetBytes("\x1b[D")); }
            }
            else if (keyInfo.VirtualKeyCode == (int)ConsoleKey.RightArrow) {
                if (cursorIndex < builder.Length) { cursorIndex++; _session.FeedTerminal(Encoding.UTF8.GetBytes("\x1b[C")); }
            }
            else if (keyInfo.VirtualKeyCode == (int)ConsoleKey.UpArrow) {
                if (historyIndex == 0) continue;
                if (historyIndex == _history.Count) uncommittedBuffer = builder.ToString();
                historyIndex--;
                RedrawInput(builder.Length, cursorIndex, _history[historyIndex], ref builder, out cursorIndex);
            }
            else if (keyInfo.VirtualKeyCode == (int)ConsoleKey.DownArrow) {
                if (historyIndex >= _history.Count) continue;
                historyIndex++;
                string next = historyIndex == _history.Count ? uncommittedBuffer : _history[historyIndex];
                RedrawInput(builder.Length, cursorIndex, next, ref builder, out cursorIndex);
            }
            else if (keyInfo.VirtualKeyCode == (int)ConsoleKey.Backspace || keyInfo.Character == '\b') {
                if (cursorIndex > 0) {
                    int oldLen = builder.Length; int oldCur = cursorIndex;
                    builder.Remove(cursorIndex - 1, 1); cursorIndex--; historyIndex = _history.Count;
                    RedrawInput(oldLen, oldCur, builder.ToString(), ref builder, out cursorIndex, cursorIndex);
                }
            }
            else if (keyInfo.Character != '\0') {
                int oldLen = builder.Length; int oldCur = cursorIndex;
                builder.Insert(cursorIndex, keyInfo.Character); cursorIndex++; historyIndex = _history.Count;
                RedrawInput(oldLen, oldCur, builder.ToString(), ref builder, out cursorIndex, cursorIndex);
            }
        }
    }

    private void RedrawInput(int oldLen, int oldCursor, string newText, ref StringBuilder builder, out int newCursor, int? forcedCursor = null)
    {
        if (oldCursor > 0) _session.FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[{oldCursor}D"));
        _session.FeedTerminal(Encoding.UTF8.GetBytes("\x1b[K")); // Clear right
        string highlighted = AnsiHighlighter.Highlight(newText);
        _session.FeedTerminal(Encoding.UTF8.GetBytes(highlighted));
        
        newCursor = forcedCursor ?? newText.Length;
        int back = newText.Length - newCursor;
        if (back > 0) _session.FeedTerminal(Encoding.UTF8.GetBytes($"\x1b[{back}D"));
        
        builder.Clear(); builder.Append(newText);
    }
}
