using System.Collections.ObjectModel;
using System.Management.Automation;

namespace NativePwshConsole.Surface;

public enum SurfaceNodeKind
{
    Surface,
    Stack,
    Text,
    Input,
    Button,
    Image,
    List,
    Separator
}

public enum SurfaceDirection { Vertical, Horizontal }

public enum SurfaceProperty
{
    Text,
    Hint,
    Items,
    SelectedItem,
    Visible,
    Enabled
}

public sealed record SurfaceMutation(SurfaceNode Source, SurfaceProperty Property);

public sealed record SurfaceEvent(
    SurfaceNode Source,
    string Name,
    object? Value = null,
    object? Item = null,
    object? OldValue = null,
    object? NewValue = null);

public abstract class SurfaceNode
{
    private readonly object _stateGate = new();
    private bool _visible = true;
    private bool _enabled = true;

    protected SurfaceNode(string? id, string? style)
    {
        Id = id;
        Style = style;
    }

    public string? Id { get; }
    public string? Style { get; }
    public abstract SurfaceNodeKind Kind { get; }

    public bool Visible
    {
        get => Read(ref _visible);
        set => Set(ref _visible, value, SurfaceProperty.Visible);
    }

    public bool Enabled
    {
        get => Read(ref _enabled);
        set => Set(ref _enabled, value, SurfaceProperty.Enabled);
    }

    internal event Action<SurfaceMutation>? Mutated;

    protected bool Set<T>(ref T field, T value, SurfaceProperty property)
    {
        lock (_stateGate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
        }
        Mutated?.Invoke(new SurfaceMutation(this, property));
        return true;
    }

    protected T Read<T>(ref T field)
    {
        lock (_stateGate) return field;
    }

    protected bool SetSilently<T>(ref T field, T value)
    {
        lock (_stateGate)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            return true;
        }
    }
}

public abstract class SurfaceContainer : SurfaceNode
{
    private readonly ReadOnlyCollection<SurfaceNode> _children;

    protected SurfaceContainer(string? id, string? style, IReadOnlyList<SurfaceNode> children)
        : base(id, style) => _children = new ReadOnlyCollection<SurfaceNode>(children.ToArray());

    public IReadOnlyList<SurfaceNode> Children => _children;
}

public sealed class SurfaceRoot : SurfaceContainer
{
    public SurfaceRoot(string? id, string? style, string? title, IReadOnlyList<SurfaceNode> children)
        : base(id, style, children) => Title = title;

    public string? Title { get; }
    public override SurfaceNodeKind Kind => SurfaceNodeKind.Surface;
}

public sealed class SurfaceStack : SurfaceContainer
{
    public SurfaceStack(string? id, string? style, SurfaceDirection direction, IReadOnlyList<SurfaceNode> children)
        : base(id, style, children) => Direction = direction;

    public SurfaceDirection Direction { get; }
    public override SurfaceNodeKind Kind => SurfaceNodeKind.Stack;
}

public sealed class SurfaceText : SurfaceNode
{
    private string _text;

    public SurfaceText(string? id, string? style, string text) : base(id, style) => _text = text;
    public override SurfaceNodeKind Kind => SurfaceNodeKind.Text;

    public string Text
    {
        get => Read(ref _text);
        set => Set(ref _text, value ?? string.Empty, SurfaceProperty.Text);
    }
}

public sealed class SurfaceInput : SurfaceNode
{
    private string _text;
    private string _hint;
    private ScriptBlock? _changed;

    public SurfaceInput(string? id, string? style, string text, string hint) : base(id, style)
    {
        _text = text;
        _hint = hint;
    }

    public override SurfaceNodeKind Kind => SurfaceNodeKind.Input;
    public ScriptBlock? Changed
    {
        get => Volatile.Read(ref _changed);
        set => Volatile.Write(ref _changed, value);
    }

    public string Text
    {
        get => Read(ref _text);
        set => Set(ref _text, value ?? string.Empty, SurfaceProperty.Text);
    }

    public string Hint
    {
        get => Read(ref _hint);
        set => Set(ref _hint, value ?? string.Empty, SurfaceProperty.Hint);
    }

