using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TerminalApp;

public static class HelpSystem
{
    private static Dictionary<string, string>? _helpCache;
    private static readonly object _lock = new();

    public static string GetHelp(string topic)
    {
        lock (_lock)
        {
            if (_helpCache == null)
            {
                _helpCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var assembly = typeof(HelpSystem).Assembly;
                    using var stream = assembly.GetManifestResourceStream("pshelp.json");
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        var json = reader.ReadToEnd();
                        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (data != null)
                        {
                            foreach (var kvp in data)
                            {
                                _helpCache[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    return $"Error loading help system: {ex.Message}";
                }
            }

            if (string.IsNullOrEmpty(topic)) return "Usage: Get-Help <topic>";

            topic = topic.Trim();

            // Try exact match
            if (_helpCache.TryGetValue(topic, out var content)) return content;

            // Try with "about_" prefix
            if (!topic.StartsWith("about_", StringComparison.OrdinalIgnoreCase))
            {
                if (_helpCache.TryGetValue("about_" + topic, out content)) return content;
            }

            // Try matching substring/wildcard (like standard Get-Help search)
            var matches = new List<string>();
            foreach (var key in _helpCache.Keys)
            {
                if (key.Contains(topic, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(key);
                }
            }

            if (matches.Count == 1)
            {
                return _helpCache[matches[0]];
            }
            else if (matches.Count > 1)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Multiple topics match '{topic}':");
                foreach (var m in matches)
                {
                    sb.AppendLine($"  {m}");
                }
                return sb.ToString();
            }

            return $"Help topic '{topic}' not found.";
        }
    }
}
