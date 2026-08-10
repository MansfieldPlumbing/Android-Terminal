using System.Management.Automation;

namespace NativePwshConsole.Surface;

[Cmdlet(VerbsCommon.Show, "TerminalSurface")]
[OutputType(typeof(PSObject))]
public sealed class ShowTerminalSurfaceCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Path { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        string resolved = GetUnresolvedProviderPathFromPSPath(Path);
        SurfaceDocument document = SurfaceParser.ParseFile(resolved);
        WriteObject(TerminalRuntime.ShowSurface(document));
    }
}

[Cmdlet(VerbsCommon.Close, "TerminalSurface")]
public sealed class CloseTerminalSurfaceCommand : PSCmdlet
{
    protected override void ProcessRecord() => TerminalRuntime.CloseSurface();
}
