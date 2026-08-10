using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Provider;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Language;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using NativePwshConsole.Surface;

namespace NativePwshConsole;

internal sealed class PowerShellSession : IDisposable
{
    public const string EngineVersion = "7.7.0-preview.2";
    private static readonly object ResolverGate = new();
    private static bool _resolverInstalled;
    private readonly Runspace _runspace;
    private readonly NativeHost _host;
    private readonly object _gate = new();
    private PowerShell? _active;
    public event Action<string>? Output;
    public IReadOnlyList<string> StartupDiagnostics => _startupDiagnostics;
    private readonly List<string> _startupDiagnostics = [];

    public PowerShellSession(string home, string scriptDirectory)
    {
        EnsureNativeResolver();

        var iss = InitialSessionState.Create();
        iss.LanguageMode = PSLanguageMode.FullLanguage;
        LoadCommands(iss, typeof(PSObject).Assembly);
        LoadCommands(iss, Assembly.Load("Microsoft.PowerShell.Commands.Utility"));
        LoadCommands(iss, Assembly.Load("Microsoft.PowerShell.Commands.Management"));
        LoadCommands(iss, typeof(PowerShellSession).Assembly);
        LoadMvpSurface(iss);
        LoadScriptFunctions(iss, scriptDirectory);
        _host = new NativeHost(s => Output?.Invoke(s));
        _runspace = RunspaceFactory.CreateRunspace(_host, iss);
        _runspace.Open();
        string profile = Path.Combine(home, "profile.ps1");
        if (!File.Exists(profile))
            File.WriteAllText(profile, "# Terminal PowerShell profile\n# This file runs when the private CoreCLR session starts.\n\n# Example:\n# Set-Alias ll Get-ChildItem\n");
        Invoke($"$env:HOME='{Q(home)}'; $env:POWERSHELL_TELEMETRY_OPTOUT='1'; $global:PROFILE='{Q(profile)}'; " +
            "$null=New-PSDrive -Name Home -PSProvider FileSystem -Root $env:HOME -Scope Global; Set-Location 'Home:\\'", false);
        Invoke("Update-TypeData -TypeName 'NativePwshConsole.AdbLoopbackStatus' -DefaultDisplayPropertySet Connected,Authority,ConnectPort -Force", false);
        LoadProfile(profile);
    }

