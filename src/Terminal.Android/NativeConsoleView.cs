using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Terminal.VT;

namespace NativePwshConsole;

internal sealed class NativeConsoleView : View
{
    private const int KeyboardCursorVisibleMs = 600;
    private const int KeyboardCursorHiddenMs = 500;
    private readonly TerminalVT _engine;
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly Paint _surfacePaint = new();
    private readonly Paint _selectionPaint = new();
    private readonly Paint _cursorPaint = new();
    private readonly Paint _cursorAuraPaint = new(PaintFlags.AntiAlias);
    private readonly ScaleGestureDetector _scale;
    private readonly float _scaledDensity;
    private readonly float _density;
    private float _cellWidth;
    private float _cellHeight;
    private float _baselineOffset;
    private float _lastTouchY;
    private float _touchStartX;
    private float _touchStartY;
    private float _dragRemainder;
    private bool _touchMoved;
    private float _fontSize;
    private uint _defaultForeground;
    private uint _defaultBackground;
    private float _pingerSizeDp;
    private int _pingerIntervalMs;
    private bool _imeVisible;
    private bool _hardwareKeyboard;
    private bool _attached;
    private bool _presenterActive;
    private bool _subscribed;
    private TerminalSnapshot? _snapshot;
    private int _snapshotDirty = 1;
    private long _keyboardCursorBlinkEpoch;

    public event Action<int, int>? ViewportChanged;
    public event Action? InputRequested;

