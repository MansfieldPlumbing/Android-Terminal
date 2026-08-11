using Android.Graphics;

namespace NativePwshConsole.Surface;

internal static class SurfaceTheme
{
    public static readonly Color Background = Color.ParseColor("#202124");
    public static readonly Color Raised = Color.ParseColor("#25262A");
    public static readonly Color EditorBackground = Color.ParseColor("#161719");
    public static readonly Color Foreground = Color.ParseColor("#F5F5F5");
    public static readonly Color MutedForeground = Color.ParseColor("#858585");
    public static readonly Color Divider = Color.ParseColor("#45464B");

    public const int ChromeHeightDp = 54;
    public const int ChromeHorizontalPaddingDp = 16;
    public const int ChromeCloseWidthDp = 48;
    public const int ContentHorizontalPaddingDp = 16;
    public const int ContentVerticalPaddingDp = 12;
    public const int CompactPaddingDp = 4;
    public const int CompactVerticalPaddingDp = 2;
    public const int ChildGapDp = 4;
    public const int EditorHorizontalPaddingDp = 12;
    public const int EditorVerticalPaddingDp = 10;
    public const int ListRowPaddingDp = 12;

    public const float BodyTextSp = 16;
    public const float EditorTextSp = 15;
    public const float HeroTextSp = 28;
    public const float StatusTextSp = 12;
    public const float CloseTextSp = 28;
}
