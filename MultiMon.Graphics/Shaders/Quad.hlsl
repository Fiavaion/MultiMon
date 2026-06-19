// Fullscreen-triangle pass. PSMain draws the Milestone 1 animated test pattern; PSSample (Milestone 2)
// samples a decoded source texture with UV passthrough. The vertex shader is shared.

cbuffer TimeConstants : register(b0)
{
    float gTime;
};

// Per-output UV sub-rect (M7, ADR 0003 D1): the slice of the source this output samples.
// (U0,V0)=top-left, (U1,V1)=bottom-right in 0..1 source space. Default (0,0,1,1) = full frame.
// One pass is shared across outputs; each output writes its own gUvRect before its Draw.
cbuffer UvConstants : register(b1)
{
    float4 gUvRect;
};

Texture2D    gSource  : register(t0);
SamplerState gSampler : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

// Single fullscreen triangle from SV_VertexID — no vertex buffer, no input layout.
VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    o.uv = uv;
    return o;
}

// Recognizably ALIVE pattern: a slow-cycling color gradient, a moving bar grid, and a bright
// vertical sweep line — if any of these freeze, the pipeline has wedged.
float4 PSMain(VSOut i) : SV_Target
{
    float3 col = 0.5 + 0.5 * cos(gTime + i.uv.xyx * 6.28318 + float3(0.0, 2.0, 4.0));
    float bars = step(0.95, frac(i.uv.x * 8.0 - gTime * 0.5));
    float sweep = smoothstep(0.02, 0.0, abs(frac(gTime * 0.25) - i.uv.x));
    col = lerp(col, float3(1.0, 1.0, 1.0), max(bars * 0.35, sweep));
    return float4(col, 1.0);
}

// Milestone 2: sample the decoded source (BGRA). M7: map this output's screen UV into its source
// sub-rect (gUvRect) — full-frame when gUvRect=(0,0,1,1). Alpha is forced opaque because Media
// Foundation's RGB32 output leaves the X byte undefined.
float4 PSSample(VSOut i) : SV_Target
{
    float2 uv = lerp(gUvRect.xy, gUvRect.zw, i.uv);
    return float4(gSource.Sample(gSampler, uv).rgb, 1.0);
}

// Milestone 5: HAP Q (scaled YCoCg-DXT5 / BC3). The BC3 texel carries Co in R, Cg in G, a scale in B,
// and luma Y in A. Convert to RGB on the GPU (math from the old VeldridHapRenderer / the HAP spec's
// ScaledCoCgYToRGBA): subtract 128/255 from Co,Cg; scale = B*(255/8)+1; Co/=scale; Cg/=scale; then
// RGB = (Y+Co-Cg, Y+Cg, Y-Co-Cg).
float4 PSSampleYCoCg(VSOut i) : SV_Target
{
    float2 uv = lerp(gUvRect.xy, gUvRect.zw, i.uv);
    float4 s = gSource.Sample(gSampler, uv);
    float Co = s.r - 0.50196078431373;
    float Cg = s.g - 0.50196078431373;
    float scale = (s.b * (255.0 / 8.0)) + 1.0;
    Co /= scale;
    Cg /= scale;
    float Y = s.a;
    return float4(Y + Co - Cg, Y + Cg, Y - Co - Cg, 1.0);
}
