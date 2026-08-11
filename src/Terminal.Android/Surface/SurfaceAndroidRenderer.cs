using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace NativePwshConsole.Surface;

internal sealed class SurfaceAndroidRenderer : IDisposable
{
    private readonly Activity _activity;
    private readonly SurfaceDocument _document;
    private readonly SurfaceEventDispatcher _dispatcher;
    private readonly Action _dismissed;
    private readonly Dictionary<SurfaceNode, View> _views = new();
    private readonly List<Bitmap> _bitmaps = [];
    private Dialog? _dialog;
    private bool _suppressDismiss;

    public SurfaceAndroidRenderer(Activity activity, SurfaceDocument document,
        SurfaceEventDispatcher dispatcher, Action dismissed)
    {
        _activity = activity;
        _document = document;
        _dispatcher = dispatcher;
        _dismissed = dismissed;
    }

    public void Show()
    {
        if (_activity.IsFinishing || _activity.IsDestroyed) return;
        var shell = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        shell.SetBackgroundColor(SurfaceTheme.Background);
        shell.AddView(CreateChrome(), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(SurfaceTheme.ChromeHeightDp)));
        View content = Render(_document.Root);
        shell.AddView(content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        foreach (SurfaceNode node in _document.Walk()) node.Mutated += OnMutated;

        _dialog = new Dialog(_activity, Android.Resource.Style.ThemeMaterialNoActionBar);
        _dialog.SetContentView(shell);
        _dialog.DismissEvent += (_, _) => { if (!_suppressDismiss) _dismissed(); };
        _dialog.Show();
        _dialog.Window?.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        _dialog.Window?.SetSoftInputMode(SoftInput.AdjustResize);
        _dialog.Window?.SetBackgroundDrawable(new ColorDrawable(SurfaceTheme.Background));
    }

    private View CreateChrome()
    {
        var chrome = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        chrome.SetGravity(GravityFlags.CenterVertical);
        chrome.SetPadding(Dp(SurfaceTheme.ChromeHorizontalPaddingDp), 0,
            Dp(SurfaceTheme.CompactPaddingDp), 0);
        chrome.SetBackgroundColor(SurfaceTheme.Raised);
        var title = Text(_document.Root.Title ?? _document.Origin.HardpointId, SurfaceTheme.BodyTextSp);
        chrome.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var close = Text("\u00D7", SurfaceTheme.CloseTextSp);
        close.Gravity = GravityFlags.Center;
        close.Clickable = true;
        close.ContentDescription = "Close";
        close.Click += (_, _) => _dialog?.Dismiss();
        chrome.AddView(close, new LinearLayout.LayoutParams(
            Dp(SurfaceTheme.ChromeCloseWidthDp), Dp(SurfaceTheme.ChromeCloseWidthDp)));
        return chrome;
    }

    private View Render(SurfaceNode node)
    {
        View view = node switch
        {
            SurfaceRoot root => RenderContainer(root, Orientation.Vertical),
            SurfaceStack stack => RenderContainer(stack,
                stack.Direction == SurfaceDirection.Horizontal ? Orientation.Horizontal : Orientation.Vertical),
            SurfaceText text => Text(text.Text, text.Style switch
            {
                SurfaceStyleCatalog.Hero => SurfaceTheme.HeroTextSp,
                SurfaceStyleCatalog.Status => SurfaceTheme.StatusTextSp,
                _ => SurfaceTheme.BodyTextSp
            }),
            SurfaceButton button => RenderButton(button),
            SurfaceInput input => RenderInput(input),
            SurfaceTextArea textArea => RenderTextArea(textArea),
            SurfaceImage image => RenderImage(image),
            SurfaceList list => RenderList(list),
            SurfaceSeparator => RenderSeparator(),
            _ => throw new NotSupportedException($"No Android renderer exists for {node.Kind}.")
        };
        view.Visibility = node.Visible ? ViewStates.Visible : ViewStates.Gone;
        view.Enabled = node.Enabled;
        _views[node] = view;
        return view;
    }

