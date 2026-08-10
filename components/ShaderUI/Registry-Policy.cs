using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Subsystem;

/// <summary>
/// VomRegistry - Thread-safe Windows NT-style Virtual File System Registry.
/// Models keys, folders, and .ini fields (SZ, DWORD, QWORD) matching the architecture specifications.
/// </summary>
public sealed class VomRegistry
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _subKeys = new(StringComparer.OrdinalIgnoreCase);

    public VomRegistry()
    {
        InitializeDefaultVfsTree();
    }

    /// <summary>
    /// Writes a value to the registry. Sets correct parent-child relationship tracking.
    /// </summary>
    public void SetValue(string path, string name, string value)
    {
        string normalizedPath = NormalizePath(path);
        string key = $"{normalizedPath}\\{name}".Trim('\\');
        _values[key] = value;

        // Register subkey path chains
        RegisterSubKeyHierarchy(normalizedPath);
    }

    /// <summary>
    /// Reads a string value (REG_SZ style). Returns fallback if path is absent.
    /// </summary>
    public string GetValue(string path, string name, string defaultValue = "")
    {
        string key = $"{NormalizePath(path)}\\{name}".Trim('\\');
        return _values.TryGetValue(key, out string? val) ? val : defaultValue;
    }

    /// <summary>
    /// Reads a DWORD value (Int32).
    /// </summary>
    public int GetDword(string path, string name, int defaultValue = 0)
    {
        string raw = GetValue(path, name);
        return int.TryParse(raw, out int val) ? val : defaultValue;
    }

    /// <summary>
    /// Reads a QWORD value (Int64).
    /// </summary>
    public long GetQword(string path, string name, long defaultValue = 0L)
    {
        string raw = GetValue(path, name);
        return long.TryParse(raw, out long val) ? val : defaultValue;
    }

    /// <summary>
    /// Returns a list of direct subkeys or folders under a registry path.
    /// </summary>
    public IEnumerable<string> EnumerateSubKeys(string path)
    {
        string normalized = NormalizePath(path);
        return _subKeys.TryGetValue(normalized, out var set) ? set : Array.Empty<string>();
    }

    /// <summary>
    /// Populates registry paths from a bulk standard Windows-style .ini string representation.
    /// </summary>
    public void ImportRegistryScript(string scriptContent)
    {
        if (string.IsNullOrEmpty(scriptContent)) return;

        string currentPath = "";
        using var reader = new StringReader(scriptContent);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.StartsWith(';') || string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentPath = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            int eqIdx = line.IndexOf('=');
            if (eqIdx > 0 && !string.IsNullOrEmpty(currentPath))
            {
                string name = line.Substring(0, eqIdx).Trim().Trim('"');
                string value = line.Substring(eqIdx + 1).Trim().Trim('"');
                SetValue(currentPath, name, value);
            }
        }
    }

    private void RegisterSubKeyHierarchy(string normalizedPath)
    {
        string[] segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string current = "";

        for (int i = 0; i < segments.Length; i++)
        {
            string parent = current;
            current = (i == 0) ? segments[i] : $"{current}\\{segments[i]}";

            if (!string.IsNullOrEmpty(parent))
            {
                var set = _subKeys.GetOrAdd(parent, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (set)
                {
                    set.Add(segments[i]);
                }
            }
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\').Trim('\\');
    }

    private void InitializeDefaultVfsTree()
    {
        // Core Microkernel & Dawn configuration registry
        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "EnableThreadIPC", "1");
        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "StrictHandleChecks", "1");
        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "Quantum", "15");

        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SessionManager", "SubSystemCount", "3");
        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SessionManager", "DefaultHost", "HtmlHost");

        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DawnGraphics", "MeshShaderSupport", "1");
        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DawnGraphics", "ForceWebGPU", "1");
        SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DawnGraphics", "EnableSDF", "1");

        // Object Manager threads & sync parameters
        SetValue(@"HKEY_OBJECT_MANAGER\Handles\ActiveContexts", "MaxHandles", "65536");
        SetValue(@"HKEY_OBJECT_MANAGER\Handles\ActiveContexts", "HandleSize", "8");

        SetValue(@"HKEY_CURRENT_USER\Console\PwshHost", "VirtualTerminalLevel", "1");
        SetValue(@"HKEY_CURRENT_USER\Console\PwshHost", "HistorySize", "1024");

        SetValue(@"HKEY_CURRENT_USER\Software\AppHost", "SingleFileHtmlTarget", "index.html");
        SetValue(@"HKEY_CURRENT_USER\Software\AppHost", "FullScreen", "1");
    }
}
