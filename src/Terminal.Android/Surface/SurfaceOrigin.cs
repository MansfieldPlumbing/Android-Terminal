namespace NativePwshConsole.Surface;

public interface ISurfaceResourceResolver
{
    bool Exists(string relativePath);
    Stream OpenRead(string relativePath);
}

public sealed class SurfaceOrigin
{
    public SurfaceOrigin(
        string hardpointId,
        string sourceDocument,
        ISurfaceResourceResolver resources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDocument);
        HardpointId = hardpointId;
        SourceDocument = NormalizeLogicalPath(sourceDocument);
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public string HardpointId { get; }
    public string SourceDocument { get; }
    public ISurfaceResourceResolver Resources { get; }

    public string ResolveResource(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.StartsWith('@'))
            throw new InvalidOperationException("Compiled Android resource identifiers are not valid Surface resources.");
        if (Path.IsPathRooted(reference))
            throw new InvalidOperationException("Absolute paths are not valid Surface resources.");

        string logical;
        if (reference.StartsWith("asset:", StringComparison.Ordinal))
            logical = "Assets/" + reference[6..].TrimStart('/', '\\');
        else if (reference.StartsWith("Assets/", StringComparison.Ordinal))
            logical = reference;
        else
            logical = Path.Combine(Path.GetDirectoryName(SourceDocument) ?? string.Empty, reference);
        return NormalizeLogicalPath(logical);
    }

    internal static string NormalizeLogicalPath(string path)
    {
        if (Path.IsPathRooted(path)) throw new InvalidOperationException("A hardpoint path cannot be absolute.");
        var parts = new List<string>();
        foreach (string part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidOperationException("A hardpoint path cannot escape its origin.");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            if (part.Contains(':')) throw new InvalidOperationException("URI schemes are not valid hardpoint paths.");
            parts.Add(part);
        }
        if (parts.Count == 0) throw new InvalidOperationException("A hardpoint path cannot be empty.");
        return string.Join('/', parts);
    }
}

public sealed class FileSurfaceResourceResolver : ISurfaceResourceResolver
{
    private readonly string _root;
    private readonly string _rootPrefix;

    public FileSurfaceResourceResolver(string root)
    {
        _root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
        _rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

    public Stream OpenRead(string relativePath) => File.OpenRead(Resolve(relativePath));

    private string Resolve(string relativePath)
    {
        string normalized = SurfaceOrigin.NormalizeLogicalPath(relativePath);
        string path = Path.GetFullPath(Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_rootPrefix, StringComparison.Ordinal) && !path.Equals(_root, StringComparison.Ordinal))
            throw new InvalidOperationException("A Surface resource cannot escape its hardpoint origin.");
        return path;
    }
}

internal sealed class EmptySurfaceResourceResolver : ISurfaceResourceResolver
{
    public static EmptySurfaceResourceResolver Instance { get; } = new();
    public bool Exists(string relativePath) => false;
    public Stream OpenRead(string relativePath) => throw new FileNotFoundException("The in-memory Surface has no resources.", relativePath);
}
