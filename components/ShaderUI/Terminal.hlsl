// =============================================================================
//               TERMINAL.HLSL - DIRECTPORT DIRECT3D12 / SPIR-V PIXEL SHADER
// =============================================================================
// Device-agnostic pixel shader implementing the spatial compositor's rendering.
// Supports both Windows D3D12 (CSO) and Cross-Platform SPIR-V (Android Vulkan).
// =============================================================================

// --- Global Constant Buffer ---
cbuffer Constants : register(b0)
{
    float2 iResolution;        // Screen resolution in pixels
    float  iTime;              // Elapsed time in seconds
    float  iPad;               // Bitpacked configuration flags
    float4 iThemeColor;        // OS Accent theme color (RGBA)
    float  iDpiScale;          // High-DPI Scaling Factor
    float2 iCameraOffset;      // Viewport camera X/Y translation offsets
    float  iPadDummy;          // Padding to 16-byte alignment boundary
};

// --- Structured State Buffers ---
struct UiElementData
{
    float2 Position;           // Normalized top-left world coordinate [0.0 - 1.0]
    float2 Size;               // Normalized width and height [0.0 - 1.0]
    float4 Color;              // Default solid color of the quad
    float  ZDepth;             // Depth coordinate: 0.0 (Front) to 1.0 (Back)
    float  Rotation;           // Rotation angle in radians
    float  ElementType;        // 1.0=Button/Icon, 2.0=Window, 3.0=Taskbar, 4.0/5.0=SnapZone, 6.0=Wallpaper, 7.0=Chooser
    float  IsActive;           // 1.0=Active, 0.0=Inactive
    float  ColorId;            // Context-specific color or widget variant index
    float  TexBlend;           // Texture blend ratio
    float2 Padding;            // Padding to align to 16 bytes
};

StructuredBuffer<UiElementData> UI_Elements : register(t0);

// --- Texture & Sampler Bindings ---
Texture2D<float4>   chromeAtlas    : register(t1); // MSDF Chrome Icons (256x256)
Texture2D<float4>   emojiAtlas     : register(t2); // MSDF Emojis (2048x2048)
Texture2D<float4>   fontAtlas      : register(t3); // MSDF Font Glyphs (512x512)
Texture2DArray<float4> appletTexture  : register(t4); // Offscreen absolute applet (IFrame) texture array
SamplerState        mySampler      : register(s0); // Point/Linear wrap sampler
SamplerState        appletSampler  : register(s1); // Clamp-to-edge bilinear sampler

// --- Vertex to Pixel Pipeline Structures ---
struct VertexOutput
{
    float4 Position : SV_Position;
    float2 UV       : TEXCOORD0;
};

// =============================================================================
// 1. MATHEMATICAL SDF & COORDINATE INTRINSICS
// =============================================================================

// Signed Distance Field (SDF) of a rounded rectangle (box).
// Corrects for the screen's aspect ratio to ensure perfect square borders.
float sdRoundedBox(float2 p, float2 b, float r, float aspect)
{
    float2 pAspect = p;
    float2 bAspect = b;
    pAspect.x *= aspect;
    bAspect.x *= aspect;
    
    float2 d = abs(pAspect) - bAspect + float2(r, r);
    return min(max(d.x, d.y), 0.0f) + length(max(d, float2(0.0f, 0.0f))) - r;
}

// Rotates 2D space coordinates around the origin, correcting aspect ratio.
float2 rotate2d(float2 uv, float angle, float aspect)
{
    float2 p = uv;
    p.x *= aspect;
    
    float s = sin(angle);
    float c = cos(angle);
    float2 rot = float2(
        p.x * c - p.y * s,
        p.x * s + p.y * c
    );
    
    rot.x /= aspect;
    return rot;
}

// Extracts the median of three color channels.
// Essential for Multi-Channel Signed Distance Field (MSDF) edge extraction.
float median(float r, float g, float b)
{
    return max(min(r, g), min(max(r, g), b));
}

