// Fullscreen-triangle pass — the MSL twin of MultiMon.Graphics/Shaders/Quad.hlsl. pattern_main draws the
// animated test pattern; sample_main samples a decoded source texture through this output's UV sub-rect;
// sample_ycocg_main adds the HapQ YCoCg→RGB conversion. The vertex shader is shared.
#include <metal_stdlib>
using namespace metal;

// One uniform block per draw (buffer 0): the pattern time and this output's UV sub-rect.
// (U0,V0)=top-left, (U1,V1)=bottom-right in 0..1 source space; (0,0,1,1) = full frame.
// MSL alignment rule: float3/float4 are 16-byte aligned, so padding with a float3 would push uvRect to
// offset 32 (sizeof 48) while the C# mirror (FullscreenQuadPass.Uniforms) is 32 bytes with the rect at 16.
// Pad with scalars only — never a vector type. Layout: time@0, pad@4..12, uvRect@16, sizeof 32.
struct QuadUniforms
{
    float  time;
    float  _pad0;
    float  _pad1;
    float  _pad2;
    float4 uvRect;
};

struct VSOut
{
    float4 pos [[position]];
    float2 uv;
};

// Single fullscreen triangle from the vertex id — no vertex buffer. Metal's NDC and texture-coordinate
// conventions match D3D's here (NDC y up, texture origin top-left), so the mapping is identical.
vertex VSOut quad_vertex(uint id [[vertex_id]])
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = uv;
    return o;
}

// Recognizably ALIVE pattern: a slow-cycling colour gradient, a moving bar grid, and a bright vertical
// sweep line — if any of these freeze, the pipeline has wedged.
fragment float4 pattern_main(VSOut i [[stage_in]], constant QuadUniforms& u [[buffer(0)]])
{
    float3 col = 0.5 + 0.5 * cos(u.time + i.uv.xyx * 6.28318 + float3(0.0, 2.0, 4.0));
    float bars = step(0.95, fract(i.uv.x * 8.0 - u.time * 0.5));
    float sweep = smoothstep(0.02, 0.0, abs(fract(u.time * 0.25) - i.uv.x));
    col = mix(col, float3(1.0, 1.0, 1.0), max(bars * 0.35, sweep));
    return float4(col, 1.0);
}

// Sample the decoded source (BGRA / BCn) through this output's sub-rect. Alpha forced opaque.
fragment float4 sample_main(VSOut i [[stage_in]], constant QuadUniforms& u [[buffer(0)]],
                            texture2d<float> source [[texture(0)]], sampler samp [[sampler(0)]])
{
    float2 uv = mix(u.uvRect.xy, u.uvRect.zw, i.uv);
    return float4(source.sample(samp, uv).rgb, 1.0);
}

// HAP Q (scaled YCoCg-DXT5 / BC3): the texel carries Co in R, Cg in G, a scale in B and luma Y in A.
// Same math as the HLSL: subtract 128/255 from Co,Cg; scale = B*(255/8)+1; Co/=scale; Cg/=scale;
// RGB = (Y+Co-Cg, Y+Cg, Y-Co-Cg).
fragment float4 sample_ycocg_main(VSOut i [[stage_in]], constant QuadUniforms& u [[buffer(0)]],
                                  texture2d<float> source [[texture(0)]], sampler samp [[sampler(0)]])
{
    float2 uv = mix(u.uvRect.xy, u.uvRect.zw, i.uv);
    float4 s = source.sample(samp, uv);
    float Co = s.r - 0.50196078431373;
    float Cg = s.g - 0.50196078431373;
    float scale = (s.b * (255.0 / 8.0)) + 1.0;
    Co /= scale;
    Cg /= scale;
    float Y = s.a;
    return float4(Y + Co - Cg, Y + Cg, Y - Co - Cg, 1.0);
}
