// Terminal.hlsl — Cell buffer quad renderer
//
// Vertex shader generates one quad (6 verts) per cell instance from SV_InstanceID.
// No vertex buffer. DrawInstanced(6, cols*rows, 0, 0).
//
// Cell texture format (RGBA8):
//   R = Rune low byte
//   G = Rune high byte  (0x00 for ASCII)
//   B = Fg color index  (xterm 0-255)
//   A = Bg color index  (xterm 0-255)

// ── Root constants ────────────────────────────────────────────────────────────
cbuffer FrameConstants : register(b0)
{
    uint  GridCols;       // terminal columns
    uint  GridRows;       // terminal rows
    float Time;           // seconds since start, for animated effects
    float EffectMode;     // 0=plain 1=water 2=bloom 3=crt
    float4 Palette[16];   // first 16 xterm colors as RGBA (rest computed)
};

// ── Textures ──────────────────────────────────────────────────────────────────
Texture2D<float4>   CellTex   : register(t0);  // w=cols h=rows, RGBA8 cell data
Texture2D<float4>   AtlasTex  : register(t1);  // MSDF glyph atlas
SamplerState        PointSamp : register(s0);  // point for cell lookups
SamplerState        LinearSamp: register(s1);  // linear for MSDF

// ── Quad corner table (two triangles, CCW) ────────────────────────────────────
static const float2 QuadCorners[6] = {
    float2(0,0), float2(1,0), float2(1,1),
    float2(0,0), float2(1,1), float2(0,1)
};

// ── VS → PS ──────────────────────────────────────────────────────────────────
struct VSOut
{
    float4 Pos      : SV_Position;
    float2 CellUV   : TEXCOORD0;  // center of cell texel in CellTex
    float2 CornerUV : TEXCOORD1;  // 0..1 within the quad (for atlas lookup)
};

// ── Vertex Shader ─────────────────────────────────────────────────────────────
VSOut VSMain(uint vertId : SV_VertexID, uint instId : SV_InstanceID)
{
    VSOut o;

    uint col    = instId % GridCols;
    uint row    = instId / GridCols;
    float2 corner = QuadCorners[vertId % 6];

    // NDC: top-left = (-1,+1), bottom-right = (+1,-1)
    float cellW = 2.0f / GridCols;
    float cellH = 2.0f / GridRows;
    float2 ndc  = float2(
         -1.0f + (col + corner.x) * cellW,
          1.0f - (row + corner.y) * cellH
    );

    o.Pos      = float4(ndc, 0.0f, 1.0f);
    o.CellUV   = float2((col + 0.5f) / GridCols, (row + 0.5f) / GridRows);
    o.CornerUV = corner;
    return o;
}

// ── xterm 256-color palette ───────────────────────────────────────────────────
float3 XtermColor(uint idx)
{
    if (idx < 16)
        return Palette[idx].rgb;

    if (idx >= 232) {
        float v = (idx - 232) / 23.0f;
        return float3(v, v, v);
    }

    idx -= 16;
    uint b = idx % 6;
    uint g = (idx / 6) % 6;
    uint r = idx / 36;
    return float3(r, g, b) / 5.0f;
}

// ── MSDF evaluation ───────────────────────────────────────────────────────────
// Median of three channels → signed distance → coverage
float Median(float r, float g, float b) { return max(min(r,g), min(max(r,g),b)); }

float MsdfCoverage(float2 atlasUV, float pxRange)
{
    float3 s   = AtlasTex.Sample(LinearSamp, atlasUV).rgb;
    float  sd  = Median(s.r, s.g, s.b);
    float  dx  = ddx(atlasUV.x);
    float  dy  = ddy(atlasUV.y);
    float  scale = pxRange / sqrt(dx*dx + dy*dy) * 0.5f;
    return saturate(sd * scale - (scale * 0.5f - 0.5f));
}

// ── Water / wave displacement (EffectMode == 1) ───────────────────────────────
float2 WaterDisplace(float2 uv, float t)
{
    float wave1 = sin(uv.x * 8.0f + t * 1.3f) * 0.004f;
    float wave2 = cos(uv.y * 6.0f + t * 0.9f) * 0.003f;
    return float2(uv.x + wave1, uv.y + wave2);
}

// ── Pixel Shader ──────────────────────────────────────────────────────────────
float4 PSMain(VSOut i) : SV_Target
{
    // Water displacement on the cell UV sample
    float2 sampleUV = i.CellUV;
    if (EffectMode >= 1.0f)
        sampleUV = WaterDisplace(sampleUV, Time);

    float4 cellData = CellTex.Sample(PointSamp, sampleUV);

    uint runeHi = (uint)(cellData.g * 255.0f + 0.5f);
    uint runeLo = (uint)(cellData.r * 255.0f + 0.5f);
    uint rune   = (runeHi << 8) | runeLo;
    uint fgIdx  = (uint)(cellData.b * 255.0f + 0.5f);
    uint bgIdx  = (uint)(cellData.a * 255.0f + 0.5f);

    float3 fgColor = XtermColor(fgIdx);
    float3 bgColor = (bgIdx == 0) ? float3(0,0,0) : XtermColor(bgIdx);

    // Blank cell — just background color
    if (rune == 0 || rune == 32)
        return float4(bgColor, 1.0f);

    // Atlas UV: glyph atlas is 16x16 glyphs, ASCII mapped to glyph index
    // PUA codepoints (0xE000+) wrap into upper atlas pages
    uint glyphIdx = rune & 0xFF; // TODO: full atlas index table via cbuffer
    float atlasCol = (glyphIdx % 16) / 16.0f;
    float atlasRow = (glyphIdx / 16) / 16.0f;
    float2 glyphUV = float2(atlasCol, atlasRow) + i.CornerUV / 16.0f;

    float coverage = MsdfCoverage(glyphUV, 4.0f);

    // CRT scanline (EffectMode == 3)
    if (EffectMode >= 3.0f) {
        float scanline = 0.85f + 0.15f * sin(i.Pos.y * 3.14159f);
        fgColor *= scanline;
        bgColor *= scanline * 0.9f;
    }

    float3 color = lerp(bgColor, fgColor, coverage);

    // Bloom: brighten cells near max fg intensity
    if (EffectMode >= 2.0f) {
        float brightness = dot(fgColor, float3(0.299f, 0.587f, 0.114f));
        color += fgColor * coverage * brightness * 0.25f;
    }

    return float4(saturate(color), 1.0f);
}
