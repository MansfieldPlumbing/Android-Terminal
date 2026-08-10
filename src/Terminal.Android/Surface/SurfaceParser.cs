using System.Xml;

namespace NativePwshConsole.Surface;

public sealed class SurfaceParseException : Exception
{
    public SurfaceParseException(string source, int line, int column, string message)
        : base($"{source}({line},{column}): {message}")
    {
        SourceName = source;
        Line = line;
        Column = column;
    }

    public string SourceName { get; }
    public int Line { get; }
    public int Column { get; }
}

public static class SurfaceParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = true
    };

    public static SurfaceDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetDirectoryName(fullPath)!;
        var origin = new SurfaceOrigin("local", Path.GetFileName(fullPath), new FileSurfaceResourceResolver(root));
        return ParseFile(fullPath, origin);
    }

    public static SurfaceDocument ParseFile(string path, SurfaceOrigin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Parse(stream, origin);
    }

    public static SurfaceDocument ParseText(string xml, string source = "<memory>")
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        var origin = new SurfaceOrigin("memory", source, EmptySurfaceResourceResolver.Instance);
        return Parse(stream, origin);
    }

    public static SurfaceDocument Parse(Stream stream, SurfaceOrigin origin)
    {
        string source = origin.SourceDocument;
        var ids = new Dictionary<string, SurfaceNode>(StringComparer.Ordinal);
        try
        {
            using XmlReader reader = XmlReader.Create(stream, ReaderSettings, source);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "surface")
                throw Error(reader, source, "The document root must be <surface>.");
            SurfaceRoot root = (SurfaceRoot)ReadNode(reader, source, ids);
            if (reader.Read() && reader.MoveToContent() != XmlNodeType.None)
                throw Error(reader, source, "Content is not allowed after </surface>.");
            return new SurfaceDocument(origin, root, ids);
        }
        catch (SurfaceParseException) { throw; }
        catch (XmlException error)
        {
            throw new SurfaceParseException(source, error.LineNumber, error.LinePosition, error.Message);
        }
    }

    private static SurfaceNode ReadNode(XmlReader reader, string source, Dictionary<string, SurfaceNode> ids)
    {
        string name = reader.Name;
        return name switch
        {
            "surface" => ReadContainer(reader, source, ids, name, (id, style, attributes, children) =>
                new SurfaceRoot(id, style, Get(attributes, "title"), children)),
            "stack" => ReadContainer(reader, source, ids, name, (id, style, attributes, children) =>
                new SurfaceStack(id, style, ParseDirection(reader, source, attributes.GetValueOrDefault("direction")), children)),
            "text" => ReadText(reader, source, ids, button: false),
            "button" => ReadText(reader, source, ids, button: true),
            "image" => ReadImage(reader, source, ids),
            "input" => ReadInput(reader, source, ids),
            "text-area" => ReadTextArea(reader, source, ids),
            "list" => ReadEmpty(reader, source, ids, (id, style) => new SurfaceList(id, style)),
            "separator" => ReadEmpty(reader, source, ids, (id, style) => new SurfaceSeparator(id, style)),
            _ => throw Error(reader, source, $"Unknown Surface element <{name}>.")
        };
    }

    private static SurfaceNode ReadContainer(
        XmlReader reader,
        string source,
        Dictionary<string, SurfaceNode> ids,
        string element,
        Func<string?, string?, Dictionary<string, string>, IReadOnlyList<SurfaceNode>, SurfaceNode> create)
    {
        Dictionary<string, string> attributes = ReadAttributes(reader, source,
            element == "stack"
                ? ["id", "style", "visible", "enabled", "grow", "direction"]
                : ["id", "style", "visible", "enabled", "grow", "title"]);
        bool empty = reader.IsEmptyElement;
        var children = new List<SurfaceNode>();
        if (!empty)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement) break;
                if (reader.NodeType == XmlNodeType.Element) children.Add(ReadNode(reader, source, ids));
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                    throw Error(reader, source, $"Text is not allowed directly inside <{element}>.");
            }
        }
        SurfaceNode node = create(Get(attributes, "id"), Get(attributes, "style"), attributes, children);
        ApplyCommon(reader, source, node, attributes);
        Register(reader, source, node, ids);
        return node;
    }

    private static SurfaceNode ReadText(XmlReader reader, string source, Dictionary<string, SurfaceNode> ids, bool button)
    {
        Dictionary<string, string> attributes = ReadAttributes(reader, source, ["id", "style", "visible", "enabled", "grow", "text"]);
        string content = attributes.GetValueOrDefault("text", string.Empty);
        if (!reader.IsEmptyElement)
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement) break;
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) content += reader.Value;
                else if (reader.NodeType == XmlNodeType.Element)
                    throw Error(reader, source, $"<{reader.Name}> is not allowed inside <{(button ? "button" : "text")}>.");
            }
        }
        SurfaceNode node = button
            ? new SurfaceButton(Get(attributes, "id"), Get(attributes, "style"), content)
            : new SurfaceText(Get(attributes, "id"), Get(attributes, "style"), content);
        ApplyCommon(reader, source, node, attributes);
        Register(reader, source, node, ids);
        return node;
    }

    private static SurfaceNode ReadInput(XmlReader reader, string source, Dictionary<string, SurfaceNode> ids)
    {
        Dictionary<string, string> attributes = ReadAttributes(reader, source,
            ["id", "style", "visible", "enabled", "grow", "text", "hint"]);
        RequireEmpty(reader, source);
        var node = new SurfaceInput(Get(attributes, "id"), Get(attributes, "style"),
            attributes.GetValueOrDefault("text", string.Empty), attributes.GetValueOrDefault("hint", string.Empty));
        ApplyCommon(reader, source, node, attributes);
        Register(reader, source, node, ids);
        return node;
    }

    private static SurfaceNode ReadTextArea(XmlReader reader, string source, Dictionary<string, SurfaceNode> ids)
    {
        Dictionary<string, string> attributes = ReadAttributes(reader, source,
            ["id", "style", "visible", "enabled", "grow", "text", "hint"]);
        RequireEmpty(reader, source);
        var node = new SurfaceTextArea(Get(attributes, "id"), Get(attributes, "style"),
            attributes.GetValueOrDefault("text", string.Empty), attributes.GetValueOrDefault("hint", string.Empty));
        ApplyCommon(reader, source, node, attributes);
        Register(reader, source, node, ids);
        return node;
    }

    private static SurfaceNode ReadImage(XmlReader reader, string source, Dictionary<string, SurfaceNode> ids)
    {
        Dictionary<string, string> attributes = ReadAttributes(reader, source,
            ["id", "style", "visible", "enabled", "grow", "src"]);
        RequireEmpty(reader, source);
        if (!attributes.TryGetValue("src", out string? src) || string.IsNullOrWhiteSpace(src))
            throw Error(reader, source, "<image> requires a non-empty 'src' attribute.");
        var node = new SurfaceImage(Get(attributes, "id"), Get(attributes, "style"), src);
        ApplyCommon(reader, source, node, attributes);
        Register(reader, source, node, ids);
        return node;
    }

    private static SurfaceNode ReadEmpty(
        XmlReader reader,
        string source,
        Dictionary<string, SurfaceNode> ids,
        Func<string?, string?, SurfaceNode> create)
    {
        Dictionary<string, string> attributes = ReadAttributes(reader, source, ["id", "style", "visible", "enabled", "grow"]);
        RequireEmpty(reader, source);
        SurfaceNode node = create(Get(attributes, "id"), Get(attributes, "style"));
        ApplyCommon(reader, source, node, attributes);
        Register(reader, source, node, ids);
        return node;
    }

    private static Dictionary<string, string> ReadAttributes(XmlReader reader, string source, string[] allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string element = reader.LocalName;
        if (!reader.HasAttributes) return result;
        while (reader.MoveToNextAttribute())
        {
            if (reader.Prefix == "xmlns" || reader.Name == "xmlns")
                throw Error(reader, source, "XML namespaces are not part of the Surface Contract.");
            if (!allowed.Contains(reader.Name, StringComparer.Ordinal))
                throw Error(reader, source, $"Unknown attribute '{reader.Name}' on <{element}>.");
            result.Add(reader.Name, reader.Value);
        }
        reader.MoveToElement();
        return result;
    }

    private static void RequireEmpty(XmlReader reader, string source)
    {
        if (reader.IsEmptyElement) return;
        string element = reader.Name;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement) return;
            if (reader.NodeType is XmlNodeType.Text && string.IsNullOrWhiteSpace(reader.Value)) continue;
            throw Error(reader, source, $"<{element}> must be empty.");
        }
    }

    private static void ApplyCommon(XmlReader reader, string source, SurfaceNode node, Dictionary<string, string> attributes)
    {
        if (attributes.TryGetValue("visible", out string? visible)) node.Visible = ParseBool(reader, source, "visible", visible);
        if (attributes.TryGetValue("enabled", out string? enabled)) node.Enabled = ParseBool(reader, source, "enabled", enabled);
        if (attributes.TryGetValue("grow", out string? grow)) node.Grow = ParseBool(reader, source, "grow", grow);
    }

    private static bool ParseBool(XmlReader reader, string source, string name, string value) =>
        bool.TryParse(value, out bool result)
            ? result
            : throw Error(reader, source, $"Attribute '{name}' must be 'true' or 'false'.");

    private static SurfaceDirection ParseDirection(XmlReader reader, string source, string? value) => value switch
    {
        null or "vertical" => SurfaceDirection.Vertical,
        "horizontal" => SurfaceDirection.Horizontal,
        _ => throw Error(reader, source, "Attribute 'direction' must be 'vertical' or 'horizontal'.")
    };

    private static void Register(XmlReader reader, string source, SurfaceNode node, Dictionary<string, SurfaceNode> ids)
    {
        if (node.Id == null) return;
        if (string.IsNullOrWhiteSpace(node.Id)) throw Error(reader, source, "Attribute 'id' cannot be empty.");
        if (!IsIdentifier(node.Id)) throw Error(reader, source, $"Surface id '{node.Id}' is not a valid identifier.");
        if (!ids.TryAdd(node.Id, node)) throw Error(reader, source, $"Duplicate Surface id '{node.Id}'.");
    }

    private static bool IsIdentifier(string value)
    {
        if (!(char.IsLetter(value[0]) || value[0] == '_')) return false;
        return value.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');
    }

    private static string? Get(Dictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out string? value) ? value : null;

    private static SurfaceParseException Error(XmlReader reader, string source, string message)
    {
        var info = (IXmlLineInfo)reader;
        return new SurfaceParseException(source, info.HasLineInfo() ? info.LineNumber : 0,
            info.HasLineInfo() ? info.LinePosition : 0, message);
    }
}
