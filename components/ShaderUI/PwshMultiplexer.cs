using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;
using TuiDwm.Core;

namespace TuiDwm.Port;

/// <summary>
/// Thread-safe, in-memory RunspacePool manager for PowerShell.
/// Pre-warms runspaces and executes logical, stateless commands asynchronously,
/// completely bypassing native process spawning (System.Diagnostics.Process).
/// </summary>
public sealed class PwshMultiplexer : IDisposable
{
    private static readonly Lazy<PwshMultiplexer> _instance = new(() => new PwshMultiplexer());
    public static PwshMultiplexer Instance => _instance.Value;

    public static Vom? VomInstance { get; set; }

    private RunspacePool? _runspacePool;
    private readonly object _lock = new();
    private bool _isInitialized;

    private PwshMultiplexer() { }

    public void Initialize(Vom vom, int poolSize = 8)
    {
        lock (_lock)
        {
            if (_isInitialized) return;

            VomInstance = vom;

            var iss = InitialSessionState.CreateDefault();
            
            // Register all 24 custom cmdlets to our pre-warmed pool
            RegisterCustomCmdlets(iss);

            _runspacePool = RunspaceFactory.CreateRunspacePool(1, poolSize, iss);
            _runspacePool.ThreadOptions = RunspacePoolThreadOptions.UseCurrentThread;
            _runspacePool.Open();

            _isInitialized = true;
        }
    }

    /// <summary>
    /// Executes a PowerShell command inside the pre-warmed pool and returns the result string.
    /// </summary>
    public async Task<string> ExecuteCommandAsync(string command)
    {
        if (!_isInitialized || _runspacePool == null)
            return "Error: PowerShell Multiplexer not initialized.\r\n";

        using var ps = PowerShell.Create();
        ps.RunspacePool = _runspacePool;
        ps.AddScript(command);
        ps.AddCommand("Out-String"); // Formats object stream to standard terminal text

        try
        {
            var results = await Task.Factory.FromAsync(
                ps.BeginInvoke(),
                ps.EndInvoke
            ).ConfigureAwait(false);

            if (ps.HadErrors)
            {
                var errors = new System.Text.StringBuilder();
                foreach (var err in ps.Streams.Error)
                {
                    errors.AppendLine(err.ToString());
                }
                return errors.ToString();
            }

            var output = new System.Text.StringBuilder();
            foreach (var obj in results)
            {
                if (obj != null)
                {
                    output.Append(obj.ToString());
                }
            }
            return output.ToString();
        }
        catch (Exception ex)
        {
            return $"Execution Error: {ex.Message}\r\n";
        }
    }

    private void RegisterCustomCmdlets(InitialSessionState iss)
    {
        var cmdlets = new List<SessionStateCmdletEntry>
        {
            new("Get-Vom", typeof(GetVomCmdlet), null),
            new("Set-Vom", typeof(SetVomCmdlet), null),
            new("New-VomWindow", typeof(NewVomWindowCmdlet), null),
            new("Remove-VomWindow", typeof(RemoveVomWindowCmdlet), null),
            new("Get-DwmState", typeof(GetDwmStateCmdlet), null),
            new("Set-DwmState", typeof(SetDwmStateCmdlet), null),
            new("Invoke-DwmAction", typeof(InvokeDwmActionCmdlet), null),
            new("Update-DwmLayout", typeof(UpdateDwmLayoutCmdlet), null),
            new("Show-DesktopIcon", typeof(ShowDesktopIconCmdlet), null),
            new("Hide-DesktopIcon", typeof(HideDesktopIconCmdlet), null),
            new("Refresh-Dwm", typeof(RefreshDwmCmdlet), null),
            new("Get-PerformanceData", typeof(GetPerformanceDataCmdlet), null),
            new("Set-PerformanceData", typeof(SetPerformanceDataCmdlet), null),
            new("Get-TelemetryConfig", typeof(GetTelemetryConfigCmdlet), null),
            new("Set-TelemetryConfig", typeof(SetTelemetryConfigCmdlet), null),
            new("New-ContextSubMenu", typeof(NewContextSubMenuCmdlet), null),
            new("Get-ActiveContexts", typeof(GetActiveContextsCmdlet), null),
            new("New-Fence", typeof(NewFenceCmdlet), null),
            new("Signal-Fence", typeof(SignalFenceCmdlet), null),
            new("Get-Mailbox", typeof(GetMailboxCmdlet), null),
            new("Send-MailboxMessage", typeof(SendMailboxMessageCmdlet), null),
            new("Receive-MailboxMessage", typeof(ReceiveMailboxMessageCmdlet), null),
            new("Get-ThreadState", typeof(GetThreadStateCmdlet), null),
            new("Set-ThreadState", typeof(SetThreadStateCmdlet), null)
        };

        foreach (var entry in cmdlets)
        {
            iss.Commands.Add(entry);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _runspacePool?.Dispose();
            _runspacePool = null;
            _isInitialized = false;
        }
    }
}

// =============================================================================
//                   THE 24 STATELESS INTEROP CUSTOM CMDLETS
// =============================================================================

[Cmdlet(VerbsCommon.Get, "Vom")]
public sealed class GetVomCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string Path { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var val = PwshMultiplexer.VomInstance?.Get<object>(Path);
        WriteObject(val);
    }
}

[Cmdlet(VerbsCommon.Set, "Vom")]
public sealed class SetVomCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string Path { get; set; } = string.Empty;

    [Parameter(Position = 1, Mandatory = true)]
    public object? Value { get; set; }

    protected override void ProcessRecord()
    {
        if (PwshMultiplexer.VomInstance != null && Value != null)
        {
            PwshMultiplexer.VomInstance.Set(Path, Value);
        }
    }
}