    private static void EnsureNativeResolver()
    {
        lock (ResolverGate)
        {
            if (_resolverInstalled) return;
            NativeLibrary.SetDllImportResolver(typeof(PowerShell).Assembly, (name, assembly, path) =>
                name.Contains("libpsl-native", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load("libpsl-android.so", assembly, null)
                    : IntPtr.Zero);
            _resolverInstalled = true;
        }
    }

    private static void LoadMvpSurface(InitialSessionState iss)
    {
        (string Alias, string Target)[] aliases =
        {
            ("ls", "Get-ChildItem"), ("gci", "Get-ChildItem"),
            ("cat", "Get-Content"), ("gc", "Get-Content"), ("pwd", "Get-Location"),
            ("sl", "Set-Location"), ("ps", "Get-Process"),
            ("gps", "Get-Process"), ("gcm", "Get-Command"), ("which", "Get-Command"),
            ("select", "Select-Object"), ("where", "Where-Object"), ("sort", "Sort-Object"),
            ("measure", "Measure-Object"), ("grep", "Select-String"), ("sls", "Select-String"),
            ("copy", "Copy-Item"), ("cp", "Copy-Item"), ("move", "Move-Item"), ("mv", "Move-Item"),
            ("del", "Remove-Item"), ("erase", "Remove-Item"), ("ren", "Rename-Item"),
            ("type", "Get-Content"), ("cls", "Clear-Host"), ("edit", "Edit-File")
        };
        foreach (var pair in aliases)
            iss.Commands.Add(new SessionStateAliasEntry(pair.Alias, pair.Target, "Native console MVP alias"));

        iss.Commands.Add(new SessionStateFunctionEntry("cd", "param([string]$Path); if ([string]::IsNullOrWhiteSpace($Path)) { Set-Location $env:HOME } else { Set-Location -LiteralPath $Path }"));
        iss.Commands.Add(new SessionStateFunctionEntry("cd..", "Set-Location -LiteralPath .."));
        iss.Commands.Add(new SessionStateFunctionEntry("cd\\", "$root=[IO.Path]::GetPathRoot((Get-Location).Path); Set-Location -LiteralPath $root"));
        iss.Commands.Add(new SessionStateFunctionEntry("dir", """
param([string]$Path='.')
$resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
$displayPath = $resolved.Replace('/', '\')
''
"Directory of $displayPath"
''
Get-ChildItem -LiteralPath $resolved | ForEach-Object {
  $stamp = $_.LastWriteTime.ToString('MM/dd/yy h:mmtt')
  $size = if ($_.PSIsContainer) { '<DIR>' }
    elseif ($_.Length -ge 1GB) { '{0:0.#}GB' -f ($_.Length / 1GB) }
    elseif ($_.Length -ge 1MB) { '{0:0.#}MB' -f ($_.Length / 1MB) }
    elseif ($_.Length -ge 1KB) { '{0:0.#}KB' -f ($_.Length / 1KB) }
    else { "$($_.Length)B" }
  '{0} {1,7} {2}' -f $stamp,$size,$_.Name
}
"""));
        iss.Commands.Add(new SessionStateFunctionEntry("deltree", """
param([Parameter(Mandatory=$true,Position=0)][string]$Path,[switch]$Force)
$target = [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
$root = [IO.Path]::GetPathRoot($target)
$home = [IO.Path]::GetFullPath($env:HOME).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($target.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $root.TrimEnd([IO.Path]::DirectorySeparatorChar) -or
    $target.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $home) {
  throw "Refusing to recursively delete protected path: $target"
}
if (-not (Test-Path -LiteralPath $target)) { throw "Path does not exist: $target" }
if (-not $Force) {
  Write-Warning "Are you sure? Recursive delete target: $target"
  Write-Output "Review it, then run: deltree '$($target.Replace("'","''"))' -Force"
  return
}
Remove-Item -LiteralPath $target -Recurse -Force
"""));

        iss.Commands.Add(new SessionStateFunctionEntry("Invoke-Vibration",
            "param([ValidateRange(1,10000)][int]$Duration=200); [NativePwshConsole.AndroidBridge]::Vibrate($Duration)"));
        iss.Commands.Add(new SessionStateAliasEntry("vibe", "Invoke-Vibration", "Vibrate the phone"));
        iss.Commands.Add(new SessionStateFunctionEntry("Set-Flashlight",
            "param([ValidateSet('On','Off','Toggle')][string]$State='Toggle'); [NativePwshConsole.AndroidBridge]::SetFlashlight($State); Write-Output \"Flashlight: $State\""));
        iss.Commands.Add(new SessionStateAliasEntry("torch", "Set-Flashlight", "Control the flashlight"));
        iss.Commands.Add(new SessionStateFunctionEntry("ipconfig", """
$rows = foreach($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
  $ips = $nic.GetIPProperties().UnicastAddresses | ForEach-Object Address | Where-Object { $_.AddressFamily -in 'InterNetwork','InterNetworkV6' }
  if($ips) { [pscustomobject]@{ Adapter=$nic.Name; Status=$nic.OperationalStatus; Address=($ips -join ', ') } }
}
$rows | ForEach-Object { "`n$($_.Adapter) [$($_.Status)]`n  $($_.Address)" }
"""));
        iss.Commands.Add(new SessionStateFunctionEntry("ping", """
param([Parameter(Mandatory,Position=0)][string]$Target,[int]$Count=4,[int]$Timeout=3000)
$p=[Net.NetworkInformation.Ping]::new()
try { 1..$Count | ForEach-Object { try { $r=$p.Send($Target,$Timeout); "Reply from $($r.Address): time=$($r.RoundtripTime)ms status=$($r.Status)" } catch { Write-Error $_ } } } finally { $p.Dispose() }
"""));
        iss.Commands.Add(new SessionStateFunctionEntry("Test-Port", """
param([Parameter(Mandatory,Position=0)][string]$HostName,[Parameter(Mandatory,Position=1)][int]$Port,[int]$Timeout=2000)
$c=[Net.Sockets.TcpClient]::new(); try { $t=$c.ConnectAsync($HostName,$Port); $ok=$t.Wait($Timeout) -and $c.Connected; [pscustomobject]@{Host=$HostName;Port=$Port;Open=$ok} } catch { [pscustomobject]@{Host=$HostName;Port=$Port;Open=$false;Error=$_.Exception.Message} } finally {$c.Dispose()}
"""));
        iss.Commands.Add(new SessionStateFunctionEntry("Get-AdbStatus", "Get-AdbLoopback"));
        iss.Commands.Add(new SessionStateAliasEntry("adbstatus", "Get-AdbStatus", "Show Android debugging state"));
        iss.Commands.Add(new SessionStateFunctionEntry("adb", """
param([Parameter(Position=0)][string]$Verb='devices',[Parameter(ValueFromRemainingArguments=$true)][string[]]$ArgumentList)
switch ($Verb.ToLowerInvariant()) {
  'devices' { Get-AdbLoopback; break }
  'status' { Get-AdbLoopback; break }
  'pair' { Start-AdbPairing; break }
  'disconnect' { Clear-AdbPairing -Force; 'Self-ADB key forgotten locally.'; break }
  'shell' {
    if (-not $ArgumentList -or $ArgumentList.Count -eq 0) {
      throw 'This console does not emulate an interactive adb shell. Use: adb shell <command>'
    }
    Invoke-AdbShell ($ArgumentList -join ' ')
    break
  }
  default { throw "Unsupported adb verb '$Verb'. Try: adb devices | adb pair | adb shell <command> | adb disconnect" }
}
"""));
        iss.Commands.Add(new SessionStateFunctionEntry("Request-FileManagerAccess", "[NativePwshConsole.AndroidBridge]::RequestFileManagerAccess()"));
        iss.Commands.Add(new SessionStateAliasEntry("settings", "Show-ConsoleSettings", "Open native console settings"));
        iss.Commands.Add(new SessionStateFunctionEntry("Get-NativeCommand", """
@(
 [pscustomobject]@{ Command='dir'; Aliases=''; Tier='Windows'; Example='dir' }
 [pscustomobject]@{ Command='Get-ChildItem'; Aliases='ls, gci'; Tier='Core'; Example='ls' }
 [pscustomobject]@{ Command='Get-Process'; Aliases='ps, gps'; Tier='Core'; Example='ps | select -First 10' }
 [pscustomobject]@{ Command='Get-Date'; Aliases=''; Tier='Core'; Example='Get-Date' }
 [pscustomobject]@{ Command='Get-Command'; Aliases='gcm, which'; Tier='Core'; Example='gcm *Process*' }
 [pscustomobject]@{ Command='Invoke-Vibration'; Aliases='vibe'; Tier='Android'; Example='vibe 100' }
 [pscustomobject]@{ Command='Set-Flashlight'; Aliases='torch'; Tier='Android'; Example='torch Toggle' }
 [pscustomobject]@{ Command='ipconfig'; Aliases=''; Tier='Network'; Example='ipconfig' }
 [pscustomobject]@{ Command='ping'; Aliases=''; Tier='Network'; Example='ping 8.8.8.8' }
 [pscustomobject]@{ Command='Test-Port'; Aliases=''; Tier='Network'; Example='Test-Port localhost 8080' }
 [pscustomobject]@{ Command='Get-AdbStatus'; Aliases='adbstatus'; Tier='Android'; Example='adbstatus' }
 [pscustomobject]@{ Command='adb'; Aliases=''; Tier='Android'; Example='adb shell id' }
 [pscustomobject]@{ Command='Request-FileManagerAccess'; Aliases=''; Tier='Android'; Example='Request-FileManagerAccess' }
 [pscustomobject]@{ Command='Test-NativeParsers'; Aliases=''; Tier='Diagnostic'; Example='Test-NativeParsers' }
 [pscustomobject]@{ Command='Get-NativeCommand'; Aliases='cmds'; Tier='Discover'; Example='cmds' }
)
"""));
        iss.Commands.Add(new SessionStateFunctionEntry("cmds", "Get-NativeCommand | ForEach-Object { '{0,-10} {1,-22} {2}' -f $_.Tier,$_.Command,$_.Aliases }"));
        iss.Commands.Add(new SessionStateFunctionEntry("Get-NativeHelp", """
param([string]$Name)
if (-not $Name) {
  Write-Host 'NATIVE POWERSHELL FOR ANDROID' -ForegroundColor Cyan
  Write-Host 'PowerShell objects, Android capabilities, no Linux container.' -ForegroundColor DarkGray
  ''
  'help <name>'
  '  Command details and parameters.'
  'cmds'
  '  Concise command catalogue.'
  'dir'
  '  Windows-shaped directory listing.'
  'ipconfig / ping <host>'
  '  Network configuration and reachability.'
  'Test-Port <host> <port>'
  '  Test a TCP service.'
  'adbstatus'
  '  USB and wireless debugging state.'
  'Request-FileManagerAccess'
  '  Open Android file access settings.'
  ''
  Write-Host 'Touch: swipe scrollback • pinch font size' -ForegroundColor Yellow
  Write-Host 'Examples: dir; ps | select -First 10; vibe 100; torch Toggle' -ForegroundColor Green
  return
}
$known = Get-NativeCommand | Where-Object Command -EQ $Name | Select-Object -First 1
if ($known) { $known | Format-List Command,Aliases,Tier,Example }
$command = Microsoft.PowerShell.Core\Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $command) { Write-Error "No command named '$Name' is loaded."; return }
$command | Select-Object Name,CommandType,ModuleName,@{n='Parameters';e={($_.Parameters.Keys | Sort-Object) -join ', '}} | Format-List
"""));
        iss.Commands.Add(new SessionStateFunctionEntry("Get-Help", "param([string]$Name); Get-NativeHelp $Name"));
        iss.Commands.Add(new SessionStateAliasEntry("help", "Get-NativeHelp", "Native offline help"));
        iss.Commands.Add(new SessionStateAliasEntry("man", "Get-NativeHelp", "Native offline help"));
        iss.Commands.Add(new SessionStateFunctionEntry("Start-TerminalHardpoint", """
param([Parameter(Mandatory=$true,Position=0)][string]$Id)
$hardpoint = Get-TerminalHardpoint -Id $Id
$global:UI = Show-TerminalHardpoint -Id $Id
. $hardpoint.ScriptPath
"""));
    }

    private void LoadProfile(string profile)
    {
        lock (_gate)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            string body = File.ReadAllText(profile);
            string directory = Path.GetDirectoryName(profile) ?? string.Empty;
            ps.AddScript($"$PSScriptRoot='{Q(directory)}'; $PSCommandPath='{Q(profile)}';\n{body}", useLocalScope: false);
            try { ps.Invoke(); }
            catch (Exception ex) { _startupDiagnostics.Add($"PROFILE ERROR: {ex.Message}"); }
            foreach (var error in ps.Streams.Error)
                _startupDiagnostics.Add($"PROFILE ERROR: {error}");
        }
    }

