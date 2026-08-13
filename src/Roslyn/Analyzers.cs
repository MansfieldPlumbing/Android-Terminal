using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Terminal.Architecture;

// =================================================================================================
// TERMINAL.VT
//
// Terminal.VT is the platform-neutral semantic machine for a VT terminal stream. It owns parser
// state, terminal cells, primary/alternate screen state, cursor and saved cursor state, scroll
// regions, rendition, terminal modes, terminal-driven title/hyperlink semantics, reply intents,
// Unicode/ANSI mechanics, and deliberately bounded terminal history. Resize of the primary screen
// preserves logical wrapped lines and terminal geometry; malformed control input returns to a known
// parser state; every representation invariant remains true after write, erase, insert, delete,
// scroll, reset, resize, and reflow.
//
// Terminal.VT does not own PTYs, processes, Remedy, Multiplexer routes, worker generations, channel
// transport, Android, Canvas, touch, viewport position, selection interaction, IME composition, or
// renderer invalidation. Presentation may observe semantic state; observation must not consume or
// mutate hidden paint bookkeeping. Specialist Unicode terminology stays in the mechanical text
// boundary and does not become the public cell model. One cell represents one presented character;
// terminal columns are addressing geometry, not fake semantic cells.
//
// The former Terminal.Engine identity is dead. Engine is not an architectural noun in this
// repository. VT code belongs in Terminal.VT, including reflow. Multiplexer may transport bytes and
// resize requests but must not contain VT parser, cell, screen, scrollback, Unicode, or reflow code.
// =================================================================================================

internal enum VtMark
{
ForbiddenName,
StaleIdentity,
StaleReference,
MisplacedVtCode,
MisplacedVtArtifact,
PlatformLeak,
ProcessLeak,
TransportLeak,
ViewportState,
SelectionState,
CompositionState,
PresentationInvalidation,
ConsumptiveSnapshot,
FullGridSnapshot,
ParserHotPathAllocation,
UnboundedHyperlinks,
IncompleteReset,
PrimaryResizeWithoutReflow,
RawCellShift,
ContinuationCell,
SpecialistTermSurface,
UnboundedControlSequence,
}