[Cmdlet(VerbsCommon.New, "VomWindow")]
public sealed class NewVomWindowCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string WindowPath { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"{WindowPath}\\Visible", true);
        PwshMultiplexer.VomInstance?.Set($"{WindowPath}\\X", 10.0f);
        PwshMultiplexer.VomInstance?.Set($"{WindowPath}\\Y", 5.0f);
        PwshMultiplexer.VomInstance?.Set($"{WindowPath}\\Width", 300.0f);
        PwshMultiplexer.VomInstance?.Set($"{WindowPath}\\Height", 200.0f);
        PwshMultiplexer.VomInstance?.Set($"{WindowPath}\\Z", 0.1f);
    }
}

[Cmdlet(VerbsCommon.Remove, "VomWindow")]
public sealed class RemoveVomWindowCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string WindowPath { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"{WindowPath}\\Visible", false);
    }
}

[Cmdlet(VerbsCommon.Get, "DwmState")]
public sealed class GetDwmStateCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject(PwshMultiplexer.VomInstance?.Get<string>("\\Dwm\\Layout", "float"));
    }
}

[Cmdlet(VerbsCommon.Set, "DwmState")]
public sealed class SetDwmStateCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string Layout { get; set; } = "float";

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set("\\Dwm\\Layout", Layout);
    }
}

[Cmdlet(VerbsLifecycle.Invoke, "DwmAction")]
public sealed class InvokeDwmActionCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string Action { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set("\\Dwm\\LastAction", Action);
    }
}

[Cmdlet(VerbsData.Update, "DwmLayout")]
public sealed class UpdateDwmLayoutCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set("\\Dwm\\LayoutUpdated", true);
    }
}

[Cmdlet(VerbsCommon.Show, "DesktopIcon")]
public sealed class ShowDesktopIconCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string IconName { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"\\Icons\\{IconName}\\Visible", true);
    }
}

[Cmdlet(VerbsCommon.Hide, "DesktopIcon")]
public sealed class HideDesktopIconCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string IconName { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"\\Icons\\{IconName}\\Visible", false);
    }
}

[Cmdlet(VerbsData.Refresh, "Dwm")]
public sealed class RefreshDwmCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set("\\Dwm\\RefreshRequested", true);
    }
}

[Cmdlet(VerbsCommon.Get, "PerformanceData")]
public sealed class GetPerformanceDataCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject(PwshMultiplexer.VomInstance?.Get<int>("\\Telemetry\\SystemCounters\\GPU_MeshShadersCount", 0));
    }
}

[Cmdlet(VerbsCommon.Set, "PerformanceData")]
public sealed class SetPerformanceDataCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public int GpuMeshCount { get; set; }

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set("\\Telemetry\\SystemCounters\\GPU_MeshShadersCount", GpuMeshCount);
    }
}

[Cmdlet(VerbsCommon.Get, "TelemetryConfig")]
public sealed class GetTelemetryConfigCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject(PwshMultiplexer.VomInstance?.Get<string>("\\System\\Telemetry\\Config\\CollectionLevel", "Minimal"));
    }
}

[Cmdlet(VerbsCommon.Set, "TelemetryConfig")]
public sealed class SetTelemetryConfigCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string Level { get; set; } = "Minimal";

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set("\\System\\Telemetry\\Config\\CollectionLevel", Level);
    }
}

[Cmdlet(VerbsCommon.New, "ContextSubMenu")]
public sealed class NewContextSubMenuCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string MenuId { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"\\UI\\ContextMenu\\{MenuId}\\Active", true);
    }
}

[Cmdlet(VerbsCommon.Get, "ActiveContexts")]
public sealed class GetActiveContextsCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject(PwshMultiplexer.VomInstance?.Get<int>("\\Windows\\ContextMenus\\Count", 0));
    }
}

[Cmdlet(VerbsCommon.New, "Fence")]
public sealed class NewFenceCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string FenceName { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"\\Fences\\{FenceName}\\Active", true);
    }
}

[Cmdlet(VerbsLifecycle.Signal, "Fence")]
public sealed class SignalFenceCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string FenceName { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"\\Fences\\{FenceName}\\Signaled", true);
    }
}

[Cmdlet(VerbsCommon.Get, "Mailbox")]
public sealed class GetMailboxCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject("Mailbox_Active");
    }
}

[Cmdlet(VerbsCommunications.Send, "MailboxMessage")]
public sealed class SendMailboxMessageCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string Message { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set("\\Mailboxes\\LastMessage", Message);
    }
}

[Cmdlet(VerbsCommunications.Receive, "MailboxMessage")]
public sealed class ReceiveMailboxMessageCmdlet : PSCmdlet
{
    protected override void ProcessRecord()
    {
        WriteObject(PwshMultiplexer.VomInstance?.Get<string>("\\Mailboxes\\LastMessage", "No Message"));
    }
}

[Cmdlet(VerbsCommon.Get, "ThreadState")]
public sealed class GetThreadStateCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string ThreadId { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        WriteObject(PwshMultiplexer.VomInstance?.Get<int>($"\\HKEY_OBJECT_MANAGER\\Threads\\{ThreadId}\\State", 0));
    }
}

[Cmdlet(VerbsCommon.Set, "ThreadState")]
public sealed class SetThreadStateCmdlet : PSCmdlet
{
    [Parameter(Position = 0, Mandatory = true)]
    public string ThreadId { get; set; } = string.Empty;

    [Parameter(Position = 1, Mandatory = true)]
    public int State { get; set; }

    protected override void ProcessRecord()
    {
        PwshMultiplexer.VomInstance?.Set($"\\HKEY_OBJECT_MANAGER\\Threads\\{ThreadId}\\State", State);
    }
}
