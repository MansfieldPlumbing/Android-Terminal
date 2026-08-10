using System.Management.Automation;
using NativePwshConsole.Surface;

namespace NativePwshConsole.Hardpoints;

[Cmdlet(VerbsCommon.Get, "TerminalHardpoint")]
[OutputType(typeof(TerminalHardpoint))]
public sealed class GetTerminalHardpointCommand : PSCmdlet
{
    [Parameter(Position = 0)] public string? Id { get; set; }

    protected override void ProcessRecord()
    {
        if (string.IsNullOrWhiteSpace(Id))
            WriteObject(TerminalRuntime.GetHardpoints(), enumerateCollection: true);
        else
            WriteObject(TerminalRuntime.GetHardpoint(Id));
    }
}

[Cmdlet(VerbsCommon.Show, "TerminalHardpoint")]
[OutputType(typeof(PSObject))]
public sealed class ShowTerminalHardpointCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)] public string Id { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        TerminalHardpoint hardpoint = TerminalRuntime.GetHardpoint(Id);
        var origin = new SurfaceOrigin(hardpoint.Id, hardpoint.SurfaceDocument,
            new FileSurfaceResourceResolver(hardpoint.RootPath));
        SurfaceDocument document = SurfaceParser.ParseFile(hardpoint.SurfacePath, origin);
        WriteObject(TerminalRuntime.ShowSurface(document));
    }
}