    private static string Q(string value) => value.Replace("'", "''");

    private static void LoadScriptFunctions(InitialSessionState iss, string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory, "ConvertFrom-*.ps1"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            iss.Commands.Add(new SessionStateFunctionEntry(name, File.ReadAllText(path)));
        }

        string testPath = Path.Combine(directory, "Test-Parsers.ps1");
        if (File.Exists(testPath))
        {
            string testBody = File.ReadAllText(testPath);
            int assertions = testBody.IndexOf("# Helper assert function", StringComparison.Ordinal);
            if (assertions >= 0) testBody = testBody[assertions..];
            iss.Commands.Add(new SessionStateFunctionEntry("Test-NativeParsers", testBody));
        }
    }

    private static void LoadCommands(InitialSessionState iss, Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes())
        {
            var cmdlet = type.GetCustomAttribute<CmdletAttribute>();
            if (cmdlet != null && !type.IsAbstract)
                iss.Commands.Add(new SessionStateCmdletEntry($"{cmdlet.VerbName}-{cmdlet.NounName}", type, null));
            var provider = type.GetCustomAttribute<CmdletProviderAttribute>();
            if (provider != null && !type.IsAbstract)
                iss.Providers.Add(new SessionStateProviderEntry(provider.ProviderName, type, null));
        }
    }

    public Task ExecuteAsync(string command) => Task.Run(() =>
    {
        command = command.Trim();
        Invoke(command, true);
    });

    public string Highlight(string code)
    {
        return code;
    }

    public Task<string> GetPromptAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript("$PWD.Path");
            string path = ps.Invoke().FirstOrDefault()?.ToString() ?? "?";
            path = path.Replace('/', '\\');
            if (path.Equals("Home:\\", StringComparison.OrdinalIgnoreCase)) path = "Home:";
            return $"PS {path}> ";
        }
    });

    public void SetWindowSize(int columns, int rows)
    {
        if (_host.UI.RawUI is NativeRawUi raw)
        {
            raw.WindowSize = new System.Management.Automation.Host.Size(Math.Max(20, columns), Math.Max(5, rows));
            raw.BufferSize = new System.Management.Automation.Host.Size(Math.Max(20, columns), Math.Max(2000, rows));
        }
    }

    public ConsoleSettings LoadSettings(string path)
    {
        lock (_gate)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript($". '{path.Replace("'", "''")}'; [pscustomobject]$NativeConsoleSettings");
            var values = ps.Invoke();
            if (ps.HadErrors || values.Count == 0) return new ConsoleSettings();
            PSObject value = values[0];
            return new ConsoleSettings
            {
                Background = ReadColor(value, "Background", "#012456"),
                Foreground = ReadColor(value, "Foreground", "#F5F5F5"),
                InputBackground = ReadColor(value, "InputBackground", "#012456"),
                InputForeground = ReadColor(value, "InputForeground", "#FFFFFF"),
                HintForeground = ReadColor(value, "HintForeground", "#808890"),
                Prompt = Read(value, "Prompt", "PS> "),
                FontSize = ReadInt(value, "FontSize", 14, 10, 32),
                Scrollback = ReadInt(value, "Scrollback", 2000, 100, 20000),
                AllowDragons = ReadBool(value, "AllowDragons", false)
            };
        }
    }

    private static string Read(PSObject value, string name, string fallback)
        => value.Properties[name]?.Value?.ToString() ?? fallback;

    private static string ReadColor(PSObject value, string name, string fallback)
    {
        string candidate = Read(value, name, fallback);
        try { _ = Android.Graphics.Color.ParseColor(candidate); return candidate; }
        catch (ArgumentException) { return fallback; }
    }

    private static int ReadInt(PSObject value, string name, int fallback, int min, int max)
        => int.TryParse(value.Properties[name]?.Value?.ToString(), out int result)
            ? Math.Clamp(result, min, max) : fallback;

    private static bool ReadBool(PSObject value, string name, bool fallback)
        => bool.TryParse(value.Properties[name]?.Value?.ToString(), out bool result) ? result : fallback;

    private void Invoke(string command, bool display)
    {
        lock (_gate)
        {
            using var ps = PowerShell.Create();
            _active = ps;
            ps.Runspace = _runspace;
            ps.AddScript(command);
            if (display) ps.AddCommand("Out-Default");
            try
            {
                ps.Invoke();
                foreach (var error in ps.Streams.Error) Output?.Invoke($"\r\n\x1b[91mERROR: {error}\x1b[0m\r\n");
            }
            catch (PipelineStoppedException) { Output?.Invoke("^C\r\n"); }
            catch (Exception ex) { Output?.Invoke($"\r\n\x1b[91mERROR: {ex.Message}\x1b[0m\r\n"); }
            finally { _active = null; }
        }
    }

    public void Stop() { lock (_gate) _active?.Stop(); }

    internal void InvokeSurfaceHandler(ScriptBlock handler, SurfaceEvent surfaceEvent)
    {
        lock (_gate)
        {
            using var ps = PowerShell.Create();
            _active = ps;
            ps.Runspace = _runspace;
            ps.AddCommand("Invoke-Command")
                .AddParameter("ScriptBlock", handler)
                .AddParameter("ArgumentList", new object?[] { surfaceEvent });
            try
            {
                ps.Invoke();
                foreach (ErrorRecord error in ps.Streams.Error)
                    Output?.Invoke($"\r\n\x1b[91mSURFACE ERROR: {error}\x1b[0m\r\n");
            }
            catch (PipelineStoppedException) { }
            catch (Exception error)
            {
                Output?.Invoke($"\r\n\x1b[91mSURFACE ERROR: {error.Message}\x1b[0m\r\n");
            }
            finally { _active = null; }
        }
    }

    public void Dispose() { _active?.Dispose(); _runspace.Dispose(); }
}

