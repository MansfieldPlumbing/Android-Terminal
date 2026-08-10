using Android.Content;
using System.Xml;

namespace NativePwshConsole.Hardpoints;

public sealed record TerminalHardpoint(
    string Id,
    int SurfaceApi,
    string RootPath,
    string SurfaceDocument,
    string ScriptDocument)
{
    public string SurfacePath => Path.Combine(RootPath, SurfaceDocument.Replace('/', Path.DirectorySeparatorChar));
    public string ScriptPath => Path.Combine(RootPath, ScriptDocument.Replace('/', Path.DirectorySeparatorChar));
}

internal sealed class HardpointCatalog
{
    public const int SupportedSurfaceApi = 1;
    private readonly IReadOnlyDictionary<string, TerminalHardpoint> _hardpoints;

    private HardpointCatalog(IReadOnlyDictionary<string, TerminalHardpoint> hardpoints, IReadOnlyList<string> diagnostics)
    {
        _hardpoints = hardpoints;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyCollection<TerminalHardpoint> All => _hardpoints.Values.ToArray();

    public TerminalHardpoint Get(string id) =>
        _hardpoints.TryGetValue(id, out TerminalHardpoint? hardpoint)
            ? hardpoint
            : throw new KeyNotFoundException($"Hardpoint '{id}' is not installed in this release.");

    public static HardpointCatalog HydrateAndLoad(Context context, string home)
    {
        string system = Path.Combine(home, ".System");
        string destination = Path.Combine(system, "Hardpoints");
        string staging = Path.Combine(system, ".Hardpoints.staging");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            CopyAssetTree(context, "hardpoints", staging);
            HardpointCatalog candidate = Load(staging);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
            return Load(destination);
        }
        catch (Exception error)
        {
            Android.Util.Log.Error("Terminal.Hardpoints", error.ToString());
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            return new HardpointCatalog(new Dictionary<string, TerminalHardpoint>(),
                [$"Hardpoint hydration failed; Terminal continued without cargo: {error.Message}"]);
        }
    }

    private static HardpointCatalog Load(string root)
    {
        var hardpoints = new Dictionary<string, TerminalHardpoint>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        if (!Directory.Exists(root)) return new HardpointCatalog(hardpoints, diagnostics);
        foreach (string directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            try
            {
                TerminalHardpoint hardpoint = ParseManifest(directory);
                if (!hardpoints.TryAdd(hardpoint.Id, hardpoint))
                    throw new InvalidDataException($"Duplicate hardpoint id '{hardpoint.Id}'.");
            }
            catch (Exception error)
            {
                diagnostics.Add($"{Path.GetFileName(directory)}: {error.Message}");
            }
        }
        return new HardpointCatalog(hardpoints, diagnostics);
    }

    private static TerminalHardpoint ParseManifest(string root)
    {
        string manifest = Path.Combine(root, "manifest.xml");
        if (!File.Exists(manifest)) throw new FileNotFoundException("manifest.xml is required.", manifest);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true };
        using XmlReader reader = XmlReader.Create(manifest, settings);
        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element || reader.Name != "hardpoint")
            throw new InvalidDataException("Manifest root must be <hardpoint>.");
        Dictionary<string, string> rootAttributes = Attributes(reader, ["id", "surface-api"]);
        string id = Required(rootAttributes, "id");
        if (!IsId(id)) throw new InvalidDataException($"Hardpoint id '{id}' is invalid.");
        if (!int.TryParse(Required(rootAttributes, "surface-api"), out int api) || api < 0)
            throw new InvalidDataException("surface-api must be a non-negative integer.");
        if (api > SupportedSurfaceApi)
            throw new InvalidDataException($"Surface API {api} is not supported by this Terminal base.");
        string? ui = null;
        string? script = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement) break;
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.Name is not ("ui" or "script")) throw new InvalidDataException($"Unknown manifest element <{reader.Name}>.");
            Dictionary<string, string> attributes = Attributes(reader, ["src"]);
            string path = NativePwshConsole.Surface.SurfaceOrigin.NormalizeLogicalPath(Required(attributes, "src"));
            if (!reader.IsEmptyElement) throw new InvalidDataException($"<{reader.Name}> must be empty.");
            if (reader.Name == "ui")
            {
                if (ui != null) throw new InvalidDataException("Manifest contains more than one <ui>.");
                ui = path;
            }
            else
            {
                if (script != null) throw new InvalidDataException("Manifest contains more than one <script>.");
                script = path;
            }
        }
        if (ui == null) throw new InvalidDataException("Manifest requires <ui src=\"...\" />.");
        if (script == null) throw new InvalidDataException("Manifest requires <script src=\"...\" />.");
        string fullRoot = Path.GetFullPath(root);
        RequireContainedFile(fullRoot, ui);
        RequireContainedFile(fullRoot, script);
        return new TerminalHardpoint(id, api, fullRoot, ui, script);
    }

    private static void CopyAssetTree(Context context, string assetPath, string destination)
    {
        string[] entries = context.Assets?.List(assetPath) ?? [];
        foreach (string entry in entries)
        {
            string childAsset = string.IsNullOrEmpty(assetPath) ? entry : assetPath + "/" + entry;
            string childDestination = Path.Combine(destination, entry);
            string[] children = context.Assets?.List(childAsset) ?? [];
            if (children.Length > 0)
            {
                Directory.CreateDirectory(childDestination);
                CopyAssetTree(context, childAsset, childDestination);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(childDestination)!);
                using Stream source = context.Assets!.Open(childAsset);
                using Stream target = File.Create(childDestination);
                source.CopyTo(target);
            }
        }
    }

    private static Dictionary<string, string> Attributes(XmlReader reader, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.MoveToNextAttribute())
        {
            if (!allowed.Contains(reader.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown attribute '{reader.Name}'.");
            result.Add(reader.Name, reader.Value);
        }
        reader.MoveToElement();
        return result;
    }

    private static string Required(Dictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Required attribute '{name}' is missing.");

    private static void RequireContainedFile(string root, string logical)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, logical.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !File.Exists(path))
            throw new FileNotFoundException($"Hardpoint file '{logical}' is absent or outside its origin.");
    }

    private static bool IsId(string id) => id.Length is > 0 and <= 128 &&
        id.Split('.').All(part => part.Length > 0 && (char.IsLetter(part[0]) || part[0] == '_') &&
            part.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'));
}