// =================================================================================================
// MECHANICAL
//
// One descriptor. One pass/fail meaning. The build message carries the terse red-mark key.
// Additional-file checks activate when project files / patches are supplied as AdditionalFiles.
// =================================================================================================

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TerminalArchitectureAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Failure = new(
        "TRMVT000",
        "Terminal.VT architecture",
        "TERMINAL.VT / {0}",
        "Terminal.VT",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Failure);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeTree);
        context.RegisterAdditionalFileAction(AnalyzeAdditionalFile);
    }

    private static void AnalyzeTree(SyntaxTreeAnalysisContext context)
    {
        string path = Normalize(context.Tree.FilePath);
        CompilationUnitSyntax root = context.Tree.GetCompilationUnitRoot(context.CancellationToken);
        string source = root.ToFullString();
        bool inVt = Has(path, "/Terminal.VT/", StringComparison.OrdinalIgnoreCase) ||
                    Has(path, "/Terminal.Engine/", StringComparison.OrdinalIgnoreCase);
        bool inMultiplexer = Has(path, "/Terminal.Multiplexer/", StringComparison.OrdinalIgnoreCase) ||
                             Has(path, "/Terminal.Router/", StringComparison.OrdinalIgnoreCase);
        var emitted = new HashSet<VtMark>();

        void Fail(VtMark mark, Location location)
        {
            if (emitted.Add(mark))
                context.ReportDiagnostic(Diagnostic.Create(Failure, location, mark.ToString()));
        }

        BaseNamespaceDeclarationSyntax? staleNamespace = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault(static node => node.Name.ToString().Equals("Terminal.Engine", StringComparison.Ordinal));

        if (staleNamespace is not null)
            Fail(VtMark.StaleIdentity, staleNamespace.Name.GetLocation());

        if (Has(path, "/Terminal.Engine/", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("TerminalEngine.cs", StringComparison.OrdinalIgnoreCase))
            Fail(VtMark.StaleIdentity, root.GetLocation());

        UsingDirectiveSyntax? staleUsing = root.DescendantNodes().OfType<UsingDirectiveSyntax>().FirstOrDefault(static node =>
            node.Name?.ToString().Equals("Terminal.Engine", StringComparison.Ordinal) == true);

        if (staleUsing is not null)
            Fail(inVt ? VtMark.StaleIdentity : VtMark.StaleReference, staleUsing.GetLocation());

        SyntaxToken forbidden = FirstDeclaredIdentifier(root, static name =>
            name.IndexOf("engine", StringComparison.OrdinalIgnoreCase) >= 0);

        if (forbidden.RawKind != 0 && (inVt || inMultiplexer || Has(source, "Terminal.Engine", StringComparison.Ordinal)))
            Fail(VtMark.ForbiddenName, forbidden.GetLocation());

        if (inMultiplexer)
        {
            SyntaxToken vtToken = FirstIdentifier(root, static name =>
                name is "TerminalEngine" or "TerminalCell" or "TerminalSnapshot" or "TerminalColor" or "TerminalAttributes" or
                        "TerminalSelection" or "UnicodeWidth" or "ParserState" or "ReflowPrimary" or
                        "ScrollbackLine" or "WrappedRows" or "ScrollViewport" or "SetComposition");
            if (vtToken.RawKind != 0)
                Fail(VtMark.MisplacedVtCode, vtToken.GetLocation());
        }

        if (!inVt)
            return;

        UsingDirectiveSyntax? platformUsing = root.DescendantNodes().OfType<UsingDirectiveSyntax>().FirstOrDefault(static node =>
        {
            string name = node.Name?.ToString() ?? string.Empty;
            return name.StartsWith("Android", StringComparison.Ordinal) ||
                   name.StartsWith("Java", StringComparison.Ordinal) ||
                   name.StartsWith("Microsoft.Win32.SafeHandles", StringComparison.Ordinal) ||
                   name.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal);
        });
        if (platformUsing is not null)
            Fail(VtMark.PlatformLeak, platformUsing.GetLocation());

        AttributeSyntax? nativeImport = root.DescendantNodes().OfType<AttributeSyntax>().FirstOrDefault(static node =>
        {
            string name = node.Name.ToString();
            return name.EndsWith("LibraryImport", StringComparison.Ordinal) ||
                   name.EndsWith("DllImport", StringComparison.Ordinal);
        });
        if (nativeImport is not null)
            Fail(VtMark.PlatformLeak, nativeImport.GetLocation());

        SyntaxToken process = FirstIdentifier(root, static name =>
            name is "IPtySession" or "PtySessionFactory" or "WindowsPtySession" or "AndroidPtySession" or
                    "SafeProcessHandle" or "ProcessId" or "JobObject" or "CreateProcess" or "CreatePseudoConsole");
        if (process.RawKind != 0)
            Fail(VtMark.ProcessLeak, process.GetLocation());

        SyntaxToken transport = FirstIdentifier(root, static name =>
            name is "EndpointTransport" or "WireFrame" or "WireKind" or "RouterMessage" or "RouterOperation" or
                    "Remedy" or "Multiplexer");
        if (transport.RawKind != 0)
            Fail(VtMark.TransportLeak, transport.GetLocation());

        ReportIdentifier(root, Fail, VtMark.ViewportState,
            "ScrollViewport", "_scrollbackOffset", "ScrollbackOffset");
        ReportIdentifier(root, Fail, VtMark.SelectionState,
            "Selection", "SetSelection", "ClearSelection", "TerminalSelection");
        ReportIdentifier(root, Fail, VtMark.CompositionState,
            "_composition", "_compositionCaret", "SetComposition", "OverlayComposition");
        ReportIdentifier(root, Fail, VtMark.PresentationInvalidation,
            "_dirtyRows", "DirtyRows", "Changed", "MarkDirty", "MarkDirtyRange", "MarkAllDirty", "consumeDirtyRows");

        MethodDeclarationSyntax? snapshot = FindMethod(root, "CaptureSnapshot");
        if (snapshot is not null)
        {
            string body = snapshot.ToFullString();
            if (Has(body, "_dirtyRows.Clear", StringComparison.Ordinal))
                Fail(VtMark.ConsumptiveSnapshot, snapshot.Identifier.GetLocation());
            if (Has(body, "CloneLines(", StringComparison.Ordinal))
                Fail(VtMark.FullGridSnapshot, snapshot.Identifier.GetLocation());
        }

        MethodDeclarationSyntax? hotPath = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(static method =>
            {
                if (method.Identifier.ValueText is not ("ProcessCharacter" or "FinishOsc" or "ParseParameters"))
                    return false;
                string text = method.ToFullString();
                return Has(text, "_sequence.ToString()", StringComparison.Ordinal) ||
                       Has(text, ".Split(';')", StringComparison.Ordinal) ||
                       Has(text, ".Split(\";\")", StringComparison.Ordinal);
            });
        if (hotPath is not null)
            Fail(VtMark.ParserHotPathAllocation, hotPath.Identifier.GetLocation());

        bool writesHyperlinks = Has(source, "_hyperlinks[id]", StringComparison.Ordinal) &&
                                Has(source, "_nextHyperlink++", StringComparison.Ordinal);
        bool boundsHyperlinks = Has(source, "_hyperlinks.Count", StringComparison.Ordinal) ||
                                Has(source, "TrimHyperlink", StringComparison.Ordinal) ||
                                Has(source, "MaxHyperlink", StringComparison.Ordinal);
        if (writesHyperlinks && !boundsHyperlinks)
        {
            SyntaxToken token = FirstIdentifier(root, static name => name == "_hyperlinks");
            Fail(VtMark.UnboundedHyperlinks, token.RawKind == 0 ? root.GetLocation() : token.GetLocation());
        }

        MethodDeclarationSyntax? reset = FindMethod(root, "Reset");
        if (reset is not null && ResetIsIncomplete(root, reset))
            Fail(VtMark.IncompleteReset, reset.Identifier.GetLocation());

        bool hasScrollback = FirstIdentifier(root, static name => name == "_scrollback").RawKind != 0;
        bool hasPrimaryResize = FindMethod(root, "Resize") is not null;
        bool hasReflow = FindMethod(root, "ReflowPrimary") is not null;
        if (hasScrollback && hasPrimaryResize && !hasReflow)
        {
            MethodDeclarationSyntax? resize = FindMethod(root, "Resize");
            Fail(VtMark.PrimaryResizeWithoutReflow, resize?.Identifier.GetLocation() ?? root.GetLocation());
        }

        MethodDeclarationSyntax? rawShift = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(static method =>
                (method.Identifier.ValueText is "InsertCharacters" or "DeleteCharacters" or "ResizeLine") &&
                Has(method.ToFullString(), "Array.Copy", StringComparison.Ordinal));
        if (rawShift is not null)
            Fail(VtMark.RawCellShift, rawShift.Identifier.GetLocation());

        PropertyDeclarationSyntax? continuation = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(static property => property.Identifier.ValueText == "IsContinuation");
        if (continuation is not null)
            Fail(VtMark.ContinuationCell, continuation.Identifier.GetLocation());

        ParameterSyntax? specialist = root.DescendantNodes().OfType<RecordDeclarationSyntax>()
            .Where(static record => record.Identifier.ValueText == "TerminalCell")
            .SelectMany(static record => record.ParameterList?.Parameters ?? default)
            .FirstOrDefault(static parameter => parameter.Identifier.ValueText == "Grapheme");
        if (specialist is not null)
            Fail(VtMark.SpecialistTermSurface, specialist.Identifier.GetLocation());

        if (Has(source, "ParserState.Csi", StringComparison.Ordinal) &&
            Has(source, "_sequence.Append", StringComparison.Ordinal) &&
            !Has(source, "_sequence.Length < 256", StringComparison.Ordinal))
            Fail(VtMark.UnboundedControlSequence, root.GetLocation());

        if (Has(source, "ParserState.Osc", StringComparison.Ordinal) &&
            Has(source, "_sequence.Append", StringComparison.Ordinal) &&
            !Has(source, "_sequence.Length < 4096", StringComparison.Ordinal))
            Fail(VtMark.UnboundedControlSequence, root.GetLocation());
    }

    private static void AnalyzeAdditionalFile(AdditionalFileAnalysisContext context)
    {
        string path = Normalize(context.AdditionalFile.Path);
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".patch", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".diff", StringComparison.OrdinalIgnoreCase))
            return;

        string text = context.AdditionalFile.GetText(context.CancellationToken)?.ToString() ?? string.Empty;
        var emitted = new HashSet<VtMark>();

        void Fail(VtMark mark)
        {
            if (emitted.Add(mark))
                context.ReportDiagnostic(Diagnostic.Create(Failure, FileStart(path), mark.ToString()));
        }

        bool inVt = Has(path, "/Terminal.VT/", StringComparison.OrdinalIgnoreCase) ||
                    Has(path, "/Terminal.Engine/", StringComparison.OrdinalIgnoreCase);
        bool inMultiplexer = Has(path, "/Terminal.Multiplexer/", StringComparison.OrdinalIgnoreCase) ||
                             Has(path, "/Terminal.Router/", StringComparison.OrdinalIgnoreCase);

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            if (inVt && (Has(text, "Terminal.Engine", StringComparison.Ordinal) ||
                         Has(text, "terminal-engine", StringComparison.OrdinalIgnoreCase)))
                Fail(VtMark.StaleIdentity);
            else if (Has(text, "Terminal.Engine", StringComparison.Ordinal))
                Fail(VtMark.StaleReference);
        }

        if ((extension.Equals(".patch", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".diff", StringComparison.OrdinalIgnoreCase)) && inMultiplexer)
        {
            if (Has(text, "Terminal.Engine/TerminalEngine.cs", StringComparison.Ordinal) ||
                Has(text, "ReflowPrimary", StringComparison.Ordinal) ||
                Has(text, "WrappedRows", StringComparison.Ordinal) ||
                Has(text, "TerminalCell", StringComparison.Ordinal))
                Fail(VtMark.MisplacedVtArtifact);
        }
    }

    private static bool ResetIsIncomplete(CompilationUnitSyntax root, MethodDeclarationSyntax reset)
    {
        string body = reset.ToFullString();

        if (FirstIdentifier(root, static name => name == "_hyperlinks").RawKind != 0 &&
            !Has(body, "_hyperlinks.Clear", StringComparison.Ordinal))
            return true;

        if (FirstIdentifier(root, static name => name == "_nextHyperlink").RawKind != 0 &&
            !Has(body, "_nextHyperlink = 1", StringComparison.Ordinal))
            return true;

        if (FirstIdentifier(root, static name => name == "_sequence").RawKind != 0 &&
            !Has(body, "_sequence.Clear", StringComparison.Ordinal))
            return true;

        if (FirstIdentifier(root, static name => name == "_parserState").RawKind != 0 &&
            !Has(body, "ParserState.Ground", StringComparison.Ordinal))
            return true;

        if (FirstIdentifier(root, static name => name == "_pendingHighSurrogate").RawKind != 0 &&
            !Has(body, "_pendingHighSurrogate = null", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static void ReportIdentifier(
        CompilationUnitSyntax root,
        Action<VtMark, Location> fail,
        VtMark mark,
        params string[] names)
    {
        SyntaxToken token = FirstIdentifier(root, name => names.Contains(name, StringComparer.Ordinal));
        if (token.RawKind != 0)
            fail(mark, token.GetLocation());
    }

    private static MethodDeclarationSyntax? FindMethod(CompilationUnitSyntax root, string name) =>
        root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.ValueText.Equals(name, StringComparison.Ordinal));

    private static SyntaxToken FirstIdentifier(CompilationUnitSyntax root, Func<string, bool> predicate) =>
        root.DescendantTokens()
            .FirstOrDefault(token => token.IsKind(SyntaxKind.IdentifierToken) && predicate(token.ValueText));

    private static SyntaxToken FirstDeclaredIdentifier(CompilationUnitSyntax root, Func<string, bool> predicate) =>
        root.DescendantTokens().FirstOrDefault(token =>
            token.IsKind(SyntaxKind.IdentifierToken) &&
            predicate(token.ValueText) &&
            IsDeclarationIdentifier(token));

    private static bool IsDeclarationIdentifier(SyntaxToken token) => token.Parent switch
    {
        BaseTypeDeclarationSyntax declaration => declaration.Identifier == token,
        DelegateDeclarationSyntax declaration => declaration.Identifier == token,
        MethodDeclarationSyntax declaration => declaration.Identifier == token,
        PropertyDeclarationSyntax declaration => declaration.Identifier == token,
        EventDeclarationSyntax declaration => declaration.Identifier == token,
        VariableDeclaratorSyntax declaration => declaration.Identifier == token,
        ParameterSyntax declaration => declaration.Identifier == token,
        LocalFunctionStatementSyntax declaration => declaration.Identifier == token,
        _ => false,
    };

    private static Location FileStart(string path) =>
        Location.Create(
            path,
            new TextSpan(0, 0),
            new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0)));

    private static bool Has(string text, string value, StringComparison comparison) =>
        text.IndexOf(value, comparison) >= 0;

    private static string Normalize(string path) =>
        (path ?? string.Empty).Replace('\\', '/');
}