internal sealed class ConsoleSettings
{
    public string Background { get; set; } = "#012456";
    public string Foreground { get; set; } = "#F5F5F5";
    public string InputBackground { get; set; } = "#012456";
    public string InputForeground { get; set; } = "#FFFFFF";
    public string HintForeground { get; set; } = "#808890";
    public float FontSize { get; set; } = 14;
    public int Scrollback { get; set; } = 2000;
    public string Prompt { get; set; } = "PS> ";
    public bool AllowDragons { get; set; }
}

internal sealed class NativeHost : PSHost
{
    private readonly NativeHostUi _ui;
    private readonly Guid _id = Guid.NewGuid();
    public NativeHost(Action<string> write) => _ui = new NativeHostUi(write);
    public override string Name => "NativePwshConsole";
    public override Version Version => new(0, 1);
    public override Guid InstanceId => _id;
    public override PSHostUserInterface UI => _ui;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override void SetShouldExit(int exitCode) { }
    public override void EnterNestedPrompt() => throw new NotSupportedException();
    public override void ExitNestedPrompt() => throw new NotSupportedException();
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
}

internal sealed class NativeHostUi : PSHostUserInterface
{
    private readonly Action<string> _write;
    private readonly NativeRawUi _raw = new();
    public NativeHostUi(Action<string> write) => _write = write;
    public override PSHostRawUserInterface RawUI => _raw;
    public override void Write(string value) => _write(NormalizeNewlines(value));
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value)
        => _write($"\x1b[{AnsiForeground(foregroundColor)};{AnsiBackground(backgroundColor)}m{NormalizeNewlines(value)}\x1b[0m");
    public override void WriteLine(string value) => _write(NormalizeNewlines(value) + "\r\n");
    public override void WriteErrorLine(string value) => _write("\x1b[91mERROR: " + NormalizeNewlines(value) + "\x1b[0m\r\n");
    public override void WriteDebugLine(string message) => _write("\x1b[90mDEBUG: " + NormalizeNewlines(message) + "\x1b[0m\r\n");
    public override void WriteVerboseLine(string message) => _write("\x1b[90mVERBOSE: " + NormalizeNewlines(message) + "\x1b[0m\r\n");
    public override void WriteWarningLine(string message) => _write("\x1b[93mWARNING: " + NormalizeNewlines(message) + "\x1b[0m\r\n");
    public override void WriteProgress(long sourceId, ProgressRecord record) { }
    public override string ReadLine() => string.Empty;
    public override SecureString ReadLineAsSecureString() => new();
    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) => new();
    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) => defaultChoice;
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) => throw new NotSupportedException();
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => throw new NotSupportedException();

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static int AnsiForeground(ConsoleColor c) => c switch
    {
        ConsoleColor.Black => 30, ConsoleColor.DarkRed => 31, ConsoleColor.DarkGreen => 32,
        ConsoleColor.DarkYellow => 33, ConsoleColor.DarkBlue => 34, ConsoleColor.DarkMagenta => 35,
        ConsoleColor.DarkCyan => 36, ConsoleColor.Gray => 37, ConsoleColor.DarkGray => 90,
        ConsoleColor.Red => 91, ConsoleColor.Green => 92, ConsoleColor.Yellow => 93,
        ConsoleColor.Blue => 94, ConsoleColor.Magenta => 95, ConsoleColor.Cyan => 96,
        _ => 97
    };

    private static int AnsiBackground(ConsoleColor c) => c switch
    {
        ConsoleColor.Black => 40, ConsoleColor.DarkRed => 41, ConsoleColor.DarkGreen => 42,
        ConsoleColor.DarkYellow => 43, ConsoleColor.DarkBlue => 44, ConsoleColor.DarkMagenta => 45,
        ConsoleColor.DarkCyan => 46, ConsoleColor.Gray => 47, ConsoleColor.DarkGray => 100,
        ConsoleColor.Red => 101, ConsoleColor.Green => 102, ConsoleColor.Yellow => 103,
        ConsoleColor.Blue => 104, ConsoleColor.Magenta => 105, ConsoleColor.Cyan => 106,
        _ => 107
    };
}

internal sealed class NativeRawUi : PSHostRawUserInterface
{
    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
    public override Coordinates CursorPosition { get; set; }
    public override Coordinates WindowPosition { get; set; }
    public override int CursorSize { get; set; } = 1;
    public override System.Management.Automation.Host.Size BufferSize { get; set; } = new(120, 2000);
    public override System.Management.Automation.Host.Size WindowSize { get; set; } = new(120, 40);
    public override System.Management.Automation.Host.Size MaxWindowSize => new(240, 100);
    public override System.Management.Automation.Host.Size MaxPhysicalWindowSize => new(240, 100);
    public override string WindowTitle { get; set; } = "Native PowerShell";
    public override bool KeyAvailable => false;
    public override KeyInfo ReadKey(ReadKeyOptions options) => default;
    public override void FlushInputBuffer() { }
    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
    public override void SetBufferContents(System.Management.Automation.Host.Rectangle rectangle, BufferCell fill) { }
    public override BufferCell[,] GetBufferContents(System.Management.Automation.Host.Rectangle rectangle) => new BufferCell[0, 0];
    public override void ScrollBufferContents(System.Management.Automation.Host.Rectangle source, Coordinates destination, System.Management.Automation.Host.Rectangle clip, BufferCell fill) { }
}
