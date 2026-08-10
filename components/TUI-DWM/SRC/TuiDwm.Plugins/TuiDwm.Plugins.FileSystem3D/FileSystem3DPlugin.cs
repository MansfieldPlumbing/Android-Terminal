using System;
using System.IO;
using System.Collections.Generic;
using TuiDwm.Core;

namespace TuiDwm.Plugins.FileSystem3D;

public class FileSystem3DPlugin : ITuiPlugin
{
    private DateTime _lastScan = DateTime.MinValue;
    private string _rootPath = "C:\\";
    private Vom _vom;
    private string _basePath;

    private int _selectedIndex = 0;
    private List<FileSystemInfo> _currentItems = new();
    private int _columns = 8;
    private bool _needsRender = true;

    public Vomt GetTemplate()
    {
        return new Vomt
        {
            PluginName = "FileSystem3D",
            Entries = new List<(string, object)>
            {
                ("\\RootPath", "C:\\"),
                ("\\Width", 0),
                ("\\Height", 0)
            }
        };
    }

    public void Initialize(Vom vom, string basePath)
    {
        _vom = vom;
        _basePath = basePath;
        RefreshItems();
    }

    public void Render(CellBuffer buffer)
    {
        // Rate limit VOM thrashing. We only redraw if requested.
        if (_needsRender)
        {
            ScanAndMapToVom();
            _needsRender = false;
        }
    }

    public void HandleInput(ConsoleKeyInfo key)
    {
        int maxIndex = _currentItems.Count - 1;
        if (maxIndex < 0) maxIndex = 0;

        switch (key.Key)
        {
            case ConsoleKey.RightArrow:
                if (_selectedIndex < maxIndex) { _selectedIndex++; _needsRender = true; }
                break;
            case ConsoleKey.LeftArrow:
                if (_selectedIndex > 0) { _selectedIndex--; _needsRender = true; }
                break;
            case ConsoleKey.DownArrow:
                if (_selectedIndex + _columns <= maxIndex) { _selectedIndex += _columns; _needsRender = true; }
                else { _selectedIndex = maxIndex; _needsRender = true; }
                break;
            case ConsoleKey.UpArrow:
                if (_selectedIndex - _columns >= 0) { _selectedIndex -= _columns; _needsRender = true; }
                else { _selectedIndex = 0; _needsRender = true; }
                break;
            case ConsoleKey.Enter:
                if (_currentItems.Count > 0)
                {
                    var selected = _currentItems[_selectedIndex];
                    if (selected is DirectoryInfo di)
                    {
                        try { _ = di.GetDirectories(); } catch { break; } // Check access
                        _rootPath = di.FullName;
                        _selectedIndex = 0;
                        RefreshItems();
                        _needsRender = true;
                    }
                    else if (selected is FileInfo fi)
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = fi.FullName,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                }
                break;
            case ConsoleKey.Backspace:
            case ConsoleKey.Escape:
                var parent = Directory.GetParent(_rootPath);
                if (parent != null && parent.Exists)
                {
                    _rootPath = parent.FullName;
                    _selectedIndex = 0;
                    RefreshItems();
                    _needsRender = true;
                }
                break;
        }
    }

    public void OnResize(int newWidth, int newHeight) { }

    private void RefreshItems()
    {
        try
        {
            var dir = new DirectoryInfo(_rootPath);
            _currentItems.Clear();
            _currentItems.AddRange(dir.GetDirectories());
            _currentItems.AddRange(dir.GetFiles());
            if (_selectedIndex >= _currentItems.Count) _selectedIndex = 0;
        }
        catch
        {
            _currentItems.Clear();
            _selectedIndex = 0;
        }
    }

    private void ScanAndMapToVom()
    {
        int elementIndex = 0;
        float startX = 0f;
        float startY = 0f;
        
        // 1. Draw Root Platform
        string spatialPath = $"\\Windows\\FS3D_{elementIndex}";
        _vom.Set($"{spatialPath}\\X", startX);
        _vom.Set($"{spatialPath}\\Y", startY);
        _vom.Set($"{spatialPath}\\Width", 40.0f);
        _vom.Set($"{spatialPath}\\Height", 20.0f);
        _vom.Set($"{spatialPath}\\TargetX", startX);
        _vom.Set($"{spatialPath}\\TargetY", startY);
        _vom.Set($"{spatialPath}\\TargetW", 40.0f);
        _vom.Set($"{spatialPath}\\TargetH", 20.0f);
        _vom.Set($"{spatialPath}\\Z", 0.1f);
        _vom.Set($"{spatialPath}\\ElementType", 2.0f); // Frosted Glass Platform
        _vom.Set($"{spatialPath}\\Visible", true);
        elementIndex++;

        // 2. Draw Files and Dirs as blocks on the platform
        int col = 0;
        int row = 0;
        for (int i = 0; i < _currentItems.Count; i++)
        {
            var item = _currentItems[i];
            spatialPath = $"\\Windows\\FS3D_{elementIndex}";
            
            float fileX = startX + 2.0f + (col * 4.0f);
            float fileY = startY + 2.0f + (row * 3.0f);
            
            float colorId = item switch
            {
                DirectoryInfo => 0.4f, // Distinct color for directories
                FileInfo fi => GetColorForExtension(fi.Extension),
                _ => 0.0f
            };

            bool isSelected = (i == _selectedIndex);
            float targetZ = isSelected ? 0.5f : 0.15f; // Pop up if selected
            float scale = isSelected ? 3.5f : 3.0f; // Slightly wider if selected
            
            _vom.Set($"{spatialPath}\\X", fileX);
            _vom.Set($"{spatialPath}\\Y", fileY);
            _vom.Set($"{spatialPath}\\Width", scale);
            _vom.Set($"{spatialPath}\\Height", 2.0f);
            _vom.Set($"{spatialPath}\\TargetX", fileX);
            _vom.Set($"{spatialPath}\\TargetY", fileY);
            _vom.Set($"{spatialPath}\\TargetW", scale);
            _vom.Set($"{spatialPath}\\TargetH", 2.0f);
            _vom.Set($"{spatialPath}\\Z", _vom.Get<float>($"{spatialPath}\\Z", targetZ)); // Interpolate to TargetZ
            _vom.Set($"{spatialPath}\\TargetZSlot", (int)(targetZ * 100)); // HACK to reuse kinematics slot logic or just update Z
            
            // Actually just update Z directly for immediate kinematic effect via VOM
            _vom.Set($"{spatialPath}\\Z", targetZ);
            
            _vom.Set($"{spatialPath}\\ElementType", 1.0f); // Solid Block
            _vom.Set($"{spatialPath}\\ColorId", colorId);
            _vom.Set($"{spatialPath}\\Visible", true);
            
            elementIndex++;
            col++;
            if (col >= _columns) { col = 0; row++; }
            if (elementIndex > 200) break; // Hard cap
        }

        // Cleanup old items
        int oldCount = _vom.Get<int>($"{_basePath}\\ElementCount", 0);
        for (int i = elementIndex; i < oldCount; i++)
        {
            _vom.Delete($"\\Windows\\FS3D_{i}");
        }
        
        _vom.Set($"{_basePath}\\ElementCount", elementIndex);
    }

    private float GetColorForExtension(string ext)
    {
        ext = ext.ToLower();
        return ext switch
        {
            ".cs" => 0.6f,   // Blue-ish
            ".ps1" => 0.3f,  // Green-ish
            ".md" => 0.1f,   // Yellow-ish
            ".json" => 0.8f, // Purple-ish
            ".exe" => 1.0f,  // Red-ish
            _ => 0.0f        // Default
        };
    }
}