// Samples a specific sub-glyph region from the MSDF chrome atlas.
float sample_msdf_chrome(float2 tex_uv, float4 atlas_coords)
{
    float2 uv_min = float2(atlas_coords.x / 256.0f, 1.0f - (atlas_coords.w / 256.0f));
    float2 uv_max = float2(atlas_coords.z / 256.0f, 1.0f - (atlas_coords.y / 256.0f));
    
    float2 uv = lerp(uv_min, uv_max, tex_uv);
    float4 m_sample = chromeAtlas.SampleLevel(mySampler, uv, 0.0f);
    return median(m_sample.r, m_sample.g, m_sample.b) - 0.5f;
}

// =============================================================================
// 2. PROCEDURAL ENVIRONMENT BACKGROUNDS
// =============================================================================

// Simple 2D hashing for procedural noise
float hash(float2 p)
{
    float h = dot(p, float2(127.1f, 311.7f));
    return frac(sin(h) * 43758.5453123f) * 2.0f - 1.0f;
}

// 2D Value Noise
float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0f - 2.0f * f);
    
    return lerp(
        lerp(hash(i + float2(0.0f, 0.0f)), hash(i + float2(1.0f, 0.0f)), u.x),
        lerp(hash(i + float2(0.0f, 1.0f)), hash(i + float2(1.0f, 1.0f)), u.x),
        u.y
    );
}

// Fractal Brownian Motion (FBM) for cloud waves
float fbm(float2 p)
{
    float value = 0.0f;
    float amplitude = 0.5f;
    float2 local_p = p;
    
    for (int i = 0; i < 4; i++)
    {
        value += amplitude * noise(local_p);
        local_p *= 2.0f;
        amplitude *= 0.5f;
    }
    return value;
}

// Procedural Liquid Water Surface / Waves Wallpaper
float3 get_water_surface(float2 uv, float time_in)
{
    float time = time_in * 0.5f + 23.0f;
    float2 p = uv * 6.28318530718f - 250.0f;
    float2 i_vec = p;
    float c = 1.0f;
    float inten = 0.005f;

    for (int n = 0; n < 5; n++)
    {
        float t = time * (1.0f - (3.5f / float(n + 1)));
        i_vec = p + float2(
            cos(t - i_vec.x) + sin(t + i_vec.y),
            sin(t - i_vec.y) + cos(t + i_vec.x)
        );
        float dx = p.x / ((sin(i_vec.x + t) / inten) + 0.0001f);
        float dy = p.y / ((cos(i_vec.y + t) / inten) + 0.0001f);
        c += 1.0f / (length(float2(dx, dy)) + 0.0001f);
    }
    
    c /= 5.0f;
    c = 1.17f - pow(abs(c), 1.4f);
    float3 color = float3(pow(abs(c), 8.0f), pow(abs(c), 8.0f), pow(abs(c), 8.0f));
    return clamp(color + float3(0.0f, 0.35f, 0.5f), 0.0f, 1.0f);
}

// Windows 10/11 Abstract Wave Wallpaper Gradient
float3 get_w10_background(float2 local_uv, float time_in, float2 res)
{
    float aspect = res.x / res.y;
    float2 uv = local_uv * float2(aspect, 1.0f);
    
    float3 color_top = float3(0.02f, 0.05f, 0.1f);
    float3 color_bot = float3(0.1f, 0.25f, 0.45f);
    float3 col = lerp(color_bot, color_top, local_uv.y);
    
    float2 n_uv = uv * 2.0f - float2(time_in * 0.05f, 0.0f);
    float n1 = fbm(n_uv);
    float n2 = fbm(n_uv * 2.0f + float2(time_in * 0.02f, 0.0f));
    
    col += float3(n1 * n2 * 0.15f, n1 * n2 * 0.15f, n1 * n2 * 0.15f);
    return col;
}

// Volumetric Scrolling Clouds
float4 get_foreground_clouds(float2 local_uv, float time_in, float2 res, float object_z)
{
    float aspect = res.x / res.y;
    float2 uv = local_uv * float2(aspect, 1.0f);
    
    float speed_x = time_in * 0.05f;
    float speed_z = time_in * 0.1f;

    float t1 = frac(speed_z);
    float scale1 = lerp(1.0f, 3.0f, t1);
    float alpha1 = sin(t1 * 3.14159f);
    float n1 = fbm((uv * scale1 * 2.0f) - float2(speed_x * scale1, 0.0f));

    float t2 = frac(speed_z + 0.5f);
    float scale2 = lerp(1.0f, 3.0f, t2);
    float alpha2 = sin(t2 * 3.14159f);
    float n2 = fbm((uv * scale2 * 2.0f) - float2(speed_x * scale2, 0.0f));
    
    float noise_val = (n1 * alpha1 + n2 * alpha2) * 0.5f;
    float cloud_alpha = smoothstep(0.15f, 0.6f, noise_val);
    
    float depth_fade = smoothstep(0.4f, 0.9f, object_z);
    float3 cloud_col = float3(0.85f, 0.9f, 0.95f);
    
    return float4(cloud_col, cloud_alpha * depth_fade * 0.85f);
}

