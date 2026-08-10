using System;

namespace TuiDwm.Core;

public class CellBuffer
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public Cell[] Cells; // flat array, row-major: index = y * Width + x

    public CellBuffer(int width, int height)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Cells = new Cell[Width * Height];
        Clear(Cell.Empty);
    }

    public ref Cell At(int x, int y)
    {
        return ref Cells[y * Width + x];
    }

    public void Resize(int newWidth, int newHeight)
    {
        newWidth = Math.Max(0, newWidth);
        newHeight = Math.Max(0, newHeight);

        if (newWidth == Width && newHeight == Height)
            return;

        var newCells = new Cell[newWidth * newHeight];
        Array.Fill(newCells, Cell.Empty);

        // Copy overlapping region
        int minW = Math.Min(Width, newWidth);
        int minH = Math.Min(Height, newHeight);

        for (int y = 0; y < minH; y++)
        {
            var srcSpan = Cells.AsSpan(y * Width, minW);
            var dstSpan = newCells.AsSpan(y * newWidth, minW);
            srcSpan.CopyTo(dstSpan);
        }

        Cells = newCells;
        Width = newWidth;
        Height = newHeight;
    }

    public void Clear(Cell fill = default)
    {
        Array.Fill(Cells, fill);
    }

    public void Blit(CellBuffer source, int srcX, int srcY, int srcW, int srcH, int dstX, int dstY)
    {
        // Clip source coordinates to source buffer dimensions
        if (srcX < 0) { srcW += srcX; srcX = 0; }
        if (srcY < 0) { srcH += srcY; srcY = 0; }
        if (srcX + srcW > source.Width) { srcW = source.Width - srcX; }
        if (srcY + srcH > source.Height) { srcH = source.Height - srcY; }

        if (srcW <= 0 || srcH <= 0) return;

        // Clip to destination buffer dimensions
        int targetX = dstX;
        int targetY = dstY;
        int targetW = srcW;
        int targetH = srcH;

        if (targetX < 0) { targetW += targetX; targetX = 0; }
        if (targetY < 0) { targetH += targetY; targetY = 0; }
        if (targetX + targetW > Width) { targetW = Width - targetX; }
        if (targetY + targetH > Height) { targetH = Height - targetY; }

        if (targetW <= 0 || targetH <= 0) return;

        // Account for any target clipping shifting the source coordinates
        int xOffset = targetX - dstX;
        int yOffset = targetY - dstY;

        int finalSrcX = srcX + xOffset;
        int finalSrcY = srcY + yOffset;

        // Perform row-by-row blit using Span.CopyTo
        for (int row = 0; row < targetH; row++)
        {
            int srcIndex = (finalSrcY + row) * source.Width + finalSrcX;
            int dstIndex = (targetY + row) * Width + targetX;

            var srcSpan = source.Cells.AsSpan(srcIndex, targetW);
            var dstSpan = Cells.AsSpan(dstIndex, targetW);
            srcSpan.CopyTo(dstSpan);
        }
    }
}
