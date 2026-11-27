sampler2D Input : register(s0);
float2 TintRG : register(c0);
float2 TintBO : register(c1);
float2 Tone : register(c2);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 pixel = tex2D(Input, uv);
    // WPF effects receive premultiplied pixels. Work in straight sRGB, then premultiply again.
    float3 source = pixel.rgb / max(pixel.a, 0.00001);
    float3 tint = float3(TintRG.x, TintRG.y, TintBO.x);
    float3 luma = float3(0.2126, 0.7152, 0.0722);
    float sourceY = dot(source, luma);
    float3 saturated = sourceY + (source - sourceY) * Tone.y;
    float luminosityShift = (dot(tint, luma) - sourceY) * Tone.x;
    float3 tinted = (saturated + luminosityShift) * (1.0 - TintBO.y) + tint * TintBO.y;
    return float4(saturate(tinted) * pixel.a, pixel.a);
}