    internal bool SetTextFromRenderer(string value)
    {
        value ??= string.Empty;
        return SetSilently(ref _text, value);
    }
}

public sealed class SurfaceButton : SurfaceNode
{
    private string _text;
    private ScriptBlock? _click;

    public SurfaceButton(string? id, string? style, string text) : base(id, style) => _text = text;
    public override SurfaceNodeKind Kind => SurfaceNodeKind.Button;
    public ScriptBlock? Click
    {
        get => Volatile.Read(ref _click);
        set => Volatile.Write(ref _click, value);
    }

    public string Text
    {
        get => Read(ref _text);
        set => Set(ref _text, value ?? string.Empty, SurfaceProperty.Text);
    }
}

public sealed class SurfaceImage : SurfaceNode
{
    public SurfaceImage(string? id, string? style, string source) : base(id, style) => Source = source;
    public override SurfaceNodeKind Kind => SurfaceNodeKind.Image;
    public string Source { get; }
}

public sealed class SurfaceList : SurfaceNode
{
    private SurfaceListEntry[] _entries = [];
    private object? _selectedItem;
    private ScriptBlock? _selectionChanged;
    private ScriptBlock? _invoked;

    public SurfaceList(string? id, string? style) : base(id, style) { }
    public override SurfaceNodeKind Kind => SurfaceNodeKind.List;
    public ScriptBlock? SelectionChanged
    {
        get => Volatile.Read(ref _selectionChanged);
        set => Volatile.Write(ref _selectionChanged, value);
    }

    public ScriptBlock? Invoked
    {
        get => Volatile.Read(ref _invoked);
        set => Volatile.Write(ref _invoked, value);
    }

    public object?[] Items
    {
        get => Read(ref _entries).Select(entry => entry.Value).ToArray();
        set
        {
            SurfaceListEntry[] entries = (value ?? []).Select(item =>
                new SurfaceListEntry(item, item?.ToString() ?? string.Empty)).ToArray();
            Set(ref _entries, entries, SurfaceProperty.Items);
        }
    }

    public object? SelectedItem
    {
        get => Read(ref _selectedItem);
        set => Set(ref _selectedItem, value, SurfaceProperty.SelectedItem);
    }

    internal void SetSelectedItemFromRenderer(object? value) => SetSilently(ref _selectedItem, value);
    internal SurfaceListEntry[] SnapshotEntries() => Read(ref _entries).ToArray();
}

internal sealed record SurfaceListEntry(object? Value, string Display);

public sealed class SurfaceSeparator : SurfaceNode
{
    public SurfaceSeparator(string? id, string? style) : base(id, style) { }
    public override SurfaceNodeKind Kind => SurfaceNodeKind.Separator;
}

public sealed class SurfaceDocument
{
    private readonly IReadOnlyDictionary<string, SurfaceNode> _nodes;

    internal SurfaceDocument(SurfaceOrigin origin, SurfaceRoot root, IReadOnlyDictionary<string, SurfaceNode> nodes)
    {
        Origin = origin;
        Root = root;
        _nodes = nodes;
        UI = new PSObject();
        foreach ((string id, SurfaceNode node) in nodes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            UI.Properties.Add(new PSNoteProperty(id, node));
    }

    public SurfaceOrigin Origin { get; }
    public string Source => Origin.SourceDocument;
    public SurfaceRoot Root { get; }
    public PSObject UI { get; }
    public IReadOnlyDictionary<string, SurfaceNode> Nodes => _nodes;

    public SurfaceNode GetNode(string id) =>
        _nodes.TryGetValue(id, out SurfaceNode? node)
            ? node
            : throw new KeyNotFoundException($"Surface node '{id}' does not exist.");

    internal IEnumerable<SurfaceNode> Walk()
    {
        var pending = new Stack<SurfaceNode>();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            SurfaceNode node = pending.Pop();
            yield return node;
            if (node is SurfaceContainer container)
                for (int i = container.Children.Count - 1; i >= 0; i--) pending.Push(container.Children[i]);
        }
    }
}
