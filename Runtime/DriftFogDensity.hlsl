#ifndef DRIFT_FOG_DENSITY_INCLUDED
#define DRIFT_FOG_DENSITY_INCLUDED

// 流入時と解析描画で同じ高さ分布を使う。厚い塊ほど半減高度が高く、上空へ連続的に薄くなる。
float GroundConcentration(float field, float height, float heightFalloff)
{
    float bank = smoothstep(0.12, 0.88, field);
    float relativeHeight = height / lerp(0.25, 0.54, bank);
    float settling = exp2(-relativeHeight * relativeHeight);
    return (0.16 + 0.77 * bank) * lerp(1.0, settling, heightFalloff);
}

#endif
