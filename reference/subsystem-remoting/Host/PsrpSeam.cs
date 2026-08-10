using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Subsystem.Host;

public static class PsrpSeam
{
    private static readonly object _lock = new();
    private static readonly Lazy<Runspace> _runspace = new(() =>
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        return rs;
    });

    public static Stream Execute(string path, Stream? requestBody)
    {
        string bodyText = "";
        try
        {
            if (requestBody != null)
            {
                if (requestBody.CanSeek) requestBody.Position = 0;
                using var ms = new MemoryStream();
                requestBody.CopyTo(ms);
                bodyText = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            Subsystem.Dg.Log("psrp", $"Execute path={path} bodyLen={bodyText.Length}");
        }
        catch (Exception ex)
        {
            Subsystem.Dg.Log("psrp", $"Execute read error: {ex.Message}");
        }

        var norm = path.Replace('\\', '/').TrimStart('/');

        if (norm.Equals("shell/psrp/session", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("psrp/session", StringComparison.OrdinalIgnoreCase))
        {
            var res = JsonSerializer.Serialize(new { id = "shared" });
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(res));
        }

        if (norm.Equals("shell/psrp/run", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("psrp/run", StringComparison.OrdinalIgnoreCase))
        {
            string script = "";
            try
            {
                if (!string.IsNullOrEmpty(bodyText))
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.TryGetProperty("script", out var sProp))
                        script = sProp.GetString() ?? "";
                }
            }
            catch (Exception ex)
            {
                Subsystem.Dg.Log("psrp", "script parse error: " + ex.Message);
            }

            string text = RunScript(script);
            var res = JsonSerializer.Serialize(new { text });
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(res));
        }

        if (norm.Equals("shell/psrp/invoke", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("psrp/invoke", StringComparison.OrdinalIgnoreCase))
        {
            var data = InvokeCommands(bodyText);
            var res = JsonSerializer.Serialize(new { data });
            return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(res));
        }

        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"unknown endpoint\"}"));
    }

    private static string RunScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return "";
        lock (_lock)
        {
            try
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace.Value;
                ps.AddScript(script);
                var results = ps.Invoke();
                var lines = results.Select(r => r?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s));
                var errs = ps.Streams.Error.Select(e => e?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s));
                var combined = string.Join("\r\n", lines.Concat(errs));
                return combined;
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }
    }

    private static object[] InvokeCommands(string jsonBody)
    {
        if (string.IsNullOrEmpty(jsonBody)) return Array.Empty<object>();
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (doc.RootElement.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
            {
                foreach (var cmd in cmds.EnumerateArray())
                {
                    var name = cmd.GetProperty("name").GetString() ?? "";
                    if (name.Equals("Get-Content", StringComparison.OrdinalIgnoreCase))
                    {
                        var paramsEl = cmd.GetProperty("parameters");
                        string path = paramsEl.GetProperty("LiteralPath").GetString() ?? "";
                        if (File.Exists(path))
                            return new object[] { File.ReadAllText(path) };
                        return new object[] { "" };
                    }
                    if (name.Equals("Set-Content", StringComparison.OrdinalIgnoreCase))
                    {
                        var paramsEl = cmd.GetProperty("parameters");
                        string path = paramsEl.GetProperty("LiteralPath").GetString() ?? "";
                        string val = paramsEl.GetProperty("Value").GetString() ?? "";
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(path, val);
                        return new object[] { true };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Subsystem.Dg.Log("psrp", "invoke parse error: " + ex.Message);
        }
        return Array.Empty<object>();
    }
}
