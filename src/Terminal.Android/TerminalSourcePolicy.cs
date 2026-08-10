using System.Management.Automation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NativePwshConsole;

public sealed record TerminalPolicyFinding(
    string RuleId,
    string Severity,
    string Path,
    int Line,
    int Column,
    string Message);

public sealed record TerminalPolicyReceipt(
    string Path,
    int Files,
    int Errors,
    int Warnings,
    bool Allowed,
    bool Dragons,
    IReadOnlyList<TerminalPolicyFinding> Findings);

internal static class TerminalSourcePolicy
{
    public static bool DragonsEnabled { get; set; }

    public static TerminalPolicyReceipt Inspect(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        string[] files = System.IO.File.Exists(fullPath)
            ? [fullPath]
            : System.IO.Directory.Exists(fullPath)
                ? System.IO.Directory.GetFiles(fullPath, "*.cs", SearchOption.AllDirectories)
                : throw new FileNotFoundException("Source file or directory was not found.", fullPath);

        var findings = new List<TerminalPolicyFinding>();
        foreach (string file in files.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
        {
            string source = System.IO.File.ReadAllText(file);
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: file);
            foreach (Diagnostic diagnostic in tree.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error))
                Add(findings, "TRM000", "Error", file, diagnostic.Location,
                    "C# syntax error: " + diagnostic.GetMessage());

            var root = tree.GetRoot();
            foreach (AttributeSyntax attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                string name = attribute.Name.ToString();
                if (EndsWithName(name, "DllImport") || EndsWithName(name, "LibraryImport"))
                    Add(findings, "TRM001", "Error", file, attribute.GetLocation(),
                        "Native library imports require an explicitly reviewed native capability.");
            }

            foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string target = invocation.Expression.ToString();
                if (EndsWithCall(target, "Process.Start"))
                    Add(findings, "TRM002", "Error", file, invocation.GetLocation(),
                        "Raw process launch bypasses Terminal's object and capability boundary.");
                if (target.Contains("Assembly.Load", StringComparison.Ordinal) ||
                    target.Contains("AssemblyLoadContext.LoadFrom", StringComparison.Ordinal) ||
                    target.Contains("NativeLibrary.Load", StringComparison.Ordinal) ||
                    target.Contains("Marshal.GetDelegateForFunctionPointer", StringComparison.Ordinal))
                    Add(findings, "TRM003", "Error", file, invocation.GetLocation(),
                        "Dynamic code or native loading requires an explicitly reviewed runtime capability.");
            }

            foreach (ObjectCreationExpressionSyntax creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                string type = creation.Type.ToString();
                if (EndsWithName(type, "TcpListener") || EndsWithName(type, "HttpListener"))
                    Add(findings, "TRM004", "Error", file, creation.GetLocation(),
                        "Network listeners require an explicitly declared listen capability and lifecycle owner.");
                if (EndsWithName(type, "Process"))
                    Add(findings, "TRM002", "Error", file, creation.GetLocation(),
                        "Raw process launch bypasses Terminal's object and capability boundary.");
            }

            foreach (SyntaxToken token in root.DescendantTokens().Where(static t => t.IsKind(SyntaxKind.UnsafeKeyword)))
                Add(findings, "TRM005", "Error", file, token.GetLocation(),
                    "Unsafe code requires an explicitly reviewed memory capability.");
        }

        int errors = findings.Count(static f => f.Severity == "Error");
        return new TerminalPolicyReceipt(fullPath, files.Length, errors,
            findings.Count(static f => f.Severity == "Warning"), errors == 0 || DragonsEnabled,
            DragonsEnabled, findings);
    }

    private static bool EndsWithName(string value, string name) =>
        value.Equals(name, StringComparison.Ordinal) ||
        value.Equals(name + "Attribute", StringComparison.Ordinal) ||
        value.EndsWith("." + name, StringComparison.Ordinal) ||
        value.EndsWith("." + name + "Attribute", StringComparison.Ordinal);

    private static bool EndsWithCall(string value, string name) =>
        value.Equals(name, StringComparison.Ordinal) || value.EndsWith("." + name, StringComparison.Ordinal);

    private static void Add(List<TerminalPolicyFinding> findings, string id, string severity,
        string path, Location location, string message)
    {
        FileLinePositionSpan span = location.GetLineSpan();
        findings.Add(new(id, severity, path, span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1, message));
    }
}

[Cmdlet(VerbsDiagnostic.Test, "TerminalSource")]
[OutputType(typeof(TerminalPolicyReceipt))]
public sealed class TestTerminalSourceCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public string Path { get; set; } = string.Empty;

    protected override void ProcessRecord() => WriteObject(Inspect());

    private TerminalPolicyReceipt Inspect()
    {
        string resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(Path);
        return TerminalSourcePolicy.Inspect(resolved);
    }
}

[Cmdlet("Assert", "TerminalSource")]
[OutputType(typeof(TerminalPolicyReceipt))]
public sealed class AssertTerminalSourceCommand : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public string Path { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        string resolved = SessionState.Path.GetUnresolvedProviderPathFromPSPath(Path);
        TerminalPolicyReceipt receipt = TerminalSourcePolicy.Inspect(resolved);
        WriteObject(receipt);
        if (!receipt.Allowed)
        {
            var exception = new InvalidOperationException(
                $"Terminal refused this source: {receipt.Errors} error-class policy finding(s). " +
                "Review the receipt. Analyzer enforcement can be changed in Settings > Configuration > Roslyn analyzers.");
            ThrowTerminatingError(new ErrorRecord(exception, "Terminal.SourcePolicyDenied",
                ErrorCategory.SecurityError, resolved));
        }
    }
}
