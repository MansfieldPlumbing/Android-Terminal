using System.Management.Automation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace NativePwshConsole;

public sealed record RoslynStatus(string Version, string Language, bool Available);

[Cmdlet(VerbsCommon.Get, "Roslyn")]
[OutputType(typeof(RoslynStatus))]
public sealed class GetRoslynCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        // Referencing both the common and C# surfaces intentionally roots the runtime compiler
        // assemblies in the Android package. This is a product capability, not an analyzer-only ref.
        string version = typeof(Compilation).Assembly.GetName().Version?.ToString() ?? "unknown";
        _ = typeof(CSharpCompilation);
        WriteObject(new RoslynStatus(version, LanguageNames.CSharp, true));
    }
}
