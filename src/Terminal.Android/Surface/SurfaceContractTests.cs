using System.Management.Automation;

namespace NativePwshConsole.Surface;

public sealed record SurfaceContractTestResult(string Test, bool Passed, string Detail);

internal static class SurfaceContractTests
{
    public static IReadOnlyList<SurfaceContractTestResult> Run()
    {
        var results = new List<SurfaceContractTestResult>();
        Check(results, "typed-tree", () =>
        {
            SurfaceDocument document = SurfaceParser.ParseText("""
                <surface id="home" title="Command finder">
                  <stack direction="vertical">
                    <input id="query" hint="Find commands" />
                    <button id="search">Search</button>
                    <text-area id="editor" grow="true" />
                    <list id="results" />
                    <text id="status">Ready</text>
                  </stack>
                </surface>
                """, "typed-tree.xml");
            Require(document.GetNode("query") is SurfaceInput, "query was not a SurfaceInput");
            Require(document.GetNode("search") is SurfaceButton, "search was not a SurfaceButton");
            SurfaceNode editorNode = document.GetNode("editor");
            Require(editorNode is SurfaceTextArea && editorNode.Grow, "editor was not a growing SurfaceTextArea");
            var textArea = (SurfaceTextArea)editorNode;
            Require(textArea.Cursor is { Offset: 0, Line: 1, Column: 1 }, "editor cursor did not start at 1:1");
            Require(document.GetNode("results") is SurfaceList, "results was not a SurfaceList");
            Require(document.GetNode("status") is SurfaceText, "status was not a SurfaceText");
            Require(document.Origin.HardpointId == "memory", "origin was not retained");
            Require(document.Root.Title == "Command finder", "surface title was not retained separately from id");
        });
        Check(results, "origin-contained-resource", () =>
        {
            var origin = new SurfaceOrigin("dev.mansfield.proof", "UI/main.xml", EmptySurfaceResourceResolver.Instance);
            Require(origin.ResolveResource("../Assets/logo.png") == "Assets/logo.png", "relative asset did not resolve inside origin");
            Require(origin.ResolveResource("asset:logo.png") == "Assets/logo.png", "logical asset URI did not resolve");
            try { _ = origin.ResolveResource("../../outside.txt"); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("origin allowed resource escape");
        });
        Check(results, "origin-resource-stream", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "terminal-surface-" + Guid.NewGuid().ToString("N"));
            string assets = Path.Combine(root, "Assets");
            try
            {
                Directory.CreateDirectory(assets);
                File.WriteAllBytes(Path.Combine(assets, "proof.bin"), [0x54, 0x55, 0x49]);
                var origin = new SurfaceOrigin("dev.mansfield.proof", "UI/main.xml", new FileSurfaceResourceResolver(root));
                string resource = origin.ResolveResource("asset:proof.bin");
                using Stream stream = origin.Resources.OpenRead(resource);
                Require(stream.ReadByte() == 0x54, "origin resolver did not return hardpoint content");
                try { _ = origin.ResolveResource("@drawable/proof"); }
                catch (InvalidOperationException) { return; }
                throw new InvalidOperationException("origin accepted a compiled Android resource id");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        Reject(results, "duplicate-id", "<surface><text id=\"same\"/><button id=\"same\"/></surface>", "Duplicate Surface id");
        Reject(results, "unknown-element", "<surface><LinearLayout /></surface>", "Unknown Surface element");
        Reject(results, "unknown-attribute", "<surface><button onclick=\"Get-Process\">No</button></surface>", "Unknown attribute");
        Check(results, "binding-is-literal", () =>
        {
            SurfaceDocument document = SurfaceParser.ParseText("<surface><text id=\"value\" text=\"{Binding Name}\" /></surface>");
            Require(((SurfaceText)document.GetNode("value")).Text == "{Binding Name}", "binding-like text was interpreted");
        });
        Reject(results, "malformed-bool", "<surface><text visible=\"sometimes\" /></surface>", "must be 'true' or 'false'");
        Reject(results, "namespace-rejected", "<surface xmlns=\"urn:not-surface\" />", "namespaces are not part");
        return results;
    }

    private static void Check(List<SurfaceContractTestResult> results, string name, Action test)
    {
        try { test(); results.Add(new SurfaceContractTestResult(name, true, "PASS")); }
        catch (Exception error) { results.Add(new SurfaceContractTestResult(name, false, error.Message)); }
    }

    private static void Reject(List<SurfaceContractTestResult> results, string name, string xml, string expected)
    {
        Check(results, name, () =>
        {
            try { _ = SurfaceParser.ParseText(xml, name + ".xml"); }
            catch (SurfaceParseException error) when (error.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                Require(error.Line > 0 && error.Column > 0, "diagnostic did not retain line and column");
                return;
            }
            throw new InvalidOperationException("Parser accepted invalid Surface XML.");
        });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

[Cmdlet(VerbsDiagnostic.Test, "SurfaceContract")]
[OutputType(typeof(SurfaceContractTestResult))]
public sealed class TestSurfaceContractCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        foreach (SurfaceContractTestResult result in SurfaceContractTests.Run()) WriteObject(result);
    }
}
