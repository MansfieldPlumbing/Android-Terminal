using System.IO;
using System.Text.Json;

namespace TuiDwm.Engine;

public static class AtlasMetrics
{
    // A 1MB array representing 4 floats per possible 16-bit char (0-65535)
    // Layout: [left, bottom, right, top] for every charCode
    public static float[] ParseToFloat32Array(string jsonFilePath)
    {
        float[] metrics = new float[65536 * 4];

        if (!File.Exists(jsonFilePath))
            return metrics;

        string json = File.ReadAllText(jsonFilePath);
        using JsonDocument doc = JsonDocument.Parse(json);

        // Optional: Get default fallback glyph (usually unicode 32 ' ' or 63 '?')
        // We'll populate fallback first or zero it out
        
        var glyphs = doc.RootElement.GetProperty("glyphs");
        
        foreach (var glyph in glyphs.EnumerateArray())
        {
            if (glyph.TryGetProperty("unicode", out var unicodeElement) && glyph.TryGetProperty("atlasBounds", out var atlasBounds))
            {
                int unicode = unicodeElement.GetInt32();
                
                if (unicode >= 0 && unicode < 65536)
                {
                    float left = atlasBounds.GetProperty("left").GetSingle();
                    float bottom = atlasBounds.GetProperty("bottom").GetSingle();
                    float right = atlasBounds.GetProperty("right").GetSingle();
                    float top = atlasBounds.GetProperty("top").GetSingle();

                    int idx = unicode * 4;
                    metrics[idx + 0] = left;
                    metrics[idx + 1] = bottom;
                    metrics[idx + 2] = right;
                    metrics[idx + 3] = top;
                }
            }
        }

        return metrics;
    }
}
