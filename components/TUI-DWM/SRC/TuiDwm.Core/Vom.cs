using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TuiDwm.Core;

public class Vom
{
    private readonly ConcurrentDictionary<string, object> _store = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string path, object value)
    {
        if (string.IsNullOrEmpty(path)) 
            throw new ArgumentException("Path cannot be null or empty", nameof(path));
        _store[path] = value;
    }

    public T Get<T>(string path, T defaultValue = default!)
    {
        if (_store.TryGetValue(path, out var value))
        {
            try
            {
                if (value is T typedValue)
                    return typedValue;

                // Handle conversions (e.g. double to float, int to double, etc.)
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    public bool TryGet<T>(string path, out T value)
    {
        if (_store.TryGetValue(path, out var rawValue))
        {
            try
            {
                if (rawValue is T typedValue)
                {
                    value = typedValue;
                    return true;
                }
                value = (T)Convert.ChangeType(rawValue, typeof(T));
                return true;
            }
            catch
            {
                value = default!;
                return false;
            }
        }
        value = default!;
        return false;
    }

    public void Delete(string path)
    {
        _store.TryRemove(path, out _);
    }

    public IEnumerable<string> EnumerateKeys(string prefix)
    {
        return _store.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