    public NativeConsoleView(Context context, TerminalVT engine, ConsoleSettings settings) : base(context)
    {
        _engine = engine;
        _scaledDensity = context.Resources?.DisplayMetrics?.ScaledDensity ?? 1f;
        _density = context.Resources?.DisplayMetrics?.Density ?? 1f;
        _fontSize = settings.FontSize;
        _pingerSizeDp = settings.PingerSize;
        _pingerIntervalMs = settings.PingerInterval;
        _paint.SetTypeface(Typeface.Monospace);
        _selectionPaint.Color = Color.Argb(92, 90, 170, 255);
        _cursorPaint.Color = Color.ParseColor("#F5F5F5");
        _cursorPaint.StrokeWidth = Math.Max(2, context.Resources?.DisplayMetrics?.Density * 1.5f ?? 2);
        _keyboardCursorBlinkEpoch = SystemClock.UptimeMillis();
        ApplyMetrics();
        ApplyColors(settings.Background, settings.Foreground);
        SetPadding(12, 4, 12, 4);
        _scale = new ScaleGestureDetector(context, new ScaleListener(this));
        Clickable = true;
        Focusable = true;
        FocusableInTouchMode = true;
    }

    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        _attached = true;
        UpdateEngineSubscription();
    }

    protected override void OnDetachedFromWindow()
    {
        _attached = false;
        UpdateEngineSubscription();
        base.OnDetachedFromWindow();
    }

    private void OnEngineChanged()
    {
        _keyboardCursorBlinkEpoch = SystemClock.UptimeMillis();
        Interlocked.Exchange(ref _snapshotDirty, 1);
        PostInvalidate();
    }

    public void SetPresenterActive(bool active)
    {
        _presenterActive = active;
        if (active) Interlocked.Exchange(ref _snapshotDirty, 1);
        UpdateEngineSubscription();
        if (active) Invalidate();
    }

    private void UpdateEngineSubscription()
    {
        bool shouldSubscribe = _attached && _presenterActive;
        if (shouldSubscribe == _subscribed) return;
        if (shouldSubscribe) _engine.Changed += OnEngineChanged;
        else _engine.Changed -= OnEngineChanged;
        _subscribed = shouldSubscribe;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (!_presenterActive) return;
        if (_snapshot == null || Interlocked.Exchange(ref _snapshotDirty, 0) != 0)
            _snapshot = _engine.CaptureSnapshot();
        TerminalSnapshot snapshot = _snapshot;
        int visibleRows = Math.Min(snapshot.Rows,
            Math.Max(1, (int)((Height - PaddingTop - PaddingBottom) / _cellHeight)));
        int visibleColumns = Math.Min(snapshot.Columns,
            Math.Max(1, (int)((Width - PaddingLeft - PaddingRight) / _cellWidth)));

        DrawBackgrounds(canvas, snapshot, visibleRows, visibleColumns);
        DrawGlyphs(canvas, snapshot, visibleRows, visibleColumns);
        DrawSelection(canvas, snapshot, visibleRows, visibleColumns);
        DrawCursor(canvas, snapshot, visibleRows, visibleColumns);
    }

    private void DrawBackgrounds(Canvas canvas, TerminalSnapshot snapshot, int rows, int columns)
    {
        for (int row = 0; row < rows; row++)
        {
            TerminalCell[] line = snapshot.Lines[row];
            int start = 0;
            while (start < columns)
            {
                uint color = EffectiveBackground(line[start]);
                int end = start + 1;
                while (end < columns && EffectiveBackground(line[end]) == color) end++;
                if (color != _defaultBackground)
                {
                    _surfacePaint.Color = new Color(unchecked((int)color));
                    canvas.DrawRect(
                        PaddingLeft + start * _cellWidth,
                        PaddingTop + row * _cellHeight,
                        PaddingLeft + end * _cellWidth,
                        PaddingTop + (row + 1) * _cellHeight,
                        _surfacePaint);
                }
                start = end;
            }
        }
    }

    private void DrawGlyphs(Canvas canvas, TerminalSnapshot snapshot, int rows, int columns)
    {
        for (int row = 0; row < rows; row++)
        {
            TerminalCell[] line = snapshot.Lines[row];
            float baseline = PaddingTop + row * _cellHeight + _baselineOffset;
            for (int column = 0; column < columns; column++)
            {
                TerminalCell cell = line[column];
                if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme) || cell.Grapheme == " ") continue;
                ConfigureGlyphPaint(cell);
                canvas.DrawText(cell.Grapheme, PaddingLeft + column * _cellWidth, baseline, _paint);
            }
        }
    }

    private void DrawSelection(Canvas canvas, TerminalSnapshot snapshot, int rows, int columns)
    {
        if (snapshot.Selection is not TerminalSelection selection) return;
        TerminalPoint start = Before(selection.Anchor, selection.Active) ? selection.Anchor : selection.Active;
        TerminalPoint end = Before(selection.Anchor, selection.Active) ? selection.Active : selection.Anchor;
        for (int row = Math.Max(0, start.Row); row <= Math.Min(rows - 1, end.Row); row++)
        {
            int first = row == start.Row ? start.Column : 0;
            int last = row == end.Row ? end.Column : columns - 1;
            first = Math.Clamp(first, 0, columns - 1);
            last = Math.Clamp(last, 0, columns - 1);
            canvas.DrawRect(
                PaddingLeft + first * _cellWidth,
                PaddingTop + row * _cellHeight,
                PaddingLeft + (last + 1) * _cellWidth,
                PaddingTop + (row + 1) * _cellHeight,
                _selectionPaint);
        }
    }

    private void DrawCursor(Canvas canvas, TerminalSnapshot snapshot, int rows, int columns)
    {
        TerminalCursor cursor = snapshot.Cursor;
        if (!cursor.Visible || cursor.Row < 0 || cursor.Row >= rows || cursor.Column < 0 || cursor.Column >= columns)
            return;
        float left = PaddingLeft + cursor.Column * _cellWidth;
        float top = PaddingTop + cursor.Row * _cellHeight;
        float centerX = left + _cellWidth * .5f;
        float centerY = top + _cellHeight * .5f;
        float radius = _pingerSizeDp * _density * .5f;
        bool keyboardActive = _imeVisible || _hardwareKeyboard;

        if (!keyboardActive)
        {
            long elapsed = SystemClock.UptimeMillis() % _pingerIntervalMs;
            long duration = Math.Min(520, _pingerIntervalMs / 2);
            bool pinging = elapsed < duration;
            DrawPinger(canvas, centerX, centerY, radius,
                pinging ? elapsed / (float)duration : 1f, pinging);
            if (_presenterActive)
            {
                if (pinging) PostInvalidateOnAnimation();
                else PostInvalidateDelayed(Math.Max(16, _pingerIntervalMs - elapsed));
            }
        }

        if (keyboardActive)
        {
            long period = KeyboardCursorVisibleMs + KeyboardCursorHiddenMs;
            long elapsed = (SystemClock.UptimeMillis() - _keyboardCursorBlinkEpoch) % period;
            if (elapsed < KeyboardCursorVisibleMs)
                DrawKeyboardCursor(canvas, snapshot, cursor, left, top);
            if (_presenterActive)
            {
                long next = elapsed < KeyboardCursorVisibleMs
                    ? KeyboardCursorVisibleMs - elapsed
                    : period - elapsed;
                PostInvalidateDelayed(Math.Max(16, next));
            }
        }
    }

    private void DrawKeyboardCursor(Canvas canvas, TerminalSnapshot snapshot, TerminalCursor cursor, float left, float top)
    {
        _cursorPaint.SetStyle(Paint.Style.Fill);
        _cursorPaint.Alpha = 255;
        canvas.DrawRect(left, top + 1, left + _cellWidth, top + _cellHeight - 1, _cursorPaint);

        TerminalCell cell = snapshot.Lines[cursor.Row][cursor.Column];
        if (cell.IsContinuation || string.IsNullOrWhiteSpace(cell.Grapheme)) return;
        ConfigureGlyphPaint(cell);
        _paint.Color = new Color(unchecked((int)_defaultBackground));
        _paint.Alpha = 255;
        canvas.DrawText(cell.Grapheme, left,
            PaddingTop + cursor.Row * _cellHeight + _baselineOffset, _paint);
    }

    private void DrawPinger(Canvas canvas, float x, float y, float radius, float phase, bool pinging)
    {
        FillCursorCircle(canvas, x, y, radius * .09f, pinging ? 92 : 48);
        if (!pinging) return;
        float fade = 1f - phase;
        _cursorAuraPaint.SetStyle(Paint.Style.Stroke);
        _cursorAuraPaint.StrokeWidth = Math.Max(1.25f * _density, radius * (.025f + .025f * fade));
        _cursorAuraPaint.Alpha = (int)(105 * fade * fade);
        canvas.DrawCircle(x, y, radius * (.12f + .88f * phase), _cursorAuraPaint);
    }

    private void FillCursorCircle(Canvas canvas, float x, float y, float radius, int alpha)
    {
        _cursorAuraPaint.SetStyle(Paint.Style.Fill);
        _cursorAuraPaint.Alpha = alpha;
        canvas.DrawCircle(x, y, radius, _cursorAuraPaint);
    }

    protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
    {
        base.OnSizeChanged(width, height, oldWidth, oldHeight);
        ResizeTerminal(width, height);
    }

    public override bool OnTouchEvent(MotionEvent? motion)
    {
        if (motion == null) return false;
        _scale.OnTouchEvent(motion);
        if (_scale.IsInProgress) return true;
        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                _lastTouchY = motion.GetY();
                _touchStartX = motion.GetX();
                _touchStartY = motion.GetY();
                _dragRemainder = 0;
                _touchMoved = false;
                Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;
            case MotionEventActions.Move:
                float delta = motion.GetY() - _lastTouchY;
                _lastTouchY = motion.GetY();
                if (Math.Abs(motion.GetX() - _touchStartX) > _cellWidth ||
                    Math.Abs(motion.GetY() - _touchStartY) > _cellHeight * .5f)
                    _touchMoved = true;
                _dragRemainder += delta;
                int rows = (int)(_dragRemainder / _cellHeight);
                if (rows != 0)
                {
                    _dragRemainder -= rows * _cellHeight;
                    _engine.ScrollViewport(rows);
                }
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                if (motion.ActionMasked == MotionEventActions.Up && !_touchMoved && IsNearCursor(motion.GetX(), motion.GetY()))
                    PerformClick();
                return true;
            default:
                return base.OnTouchEvent(motion);
        }
    }

    public override bool PerformClick()
    {
        base.PerformClick();
        InputRequested?.Invoke();
        return true;
    }

    private bool IsNearCursor(float x, float y)
    {
        TerminalCursor cursor = _engine.CaptureSnapshot(false).Cursor;
        if (!cursor.Visible) return false;
        float cursorX = PaddingLeft + cursor.Column * _cellWidth + _cellWidth * .5f;
        float cursorY = PaddingTop + cursor.Row * _cellHeight + _cellHeight * .5f;
        float radius = _pingerSizeDp * _density * .5f;
        float dx = x - cursorX;
        float dy = y - cursorY;
        return dx * dx + dy * dy <= radius * radius;
    }

    public void SetFontSize(float size)
    {
        _fontSize = Math.Clamp(size, 10f, 32f);
        ApplyMetrics();
        ResizeTerminal(Width, Height);
        Invalidate();
    }

    public void SetScrollback(int lines) => _engine.MaxScrollback = Math.Clamp(lines, 100, 20000);

    public void SetPinger(float sizeDp, int intervalMs)
    {
        _pingerSizeDp = Math.Clamp(sizeDp, 32f, 112f);
        _pingerIntervalMs = Math.Clamp(intervalMs, 400, 5000);
        Invalidate();
    }

    public void SetHardwareKeyboard(bool present)
    {
        if (_hardwareKeyboard == present) return;
        _hardwareKeyboard = present;
        if (present) _keyboardCursorBlinkEpoch = SystemClock.UptimeMillis();
        Invalidate();
    }

    public void SetImeVisible(bool visible)
    {
        if (_imeVisible == visible) return;
        _imeVisible = visible;
        if (visible) _keyboardCursorBlinkEpoch = SystemClock.UptimeMillis();
        Invalidate();
    }

    public override WindowInsets? OnApplyWindowInsets(WindowInsets? insets)
    {
        SetImeVisible(insets?.IsVisible(WindowInsets.Type.Ime()) == true);
        return base.OnApplyWindowInsets(insets);
    }

    public void ApplyColors(string background, string foreground)
    {
        _defaultBackground = ParseArgb(background, 0xff012456);
        _defaultForeground = ParseArgb(foreground, 0xfff5f5f5);
        SetBackgroundColor(new Color(unchecked((int)_defaultBackground)));
        _cursorPaint.Color = new Color(unchecked((int)_defaultForeground));
        _cursorAuraPaint.Color = new Color(unchecked((int)_defaultForeground));
        Invalidate();
    }

    private void ApplyMetrics()
    {
        _paint.TextSize = _fontSize * _scaledDensity;
        _cellWidth = Math.Max(1, _paint.MeasureText("0123456789") / 10f);
        _cellHeight = Math.Max(1, _paint.TextSize * 1.27f);
        _baselineOffset = _paint.TextSize;
    }

    private void ResizeTerminal(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        int columns = Math.Max(1, (int)((width - PaddingLeft - PaddingRight) / _cellWidth));
        int rows = Math.Max(1, (int)((height - PaddingTop - PaddingBottom) / _cellHeight));
        _engine.Resize(columns, rows);
        ViewportChanged?.Invoke(columns, rows);
    }

    private void ConfigureGlyphPaint(TerminalCell cell)
    {
        _paint.Color = new Color(unchecked((int)EffectiveForeground(cell)));
        _paint.Alpha = cell.Attributes.HasFlag(TerminalAttributes.Dim) ? 150 : 255;
        _paint.FakeBoldText = cell.Attributes.HasFlag(TerminalAttributes.Bold);
        _paint.TextSkewX = cell.Attributes.HasFlag(TerminalAttributes.Italic) ? -.2f : 0f;
        _paint.UnderlineText = cell.Attributes.HasFlag(TerminalAttributes.Underline);
        _paint.StrikeThruText = cell.Attributes.HasFlag(TerminalAttributes.Strike);
    }

    private uint EffectiveForeground(TerminalCell cell) =>
        cell.Attributes.HasFlag(TerminalAttributes.Inverse)
            ? Resolve(cell.Background, _defaultBackground)
            : Resolve(cell.Foreground, _defaultForeground);

    private uint EffectiveBackground(TerminalCell cell) =>
        cell.Attributes.HasFlag(TerminalAttributes.Inverse)
            ? Resolve(cell.Foreground, _defaultForeground)
            : Resolve(cell.Background, _defaultBackground);

    private static uint Resolve(TerminalColor color, uint fallback) => color.Kind switch
    {
        TerminalColorKind.Default => fallback,
        TerminalColorKind.Rgb => 0xff000000u | color.Value,
        TerminalColorKind.Indexed => Xterm(color.Index),
        _ => fallback
    };

    private static uint Xterm(int index)
    {
        uint[] normal = [0xff0c0c0c, 0xffc50f1f, 0xff13a10e, 0xffc19c00, 0xff0037da, 0xff881798, 0xff3a96dd, 0xffcccccc];
        uint[] bright = [0xff767676, 0xffe74856, 0xff16c60c, 0xfff9f1a5, 0xff3b78ff, 0xffb4009e, 0xff61d6d6, 0xfff2f2f2];
        if (index < 16) return (index < 8 ? normal : bright)[index & 7];
        if (index < 232)
        {
            int value = index - 16;
            int Component(int component) => component == 0 ? 0 : 55 + component * 40;
            return Argb(Component(value / 36), Component(value / 6 % 6), Component(value % 6));
        }
        int gray = 8 + (Math.Clamp(index, 232, 255) - 232) * 10;
        return Argb(gray, gray, gray);
    }

    private static uint Argb(int red, int green, int blue) =>
        0xff000000u | (uint)(red << 16) | (uint)(green << 8) | (uint)blue;

    private static uint ParseArgb(string value, uint fallback)
    {
        try { return unchecked((uint)Color.ParseColor(value).ToArgb()); }
        catch (ArgumentException) { return fallback; }
    }

    private static bool Before(TerminalPoint left, TerminalPoint right) =>
        left.Row < right.Row || left.Row == right.Row && left.Column <= right.Column;

    private sealed class ScaleListener(NativeConsoleView owner) : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        public override bool OnScale(ScaleGestureDetector detector)
        {
            owner.SetFontSize(owner._fontSize * detector.ScaleFactor);
            return true;
        }
    }
}
