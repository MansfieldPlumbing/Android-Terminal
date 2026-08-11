namespace NativePwshConsole.Surface;

internal static class SurfaceStyleCatalog
{
    public const string Workspace = "workspace";
    public const string CommandBar = "command-bar";
    public const string StatusBar = "status-bar";
    public const string Hero = "hero";
    public const string Status = "status";
    public const string Editor = "editor";

    private static readonly IReadOnlyDictionary<SurfaceNodeKind, HashSet<string>> Allowed =
        new Dictionary<SurfaceNodeKind, HashSet<string>>
        {
            [SurfaceNodeKind.Surface] = Names(Workspace),
            [SurfaceNodeKind.Stack] = Names(CommandBar, StatusBar),
            [SurfaceNodeKind.Text] = Names(Hero, Status),
            [SurfaceNodeKind.Input] = Names(),
            [SurfaceNodeKind.TextArea] = Names(Editor),
            [SurfaceNodeKind.Button] = Names(),
            [SurfaceNodeKind.Image] = Names(),
            [SurfaceNodeKind.List] = Names(),
            [SurfaceNodeKind.Separator] = Names()
        };

    public static bool IsAllowed(SurfaceNodeKind kind, string? style) =>
        style == null || Allowed[kind].Contains(style);

    public static string Describe(SurfaceNodeKind kind) =>
        Allowed[kind].Count == 0 ? "no named styles" : string.Join(", ", Allowed[kind].Order(StringComparer.Ordinal));

    private static HashSet<string> Names(params string[] values) => new(values, StringComparer.Ordinal);
}
