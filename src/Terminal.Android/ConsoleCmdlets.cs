using System.Management.Automation;

namespace NativePwshConsole;

[Cmdlet(VerbsCommon.Show, "ConsoleSettings")]
public sealed class ShowConsoleSettingsCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        AndroidBridge.ShowSettings();
        WriteObject("Native settings menu opened.");
    }
}

[Cmdlet(VerbsCommon.Show, "ConsoleMenu")]
[OutputType(typeof(string))]
public sealed class ShowConsoleMenuCommand : PSCmdlet
{
    [Parameter(Position = 0)] public string Title { get; set; } = "Choose";
    [Parameter(Mandatory = true, Position = 1)] public string[] Item { get; set; } = [];

    protected override void ProcessRecord()
    {
        string? selected = AndroidBridge.ShowMenu(Title, Item);
        if (selected != null) WriteObject(selected);
    }
}

[Cmdlet(VerbsLifecycle.Start, "SessionGuardian")]
[OutputType(typeof(SessionGuardianStatus))]
public sealed class StartSessionGuardianCommand : PSCmdlet
{
    [Parameter(Position = 0)] public string Name { get; set; } = "PowerShell session";
    [Parameter(Position = 1)] public string Endpoint { get; set; } = "Local session";

    protected override void ProcessRecord()
    {
        AndroidBridge.StartSessionGuardian(Name, Endpoint);
        WriteObject(new SessionGuardianStatus(true, Name, Endpoint));
    }
}

[Cmdlet(VerbsCommon.Get, "SessionGuardian")]
[OutputType(typeof(SessionGuardianStatus))]
public sealed class GetSessionGuardianCommand : PSCmdlet
{
    protected override void ProcessRecord() => WriteObject(AndroidBridge.GetSessionGuardian());
}

[Cmdlet(VerbsLifecycle.Stop, "SessionGuardian")]
public sealed class StopSessionGuardianCommand : PSCmdlet
{
    protected override void ProcessRecord() => AndroidBridge.RequestStopSessionGuardian();
}

[Cmdlet(VerbsData.Edit, "File")]
[OutputType(typeof(System.IO.FileInfo))]
public sealed class EditFileCommand : PSCmdlet
{
    [Parameter(Position = 0)] public string? Path { get; set; }

    protected override void ProcessRecord()
    {
        string requested = string.IsNullOrWhiteSpace(Path) ? SessionState.PSVariable.GetValue("PROFILE")?.ToString() ?? "profile.ps1" : Path;
        string resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(requested);
        if (AndroidBridge.EditFile(resolved)) WriteObject(new System.IO.FileInfo(resolved));
    }
}

[Cmdlet(VerbsCommon.Get, "AdbLoopback")]
[OutputType(typeof(AdbLoopbackStatus))]
public sealed class GetAdbLoopbackCommand : PSCmdlet
{
    protected override void ProcessRecord() => WriteObject(AdbLoopback.GetStatus());
}

[Cmdlet(VerbsLifecycle.Start, "AdbPairing")]
public sealed class StartAdbPairingCommand : PSCmdlet
{
    protected override void ProcessRecord() => AdbLoopback.BeginSetup();
}

[Cmdlet(VerbsLifecycle.Invoke, "AdbShell")]
[OutputType(typeof(string))]
public sealed class InvokeAdbShellCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromRemainingArguments = true)]
    public string[] Command { get; set; } = [];
    protected override void ProcessRecord() => WriteObject(AdbLoopback.Shell(string.Join(' ', Command)));
}

[Cmdlet(VerbsCommon.Clear, "AdbPairing")]
public sealed class ClearAdbPairingCommand : PSCmdlet
{
    [Parameter] public SwitchParameter Force { get; set; }
    protected override void ProcessRecord()
    {
        if (!Force) throw new InvalidOperationException("This forgets the app-private ADB key. Re-run with -Force.");
        AdbLoopback.Forget();
    }
}
