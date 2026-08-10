using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TuiDwm.Core;

namespace TuiDwm.Plugins.SysInfo;

public class SysInfoPlugin : ITuiPlugin
{
    private Vom? _vom;
    private string _basePath = "";
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private readonly int[] _memHistory = new int[30];
    private int _historyCount = 0;
    private DateTime _lastUpdate = DateTime.MinValue;

    // Metrics cached to avoid excessive OS calls
    private string _hostName = "";
    private string _osDesc = "";
    private string _runtimeDesc = "";
    private string _uptime = "";
    private string _memMb = "";
    private string _threads = "";

    public Vomt GetTemplate()
    {
        return new Vomt
        {
            PluginName = "SysInfo",
            Entries = new()
            {
                ("\\Title", "System Info"),
                ("\\MinWidth", 35),
                ("\\MinHeight", 10),
                ("\\Focused", false),
                ("\\Visible", true)
            }
        };
    }

    public void Initialize(Vom vom, string basePath)
    {
        _vom = vom;
        _basePath = basePath;
        _hostName = Environment.MachineName;
        _osDesc = RuntimeInformation.OSDescription;
        if (_osDesc.Length > 25) _osDesc = _osDesc[..22] + "...";
        _runtimeDesc = RuntimeInformation.FrameworkDescription;
    }

    public void Render(CellBuffer buffer)
    {
        // Dark blue-gray background (xterm colors)
        buffer.Clear(new Cell(' ', 250, 234, 0));

        // Update metrics once every second to conserve CPU
        if ((DateTime.Now - _lastUpdate).TotalMilliseconds >= 1000)
        {
            _currentProcess.Refresh();
            
            // Calculate uptime
            var uptimeSpan = DateTime.Now - _currentProcess.StartTime;
            _uptime = $"{uptimeSpan.Hours:D2}:{uptimeSpan.Minutes:D2}:{uptimeSpan.Seconds:D2}";
            
            // Memory in MB
            long memBytes = _currentProcess.WorkingSet64;
            double memMbDouble = memBytes / (1024.0 * 1024.0);
            _memMb = $"{memMbDouble:F1} MB";

            // Threads
            _threads = _currentProcess.Threads.Count.ToString();

            // Record to memory history
            int val = (int)Math.Clamp(memMbDouble, 0, 200); // clamp to 200MB max for scaling
            if (_historyCount < _memHistory.Length)
            {
                _memHistory[_historyCount++] = val;
            }
            else
            {
                // Shift left
                Array.Copy(_memHistory, 1, _memHistory, 0, _memHistory.Length - 1);
                _memHistory[^1] = val;
            }

            _lastUpdate = DateTime.Now;
        }

        // Render content
        int y = 0;
        WriteText(buffer, 1, y++, "PROCESS OVERVIEW", 14, 234, 1); // cyan bold
        WriteText(buffer, 1, y++, "───────────────────────────────", 242, 234);

        WriteText(buffer, 1, y, "Host:", 247, 234);
        WriteText(buffer, 12, y++, _hostName, 255, 234, 1);

        WriteText(buffer, 1, y, "OS:", 247, 234);
        WriteText(buffer, 12, y++, _osDesc, 252, 234);

        WriteText(buffer, 1, y, "Runtime:", 247, 234);
        WriteText(buffer, 12, y++, _runtimeDesc, 252, 234);

        WriteText(buffer, 1, y, "Uptime:", 247, 234);
        WriteText(buffer, 12, y++, _uptime, 11, 234); // yellow uptime

        WriteText(buffer, 1, y, "Threads:", 247, 234);
        WriteText(buffer, 12, y++, _threads, 252, 234);

        WriteText(buffer, 1, y, "Memory:", 247, 234);
        WriteText(buffer, 12, y++, _memMb, 10, 234, 1); // green memory

        y++;
        // Render Memory usage sparkline if space permits
        if (buffer.Height >= 11)
        {
            WriteText(buffer, 1, y++, "Memory Activity Graph (0-150MB):", 244, 234);
            
            // Draw visual histogram
            int graphW = Math.Min(buffer.Width - 4, _memHistory.Length);
            int graphH = Math.Max(2, buffer.Height - y - 1);

            int startIdx = Math.Max(0, _historyCount - graphW);
            int drawCount = _historyCount - startIdx;

            // Find min/max in current window for dynamic scaling
            int maxVal = 100; // default ceiling
            for (int i = 0; i < drawCount; i++)
            {
                if (_memHistory[startIdx + i] > maxVal)
                    maxVal = _memHistory[startIdx + i];
            }

            for (int r = 0; r < graphH; r++)
            {
                int py = y + graphH - 1 - r;
                double threshold = ((double)r / graphH) * maxVal;
                
                for (int c = 0; c < drawCount; c++)
                {
                    int px = 2 + c;
                    int val = _memHistory[startIdx + c];

                    if (px < buffer.Width && py < buffer.Height)
                    {
                        ref var cell = ref buffer.At(px, py);
                        if (val > threshold)
                        {
                            cell.Rune = '█';
                            cell.Fg = (byte)(10 + (r % 6)); // gradient colors
                            cell.Bg = 234;
                            cell.Style = 0;
                        }
                    }
                }
            }
        }
    }

    public void HandleInput(ConsoleKeyInfo key)
    {
        // Force refresh on 'R' or 'r'
        if (key.Key == ConsoleKey.R)
        {
            _lastUpdate = DateTime.MinValue;
        }
    }

    public void OnResize(int newWidth, int newHeight)
    {
    }

    private void WriteText(CellBuffer buffer, int x, int y, string text, byte fg, byte bg, byte style = 0)
    {
        if (y < 0 || y >= buffer.Height) return;

        for (int i = 0; i < text.Length; i++)
        {
            int px = x + i;
            if (px >= 0 && px < buffer.Width)
            {
                ref var cell = ref buffer.At(px, y);
                cell.Rune = text[i];
                cell.Fg = fg;
                cell.Bg = bg;
                cell.Style = style;
            }
        }
    }
}