    private View RenderContainer(SurfaceContainer container, Orientation orientation)
    {
        var layout = new LinearLayout(_activity) { Orientation = orientation };
        int horizontalPadding = container.Style is SurfaceStyleCatalog.CommandBar or SurfaceStyleCatalog.StatusBar
            ? SurfaceTheme.CompactPaddingDp : SurfaceTheme.ContentHorizontalPaddingDp;
        int verticalPadding = container.Style is SurfaceStyleCatalog.CommandBar or SurfaceStyleCatalog.StatusBar
            ? SurfaceTheme.CompactVerticalPaddingDp : SurfaceTheme.ContentVerticalPaddingDp;
        layout.SetPadding(Dp(horizontalPadding), Dp(verticalPadding), Dp(horizontalPadding), Dp(verticalPadding));
        if (container.Style == SurfaceStyleCatalog.CommandBar) layout.SetBackgroundColor(SurfaceTheme.Raised);
        foreach (SurfaceNode child in container.Children)
        {
            View view = Render(child);
            LinearLayout.LayoutParams parameters;
            if (orientation == Orientation.Horizontal && child.Grow)
                parameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
            else if (orientation == Orientation.Horizontal)
                parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            else if (child.Grow)
                parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1);
            else if (child is SurfaceSeparator)
                parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(1));
            else
                parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            parameters.SetMargins(0, Dp(SurfaceTheme.ChildGapDp), 0, Dp(SurfaceTheme.ChildGapDp));
            layout.AddView(view, parameters);
        }
        return layout;
    }

    private View RenderButton(SurfaceButton node)
    {
        var button = new Button(_activity) { Text = node.Text };
        button.SetAllCaps(false);
        button.Click += (_, _) => _dispatcher.Enqueue(node.Click, new SurfaceEvent(node, "Click"));
        return button;
    }

    private View RenderInput(SurfaceInput node)
    {
        var input = new EditText(_activity)
        {
            Text = node.Text,
            Hint = node.Hint,
            TextSize = SurfaceTheme.BodyTextSp
        };
        input.SetSingleLine(true);
        input.TextChanged += (_, _) =>
        {
            string next = input.Text ?? string.Empty;
            string previous = node.Text;
            if (node.SetTextFromRenderer(next))
                _dispatcher.Enqueue(node.Changed, new SurfaceEvent(node, "Changed", next, OldValue: previous, NewValue: next));
        };
        return input;
    }

    private View RenderTextArea(SurfaceTextArea node)
    {
        var editor = new SurfaceTextAreaView(_activity)
        {
            Text = node.Text,
            Hint = node.Hint,
            TextSize = SurfaceTheme.EditorTextSp,
            Gravity = GravityFlags.Top | GravityFlags.Start,
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagNoSuggestions
        };
        editor.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        editor.SetTextColor(SurfaceTheme.Foreground);
        editor.SetHintTextColor(SurfaceTheme.MutedForeground);
        editor.SetBackgroundColor(SurfaceTheme.EditorBackground);
        editor.SetPadding(Dp(SurfaceTheme.EditorHorizontalPaddingDp), Dp(SurfaceTheme.EditorVerticalPaddingDp),
            Dp(SurfaceTheme.EditorHorizontalPaddingDp), Dp(SurfaceTheme.EditorVerticalPaddingDp));
        editor.SetHorizontallyScrolling(false);
        editor.SetSingleLine(false);
        editor.TextChanged += (_, _) =>
        {
            string next = editor.Text ?? string.Empty;
            string previous = node.Text;
            if (node.SetTextFromRenderer(next))
                _dispatcher.Enqueue(node.Changed, new SurfaceEvent(node, "Changed", next, OldValue: previous, NewValue: next));
        };
        editor.SelectionChanged += (start, end) =>
        {
            SurfaceCursor cursor = Cursor(editor.Text ?? string.Empty, start, end);
            if (node.SetCursorFromRenderer(cursor))
                _dispatcher.Enqueue(node.CursorChanged, new SurfaceEvent(node, "CursorChanged", cursor));
        };
        return editor;
    }

    private static SurfaceCursor Cursor(string text, int selectionStart, int selectionEnd)
    {
        int offset = Math.Clamp(selectionEnd, 0, text.Length);
        int line = 1;
        int lastBreak = -1;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] != '\n') continue;
            line++;
            lastBreak = i;
        }
        return new SurfaceCursor(offset, Math.Max(0, selectionStart), Math.Max(0, selectionEnd), line, offset - lastBreak);
    }

    private View RenderImage(SurfaceImage node)
    {
        var image = new ImageView(_activity);
        image.SetAdjustViewBounds(true);
        try
        {
            string resource = _document.Origin.ResolveResource(node.Source);
            using Stream stream = _document.Origin.Resources.OpenRead(resource);
            Bitmap? bitmap = BitmapFactory.DecodeStream(stream);
            if (bitmap == null) throw new InvalidDataException($"Resource '{resource}' is not a supported image.");
            _bitmaps.Add(bitmap);
            image.SetImageBitmap(bitmap);
        }
        catch (Exception error)
        {
            image.ContentDescription = $"Image unavailable: {error.Message}";
            Android.Util.Log.Warn("Terminal.Surface", error.ToString());
        }
        return image;
    }

    private View RenderList(SurfaceList node)
    {
        var list = new ListView(_activity) { Adapter = new SurfaceListAdapter(_activity, node) };
        list.ItemClick += (_, args) =>
        {
            SurfaceListEntry[] entries = node.SnapshotEntries();
            if (args.Position < 0 || args.Position >= entries.Length) return;
            object? previous = node.SelectedItem;
            object? selected = entries[args.Position].Value;
            node.SetSelectedItemFromRenderer(selected);
            _dispatcher.Enqueue(node.SelectionChanged,
                new SurfaceEvent(node, "SelectionChanged", selected, selected, previous, selected));
            _dispatcher.Enqueue(node.Invoked, new SurfaceEvent(node, "Invoked", selected, selected));
        };
        return list;
    }

    private View RenderSeparator()
    {
        var separator = new View(_activity);
        separator.SetBackgroundColor(SurfaceTheme.Divider);
        separator.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(1));
        return separator;
    }

    private void OnMutated(SurfaceMutation mutation)
    {
        _activity.RunOnUiThread(() => Apply(mutation));
    }

    private void Apply(SurfaceMutation mutation)
    {
        if (!_views.TryGetValue(mutation.Source, out View? view)) return;
        switch (mutation.Property)
        {
            case SurfaceProperty.Visible:
                view.Visibility = mutation.Source.Visible ? ViewStates.Visible : ViewStates.Gone;
                break;
            case SurfaceProperty.Enabled:
                view.Enabled = mutation.Source.Enabled;
                break;
            case SurfaceProperty.Text when mutation.Source is SurfaceText text && view is TextView textView:
                textView.Text = text.Text;
                break;
            case SurfaceProperty.Text when mutation.Source is SurfaceButton button && view is Button buttonView:
                buttonView.Text = button.Text;
                break;
            case SurfaceProperty.Text when mutation.Source is SurfaceInput input && view is EditText inputView:
                if (!string.Equals(inputView.Text, input.Text, StringComparison.Ordinal)) inputView.Text = input.Text;
                break;
            case SurfaceProperty.Hint when mutation.Source is SurfaceInput input && view is EditText inputView:
                inputView.Hint = input.Hint;
                break;
            case SurfaceProperty.Text when mutation.Source is SurfaceTextArea textArea && view is SurfaceTextAreaView textAreaView:
                if (!string.Equals(textAreaView.Text, textArea.Text, StringComparison.Ordinal)) textAreaView.Text = textArea.Text;
                break;
            case SurfaceProperty.Hint when mutation.Source is SurfaceTextArea textArea && view is SurfaceTextAreaView textAreaView:
                textAreaView.Hint = textArea.Hint;
                break;
            case SurfaceProperty.Items when mutation.Source is SurfaceList && view is ListView listView:
                ((BaseAdapter?)listView.Adapter)?.NotifyDataSetChanged();
                break;
        }
    }

    private TextView Text(string value, float size)
    {
        var text = new TextView(_activity) { Text = value, TextSize = size };
        text.SetTextColor(SurfaceTheme.Foreground);
        text.SetPadding(Dp(SurfaceTheme.CompactPaddingDp), Dp(SurfaceTheme.CompactPaddingDp),
            Dp(SurfaceTheme.CompactPaddingDp), Dp(SurfaceTheme.CompactPaddingDp));
        return text;
    }

    private int Dp(int value) => (int)(value * (_activity.Resources?.DisplayMetrics?.Density ?? 1f) + .5f);

    public void Dispose()
    {
        foreach (SurfaceNode node in _document.Walk()) node.Mutated -= OnMutated;
        _suppressDismiss = true;
        _dialog?.Dismiss();
        _dialog?.Dispose();
        _dialog = null;
        foreach (Bitmap bitmap in _bitmaps) bitmap.Dispose();
        _bitmaps.Clear();
        _views.Clear();
    }

    private sealed class SurfaceListAdapter(Activity activity, SurfaceList node) : BaseAdapter
    {
        public override int Count => node.SnapshotEntries().Length;
        public override Java.Lang.Object? GetItem(int position) => null;
        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var text = convertView as TextView ?? new TextView(activity) { TextSize = SurfaceTheme.BodyTextSp };
            text.SetTextColor(SurfaceTheme.Foreground);
            int padding = (int)(SurfaceTheme.ListRowPaddingDp *
                (activity.Resources?.DisplayMetrics?.Density ?? 1f) + .5f);
            text.SetPadding(padding, padding, padding, padding);
            SurfaceListEntry[] entries = node.SnapshotEntries();
            text.Text = position >= 0 && position < entries.Length ? entries[position].Display : string.Empty;
            return text;
        }
    }

    private sealed class SurfaceTextAreaView(Activity activity) : EditText(activity)
    {
        public event Action<int, int>? SelectionChanged;

        protected override void OnSelectionChanged(int selStart, int selEnd)
        {
            base.OnSelectionChanged(selStart, selEnd);
            SelectionChanged?.Invoke(selStart, selEnd);
        }
    }
}
