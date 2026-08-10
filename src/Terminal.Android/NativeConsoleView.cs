using Android.Content;
using Android.Graphics;
using Android.Views;

namespace NativePwshConsole;

internal sealed class NativeConsoleView : View
{
    private readonly CellBuffer _buffer;
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private float _cellWidth;
    private float _cellHeight;
    private float _lastTouchY;
    private float _dragRemainder;
    private readonly ScaleGestureDetector _scale;
    private readonly float _scaledDensity;
    private float _fontSize;
    private char[] _glyphRun = new char[128];
    public event Action<int, int>? ViewportChanged;

    public NativeConsoleView(Context context, CellBuffer buffer, ConsoleSettings settings) : base(context)
    {
        _buffer = buffer;
        _scaledDensity = context.Resources?.DisplayMetrics?.ScaledDensity ?? 1f;
        _paint.Color = Color.ParseColor(settings.Foreground);
        _fontSize = settings.FontSize;
        _paint.TextSize = _fontSize * _scaledDensity;
        _paint.SetTypeface(Typeface.Monospace);
        _cellWidth = MeasureCellWidth();
        _cellHeight = _paint.TextSize * 1.27f;
        SetBackgroundColor(Color.ParseColor(settings.Background));
        SetPadding(12, 4, 12, 4);
        _scale = new ScaleGestureDetector(context, new ScaleListener(this));
        Clickable = true;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        int columns = Math.Max(1, (int)((Width - PaddingLeft - PaddingRight) / _cellWidth));
        int rows = Math.Max(1, (int)(Height / _cellHeight));
        CellLine[] lines = _buffer.Snapshot(rows, columns);
        float baseline = _paint.TextSize;
        foreach (CellLine line in lines)
        {
            float x = PaddingLeft;
            int start = 0;
            while (start < line.Cells.Length)
            {
                uint color = line.Cells[start].Foreground;
                int end = start + 1;
                while (end < line.Cells.Length && line.Cells[end].Foreground == color) end++;
                _paint.Color = new Color((int)color);
                int length = end - start;
                if (_glyphRun.Length < length)
                    Array.Resize(ref _glyphRun, Math.Max(length, _glyphRun.Length * 2));
                for (int i = 0; i < length; i++)
                    _glyphRun[i] = line.Cells[start + i].Rune;
                canvas.DrawText(_glyphRun, 0, length, x, baseline, _paint);
                x += _paint.MeasureText(_glyphRun, 0, length);
                start = end;
            }
            baseline += _cellHeight;
        }
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        int columns = Math.Max(1, (int)((w - PaddingLeft - PaddingRight) / _cellWidth));
        int rows = Math.Max(1, (int)(h / _cellHeight));
        ViewportChanged?.Invoke(columns, rows);
    }

    public void Append(string text)
    {
        _buffer.Write(text);
        PostInvalidate();
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        _scale.OnTouchEvent(e);
        if (_scale.IsInProgress) return true;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _lastTouchY = e.GetY();
                _dragRemainder = 0;
                Parent?.RequestDisallowInterceptTouchEvent(true);
                return true;
            case MotionEventActions.Move:
                float delta = e.GetY() - _lastTouchY;
                _lastTouchY = e.GetY();
                _dragRemainder += delta;
                int rows = (int)(_dragRemainder / _cellHeight);
                if (rows != 0)
                {
                    _dragRemainder -= rows * _cellHeight;
                    int columns = Math.Max(1, (int)(Width / _cellWidth));
                    int viewportRows = Math.Max(1, (int)(Height / _cellHeight));
                    _buffer.ScrollRows(rows, viewportRows, columns);
                    Invalidate();
                }
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                Parent?.RequestDisallowInterceptTouchEvent(false);
                PerformClick();
                return true;
            default:
                return base.OnTouchEvent(e);
        }
    }

    public override bool PerformClick()
    {
        base.PerformClick();
        return true;
    }

    public void SetFontSize(float size)
    {
        _fontSize = Math.Clamp(size, 10f, 32f);
        _paint.TextSize = _fontSize * _scaledDensity;
        _cellWidth = MeasureCellWidth();
        _cellHeight = _paint.TextSize * 1.27f;
        ViewportChanged?.Invoke(
            Math.Max(1, (int)((Width - PaddingLeft - PaddingRight) / _cellWidth)),
            Math.Max(1, (int)(Height / _cellHeight)));
        Invalidate();
    }

    public void SetScrollback(int lines) => _buffer.MaxLines = Math.Clamp(lines, 100, 20000);

    private float MeasureCellWidth() => _paint.MeasureText("0123456789") / 10f;

    public void ApplyColors(string background, string foreground)
    {
        SetBackgroundColor(Color.ParseColor(background));
        _paint.Color = Color.ParseColor(foreground);
        Invalidate();
    }

    private sealed class ScaleListener(NativeConsoleView owner) : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        public override bool OnScale(ScaleGestureDetector detector)
        {
            owner.SetFontSize(owner._fontSize * detector.ScaleFactor);
            return true;
        }
    }
}