// =============================================================================
// 3. MAIN COMPOSITOR EXECUTION LOOP
// =============================================================================

float4 main(VertexOutput input) : SV_Target
{
    float2 uv = input.UV;
    
    // Unpack padding/flags
    uint pad_bits = asuint(iPad);
    float bg_mode_f = float((pad_bits >> 24u) & 0xFFu);
    float drown_mode_f = float(pad_bits & 0xFFu);

    // Render wallpaper background
    float3 w10_col = get_w10_background(uv, iTime, iResolution);
    float3 water_col = get_water_surface(uv, iTime);
    
    float3 bg_col = lerp(water_col, w10_col, clamp(bg_mode_f, 0.0f, 1.0f));
    float4 final_pixel = float4(bg_col, 1.0f);
    float aspect = iResolution.x / iResolution.y;

    float closest_z = 1.0f;

    // Fetch total elements from structured buffer count
    uint total_elements = 0;
    uint dummy_stride = 0;
    UI_Elements.GetDimensions(total_elements, dummy_stride);

    // --- Instanced Painter's Algorithm Loop ---
    for (uint idx = 0; idx < total_elements; idx++)
    {
        UiElementData el = UI_Elements[idx];
        
        if (el.IsActive < 0.5f) { continue; } // skipped / inactive
        if (el.Size.x <= 0.0f) { continue; }  // empty / invalid size

        // Perspective Parallax Depth Skew
        float zScale = 1.0f + (el.ZDepth * 2.0f);
        
        float2 world_center = el.Position + (el.Size * 0.5f);
        float2 vp = float2(0.5f, 0.5f);
        
        // Offset by camera viewport if it is a standard window
        float2 camera_offset = float2(0.0f, 0.0f);
        if (el.ElementType == 2.0f)
        {
            camera_offset = iCameraOffset;
        }
        
        float2 screen_center = vp + (world_center - vp) / zScale - (camera_offset / zScale);
        
        float2 pixel_offset = uv - screen_center;
        pixel_offset *= zScale;
        
        // Handle rotation inside pixel space
        if (el.Rotation != 0.0f)
        {
            pixel_offset = rotate2d(pixel_offset, -el.Rotation, aspect);
        }

        float2 half_extents = el.Size * 0.5f;

        // --- Volumetric Shadow Casting ---
        float height = clamp(1.0f - el.ZDepth, 0.0f, 1.0f);
        float2 drop_dir = float2(0.0f, 0.005f + (height * 0.02f));
        float2 shadow_offset = drop_dir;
        if (el.Rotation != 0.0f) { shadow_offset = rotate2d(shadow_offset, -el.Rotation, aspect); }
        float2 shadow_local = pixel_offset - shadow_offset;
        
        float border_radius = 0.015f;
        float shadow_border_radius = 0.02f;
        if (el.ElementType == 3.0f) // Taskbar is sharp
        {
            border_radius = 0.0f;
            shadow_border_radius = 0.0f;
        }

        float shadow_blur_radius = max(0.015f, height * 0.05f);
        float shadow_dist = sdRoundedBox(shadow_local, half_extents, shadow_border_radius, aspect);
        
        if (shadow_dist < shadow_blur_radius)
        {
            float shadow_intensity = 1.0f;
            if (shadow_dist > 0.0f)
            {
                shadow_intensity = 1.0f - smoothstep(0.0f, shadow_blur_radius, shadow_dist);
            }
            
            float final_shadow = shadow_intensity * lerp(0.1f, 0.25f, height);
            if (final_shadow > 0.0f)
            {
                final_pixel = lerp(final_pixel, float4(0,0,0,1), final_shadow);
            }
        }

        // --- Element Boundary SDF Check ---
        float distance = sdRoundedBox(pixel_offset, half_extents, border_radius, aspect);

        if (distance < 0.0f)
        {
            float4 base_color = el.Color;
            float2 inner_uv = (pixel_offset / el.Size) + 0.5f;

            if (el.ElementType == 2.0f) // Window Reconciler
            {
                // Draw Titlebar
                float titlebar_height_px = 36.0f * iDpiScale;
                float btn_width_px = 46.0f * iDpiScale;
                float icon_size_px = 14.0f * iDpiScale;
                
                float y_from_top_px = inner_uv.y * el.Size.y * iResolution.y;
                bool is_title_bar = y_from_top_px < titlebar_height_px;
                
                if (is_title_bar)
                {
                    base_color = float4(iThemeColor.rgb, base_color.a);
                    float x_from_right_px = (1.0f - inner_uv.x) * el.Size.x * iResolution.x;
                    float slots_center_y = titlebar_height_px * 0.5f;
                    
                    // Slot 1: Close Button (X)
                    float slot1_center_x = btn_width_px * 0.5f;
                    if (x_from_right_px < btn_width_px)
                    {
                        float dx = x_from_right_px - slot1_center_x;
                        float dy = y_from_top_px - slots_center_y;
                        float2 local_uv = float2(-dx, dy) / icon_size_px;
                        
                        float w = 0.15f;
                        float line_dist = min(abs(local_uv.x - local_uv.y), abs(local_uv.x + local_uv.y));
                        float active_area = max(abs(local_uv.x), abs(local_uv.y));
                        float alpha = smoothstep(w + 0.05f, w, line_dist) * step(active_area, 0.45f);
                        
                        base_color = lerp(base_color, float4(1, 0.8f, 0.8f, 1), alpha * 0.8f);
                    }
                    // Slot 2: Maximize Button (Square)
                    else if (x_from_right_px < btn_width_px * 2.0f)
                    {
                        float slot2_center_x = btn_width_px * 1.5f;
                        float dx = x_from_right_px - slot2_center_x;
                        float dy = y_from_top_px - slots_center_y;
                        float2 local_uv = float2(-dx, dy) / icon_size_px;
                        
                        float w = 0.15f;
                        float d = abs(max(abs(local_uv.x), abs(local_uv.y)) - 0.4f);
                        float alpha = smoothstep(w + 0.02f, w, d);
                        
                        base_color = lerp(base_color, float4(1,1,1,1), alpha * 0.8f);
                    }
                }
                else
                {
                    // Sample standard application/iframe texture blit using the array slice index
                    float4 applet_color = appletTexture.SampleLevel(appletSampler, float3(inner_uv, el.ColorId), 0.0f);
                    base_color = lerp(base_color, float4(applet_color.rgb, 1.0f), applet_color.a);
                }

                // Hard Outer Border Outline
                float inner_bd = sdRoundedBox(pixel_offset, half_extents - float2(0.002f, 0.002f), border_radius, aspect);
                if (inner_bd > -0.002f)
                {
                    base_color = lerp(base_color, float4(0.2f, 0.2f, 0.2f, 1.0f), 0.9f);
                }
            }
            else if (el.ElementType == 3.0f) // Taskbar Bottom Bevel
            {
                if (inner_uv.y < 0.05f)
                {
                    base_color = lerp(base_color, float4(1,1,1,1), 0.2f);
                }
            }
            else if (el.ElementType == 1.0f) // Dynamic Tiles / Buttons
            {
                if (el.TexBlend == 2.0f) // Trebuchet / Android Launcher Grid Icon
                {
                    bool is_widget = el.ColorId > 0.0f;
                    if (!is_widget)
                    {
                        // Render clean rounded Android-style shortcut icon
                        float2 icon_center = float2(0.5f, 0.4f);
                        float2 icon_uv = inner_uv - icon_center;
                        float squircle_radius = 0.28f;
                        float d_sq = length(pow(abs(icon_uv), float2(3,3))) - pow(squircle_radius, 3.0f);
                        
                        if (d_sq < 0.0f)
                        {
                            base_color = lerp(el.Color, float4(el.Color.rgb * 0.6f, 1), inner_uv.y);
                            if (icon_uv.y < 0.0f)
                            {
                                base_color = lerp(base_color, float4(1,1,1,1), clamp(-icon_uv.y * 1.5f, 0.0f, 0.2f));
                            }
                        }
                        else
                        {
                            base_color = float4(0,0,0,0); // transparent cell bounds
                            
                            // Draw label placeholder pill below icon
                            float2 label_center = float2(0.5f, 0.85f);
                            float label_dist = sdRoundedBox(inner_uv - label_center, float2(0.25f, 0.05f), 0.05f, aspect);
                            if (label_dist < 0.0f)
                            {
                                base_color = float4(1,1,1,0.8f);
                            }
                        }
                    }
                    else
                    {
                        // Render procedural active widget details (e.g. Clock / Weather)
                        base_color = lerp(el.Color, float4(el.Color.rgb * 0.72f, 1), inner_uv.y);
                        
                        if (el.ColorId == 1.0f) // Analog Clock Widget
                        {
                            float2 center = float2(0.5f, 0.5f);
                            float d_c = length(inner_uv - center);
                            if (d_c < 0.35f)
                            {
                                if (d_c > 0.32f)
                                {
                                    base_color = float4(1,1,1,0.95f);
                                }
                                else
                                {
                                    base_color = lerp(base_color, float4(1,1,1,0.15f), 0.15f);
                                    float sec_ang = iTime * 6.0f;
                                    float2 p_sec = inner_uv - center;
                                    float2 sec_dir = float2(sin(sec_ang), cos(sec_ang));
                                    float d_sec = length(p_sec - sec_dir * clamp(dot(p_sec, sec_dir), 0.0f, 0.26f));
                                    if (d_sec < 0.005f)
                                    {
                                        base_color = float4(1, 0.3f, 0.2f, 1);
                                    }
                                }
                            }
                        }
                    }
                    
                    float inner_bd = sdRoundedBox(pixel_offset, half_extents - float2(0.003f, 0.003f), border_radius, aspect);
                    if (inner_bd > -0.003f)
                    {
                        base_color = lerp(base_color, float4(1,1,1,1), 0.35f);
                    }
                }
            }

            float slot_factor = (el.ElementType == 5.0f && el.Size.x > 0.3f) ? 1.0f : 0.0f;
            float pulse = pow(sin(iTime * 3.0f) * 0.5f + 0.5f, 2.0f) * 0.12f * slot_factor;
            if (pulse > 0.0f)
            {
                base_color += float4(pulse, pulse, pulse, 0.0f);
            }

            // Depth/Aerial Fog
            float3 distance_fog = lerp(base_color.rgb, bg_col, clamp(-el.ZDepth * 0.6f, 0.0f, 1.0f));
            
            float4 final_element_color = float4(distance_fog, base_color.a);
            final_pixel = lerp(final_pixel, final_element_color, final_element_color.a);
            
            if (final_element_color.a > 0.5f)
            {
                closest_z = min(closest_z, el.ZDepth);
            }

            // Liquid Neon Highlight Glow
            float dist2 = sdRoundedBox(pixel_offset, half_extents - float2(0.005f, 0.005f), 0.015f, aspect);
            float glow_mask = saturate(1.0f - abs(dist2 + 0.002f) * 220.0f) * slot_factor;
            float flow = (pixel_offset.x + pixel_offset.y) * 22.0f - iTime * 6.0f;
            float3 neon_color = lerp(float3(0, 0.8f, 1), float3(0.85f, 0.1f, 1), sin(flow * 0.3f) * 0.5f + 0.5f);
            float spark = pow(sin(flow) * 0.5f + 0.5f, 4.0f);
            float3 liquid_light = lerp(neon_color, float3(1,1,1), spark * 0.7f);

            final_pixel = lerp(final_pixel, float4(liquid_light, 1), glow_mask * 0.85f);
        }
    }

    // Overlay volumetric clouds
    float4 clouds = get_foreground_clouds(uv, iTime, iResolution, closest_z);
    final_pixel = lerp(final_pixel, clouds, clouds.a * clamp(drown_mode_f, 0.0f, 1.0f));

    return final_pixel;
}
