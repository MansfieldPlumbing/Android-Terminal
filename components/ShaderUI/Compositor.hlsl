// ===========================================================================================
// DEVICE-AGNOSTIC HLSL INFINITE CANVAS COMPOSITOR
// Target Profiles: ps_6_0, cs_6_0
// Optimized for direct compilation via DXC (Direct3D12/Vulkan SPIR-V)
// ===========================================================================================

struct UiElementData
{
    float4 Rect;              // x, y, w, h (Normalized coords 0.0 - 1.0)
    float4 Color;             // r, g, b, a (Base fill color)
    float  ZDepth;            // Push-Z: 0.0 (Front) to 1.0 (Back)
    float  Rotation;          // Rotation angle in radians
    float  ElementType;       // 1.0=Wallpaper, 2.0=Card, 3.0=Chrome
    float  IsActive;          // 1.0 = Active, 0.0 = Disabled
    float  ColorId;
    float  TexBlend;
    float2 Padding;
};

cbuffer Constants : register(b0)
{
    float2 Resolution;
    float  Time;
    float  DpiScale;
    float4 ThemeColor;
    float2 Camera;
    float2 UnusedPad;
};

// State Data Interface
StructuredBuffer<UiElementData> UI_Elements : register(t0);

// For Compute Shaders (Uncomment and bind if compiling target is cs_6_0)
// RWTexture2D<float4> OutputTexture : register(u0);

float sdRoundedBox(float2 p, float2 b, float r, float aspect)
{
    float2 pAspect = p;
    float2 bAspect = b;
    pAspect.x *= aspect;
    bAspect.x *= aspect;
    float2 d = abs(pAspect) - bAspect + float2(r, r);
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - r;
}

float2 rotate2d(float2 uv, float angle, float aspect)
{
    float2 p = uv;
    p.x *= aspect;
    float s = sin(angle);
    float c = cos(angle);
    float2 rot = float2(p.x * c - p.y * s, p.x * s + p.y * c);
    rot.x /= aspect;
    return rot;
}

float3 get_background(float2 uv, float aspect)
{
    float2 world_pos = float2(uv.x * aspect, uv.y) + float2(Camera.x * aspect, Camera.y);
    float2 pos = world_pos * 0.5;
    float r = sin(pos.x) * 0.5 + 0.5;
    float g = cos(pos.y + 2.0) * 0.5 + 0.5;
    float b = sin(pos.x - pos.y) * 0.5 + 0.5;
    return float3(r, g, b) * 0.28 + float3(0.08, 0.08, 0.08);
}

// ===========================================================================================
// FRAGMENT SHADER ENTRYPOINT (ps_6_0)
// ===========================================================================================
float4 ps_main(float4 sv_position : SV_Position) : SV_Target
{
    float2 uv = sv_position.xy / Resolution;
    uv.y = 1.0f - uv.y; // Ensure top-down Y parity
    float aspect = Resolution.x / Resolution.y;

    float3 bg_col = get_background(uv, aspect);
    float4 final_pixel = float4(bg_col, 1.0f);

    uint total_elements = 256; // MaxElements
    for (uint idx = 0; idx < total_elements; idx++)
    {
        UiElementData e = UI_Elements[idx];
        if (e.IsActive < 0.5) { continue; }
        if (e.Rect.z <= 0.0 || e.Rect.w <= 0.0) { continue; }

        float2 p_pos = e.Rect.xy;
        float2 p_size = e.Rect.zw;
        float4 p_base_color = e.Color;
        float p_z = e.ZDepth;
        float p_rot = e.Rotation;
        float p_element_type = e.ElementType;

        float zScale = 1.0 + (p_z * 2.0);
        float2 camera_offset = float2(0.0, 0.0);
        if (p_element_type == 2.0) { camera_offset = Camera; }

        float2 world_center = p_pos + (p_size * 0.5);
        float2 vp = float2(0.5, 0.5);
        float2 screen_center = vp + (world_center - vp) / zScale - (camera_offset / zScale);

        float2 pixel_offset = (uv - screen_center) * zScale;
        if (p_rot != 0.0) { pixel_offset = rotate2d(pixel_offset, -p_rot, aspect); }

        float2 half_extents = p_size * 0.5;
        float height = clamp(1.0 - p_z, 0.0, 1.0);

        // Volumetric Drop Shadow calculation
        float2 drop_dir = float2(0.0, 0.005 + (height * 0.02));
        float2 shadow_offset = drop_dir;
        if (p_rot != 0.0) { shadow_offset = rotate2d(shadow_offset, -p_rot, aspect); }
        float shadow_blur_radius = max(0.015, height * 0.05);
        float shadow_dist = sdRoundedBox(pixel_offset - shadow_offset, half_extents, 0.02, aspect);
        if (shadow_dist < shadow_blur_radius)
        {
            float shadow_intensity = 1.0;
            if (shadow_dist > 0.0) { shadow_intensity = 1.0 - smoothstep(0.0, shadow_blur_radius, shadow_dist); }
            final_pixel.rgb *= (1.0 - (shadow_intensity * lerp(0.1, 0.25, height)));
        }

        // Main Box Fill and 3D Beveling
        float dist = sdRoundedBox(pixel_offset, half_extents, 0.015, aspect);
        if (dist < 0.0)
        {
            float4 base_color = p_base_color;
            float2 inner_uv = float2(pixel_offset.x / p_size.x + 0.5, pixel_offset.y / p_size.y + 0.5);

            // Light highlight slope on the vertical edge
            base_color.rgb = lerp(base_color.rgb * 1.12, base_color.rgb * 0.85, inner_uv.y);
            
            // Outer glassy stroke highlight
            float inner_bd = sdRoundedBox(pixel_offset, half_extents - float2(0.003, 0.003), 0.015, aspect);
            if (inner_bd > -0.003) { base_color.rgb = lerp(base_color.rgb, float3(1.0, 1.0, 1.0), 0.12); }

            // Depth attenuation / atmospheric fog
            float3 fogged = lerp(base_color.rgb, bg_col, clamp(-p_z * 0.6, 0.0, 1.0));
            final_pixel.rgb = lerp(final_pixel.rgb, fogged, base_color.a);
        }
    }

    return final_pixel;
}
