using System.Globalization;
using System.Text;

namespace Terminal.Engine;

internal static class UnicodeWidth
{
    public static int Of(Rune rune)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.Format or UnicodeCategory.Control)
            return 0;

        int value = rune.Value;
        return IsWide(value) ? 2 : 1;
    }

    // Derived from the stable East Asian Wide/Fullwidth ranges used by wcwidth.
    private static bool IsWide(int value) =>
        value >= 0x1100 &&
        (value <= 0x115f || value == 0x2329 || value == 0x232a ||
         value is >= 0x2e80 and <= 0xa4cf && value != 0x303f ||
         value is >= 0xac00 and <= 0xd7a3 ||
         value is >= 0xf900 and <= 0xfaff ||
         value is >= 0xfe10 and <= 0xfe19 ||
         value is >= 0xfe30 and <= 0xfe6f ||
         value is >= 0xff00 and <= 0xff60 ||
         value is >= 0xffe0 and <= 0xffe6 ||
         value is >= 0x1f300 and <= 0x1faff ||
         value is >= 0x20000 and <= 0x3fffd);
}
