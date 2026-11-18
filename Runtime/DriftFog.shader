Shader "Drift Fog/Local Volume"
{
    Properties
    {
        _FogTint ("Lit tint", Color) = (0.76, 0.85, 0.90, 1)
        _FogShade ("Shadow tint", Color) = (0.22, 0.34, 0.44, 1)
        _FogNoise ("Repeating volume noise", 3D) = "white" {}
        _FogShape ("Density, frequency, turbulence, edge", Vector) = (0.38, 0.065, 0.62, 0.14)
        _FogMotion ("Wind XYZ, height falloff", Vector) = (0.45, 0, 0.14, 0.72)
        _FogMarch ("Steps, distance, shape, time", Vector) = (24, 100, 0, 0)
        [HideInInspector] _FogFluidDensity ("Fluid concentration", 3D) = "black" {}
        [HideInInspector] _FogFluidVelocity ("Fluid velocity", 3D) = "black" {}
        [HideInInspector] _FogFluidDisplacement ("Transported detail displacement", 3D) = "black" {}
        [HideInInspector] _FogFluidDrift ("Integrated wind in local metres, detail enabled", Vector) = (0, 0, 0, 0)
        [HideInInspector] _FogFluidActive ("Fluid enabled", Float) = 0
        [HideInInspector] _FogSourceCount ("Sources", Float) = 0
        [HideInInspector] _FogLampCount ("Local lamps", Float) = 0
        [HideInInspector] _FogObstacleCount ("Box obstacles", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-10" "RenderType"="Transparent" "DisableBatching"="True" }
        Pass
        {
            Name "Drift Fog"
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Front
            ZTest Always
            ZWrite Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #define _SURFACE_TYPE_TRANSPARENT 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"

            TEXTURE3D(_FogNoise);
            SAMPLER(sampler_FogNoise);
            TEXTURE3D(_FogFluidDensity);
            SAMPLER(sampler_FogFluidDensity);
            TEXTURE3D(_FogFluidVelocity);
            TEXTURE3D(_FogFluidDisplacement);

            CBUFFER_START(UnityPerMaterial)
                float4 _FogTint;
                float4 _FogShade;
                float4 _FogShape;
                float4 _FogMotion;
                float4 _FogMarch;
                float4 _FogFluidDrift;
                float _FogFluidActive;
                float _FogSourceCount;
                float _FogLampCount;
                float _FogObstacleCount;
                float4 _FogSources[12];
                float _FogStrengths[12];
                float4 _FogLamps[2];
                float4 _FogLampColors[2];
                float4 _FogObstacleCenters[2];
                float4 _FogObstacleX[2];
                float4 _FogObstacleY[2];
                float4 _FogObstacleZ[2];
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                // 後面が遠クリップ面より遠い場合も、手前の霧を積分する。
                #if UNITY_REVERSED_Z
                    output.positionCS.z = max(output.positionCS.z, 0);
                #else
                    output.positionCS.z = min(output.positionCS.z, output.positionCS.w);
                #endif
                return output;
            }

            float2 BoxInterval(float3 origin, float3 direction, float3 extents)
            {
                float3 signDir = step(0.0, direction) * 2.0 - 1.0;
                float3 invDir = signDir / max(abs(direction), 1e-8);
                float3 a = (-extents - origin) * invDir;
                float3 b = (extents - origin) * invDir;
                float3 nearT = min(a, b);
                float3 farT = max(a, b);
                return float2(Max3(nearT.x, nearT.y, nearT.z), Min3(farT.x, farT.y, farT.z));
            }

            float2 VolumeInterval(float3 origin, float3 direction)
            {
                float2 interval = BoxInterval(origin, direction, 0.5);
                float anchor = max(0.0, interval.x);
                if (_FogMarch.z <= 0.5 || interval.y <= anchor)
                    return interval;

                // 箱に入った位置を基準にし、遠方の小さな楕円で二次方程式の桁落ちを防ぐ。
                float3 p = origin + direction * anchor;
                float inverseSpeed = rsqrt(dot(direction, direction));
                float3 axis = direction * inverseSpeed;
                float middle = -dot(p, axis);
                float3 closest = p + axis * middle;
                float radius2 = 0.25 - dot(closest, closest);
                if (radius2 <= 0)
                    return float2(1, 0);
                float halfChord = sqrt(radius2);
                return anchor + (middle + float2(-halfChord, halfChord)) * inverseSpeed;
            }

            float RaySampleCount(float segmentLength, float3 directionOS, int budget)
            {
                float3 axisSpeed = abs(directionOS);
                float shapeRate = Max3(axisSpeed.x, axisSpeed.y, axisSpeed.z) * budget;
                float noiseRate = _FogShape.y * lerp(12.0, 32.0, saturate(_FogShape.z));
                float rate = max(shapeRate, max(noiseRate, 2.0 * _FogShape.x));
                if (_FogFluidActive > 0.5)
                {
                    uint width, height, depth;
                    _FogFluidDensity.GetDimensions(width, height, depth);
                    float cellsPerMetre = length(directionOS * float3(width, height, depth));
                    rate = max(rate, cellsPerMetre * (0.5 + budget / 40.0));
                }
                return clamp(segmentLength * rate, min(6, budget), budget);
            }

            float SampleDistance(float segmentStart, float segmentEnd, float stepLength, int index, float jitter)
            {
                float cellStart = min(segmentEnd, segmentStart + index * stepLength);
                return cellStart + jitter * clamp(segmentEnd - cellStart, 0.0, stepLength);
            }

            bool SegmentTouchesSphere(float3 startWS, float3 direction, float segmentLength, float3 center, float radius2)
            {
                float projection = clamp(dot(center - startWS, direction), 0, segmentLength);
                float3 offset = startWS + direction * projection - center;
                return dot(offset, offset) < radius2;
            }

            // 視線から外れた影響源を積分ループの前でまとめて除外する。
            uint IntersectingSources(float3 startWS, float3 direction, float segmentLength, int count)
            {
                uint mask = 0u;
                [loop] for (int i = 0; i < count; i++)
                {
                    float4 source = _FogSources[i];
                    if (SegmentTouchesSphere(startWS, direction, segmentLength, source.xyz, source.w * source.w * 2.25))
                        mask |= (1u << i);
                }
                return mask;
            }

            uint IntersectingLamps(float3 startWS, float3 direction, float segmentLength, int count)
            {
                uint mask = 0u;
                [loop] for (int i = 0; i < count; i++)
                {
                    float4 lamp = _FogLamps[i];
                    if (SegmentTouchesSphere(startWS, direction, segmentLength, lamp.xyz, lamp.w * lamp.w))
                        mask |= (1u << i);
                }
                return mask;
            }

            float3 ObstacleSpace(int index, float3 offset)
            {
                return float3(dot(offset, _FogObstacleX[index].xyz), dot(offset, _FogObstacleY[index].xyz),
                              dot(offset, _FogObstacleZ[index].xyz));
            }

            float3 ObstacleExtents(int index)
            {
                return float3(_FogObstacleX[index].w, _FogObstacleY[index].w, _FogObstacleZ[index].w);
            }

            uint IntersectingObstacles(float3 startWS, float3 direction, float segmentLength, int count)
            {
                uint mask = 0u;
                [loop] for (int i = 0; i < count; i++)
                {
                    float4 center = _FogObstacleCenters[i];
                    float2 hits = BoxInterval(ObstacleSpace(i, startWS - center.xyz), ObstacleSpace(i, direction),
                                              ObstacleExtents(i) + center.w);
                    if (max(hits.x, 0) < min(hits.y, segmentLength))
                        mask |= (1u << i);
                }
                return mask;
            }

            float BoundaryDisplacement(float3 position, uint mask, out float3 displacement)
            {
                float openness = 1;
                float ridge = 0;
                displacement = 0;
                [unroll] for (int i = 0; i < 2; i++)
                {
                    if ((mask & (1u << i)) == 0u)
                        continue;
                    float4 center = _FogObstacleCenters[i];
                    float3 local = ObstacleSpace(i, position - center.xyz);
                    float3 outside = max(abs(local) - ObstacleExtents(i), 0) * sign(local);
                    float distance = length(outside) / center.w;
                    float inner = smoothstep(0.0, 0.40, distance);
                    float outer = 1.0 - smoothstep(0.40, 1.0, distance);
                    openness = min(openness, inner);
                    ridge = max(ridge, inner * outer);
                    // 最近点からの外向きベクトルでノイズを曲げ、角を回り込む流れを作る。
                    displacement += (_FogObstacleX[i].xyz * outside.x + _FogObstacleY[i].xyz * outside.y +
                                     _FogObstacleZ[i].xyz * outside.z) * outer;
                }
                return openness * (1.0 + 0.44 * ridge);
            }

            float RadialDisplacement(float3 position, uint mask, int count, out float3 displacement)
            {
                displacement = 0;
                float clearance = 0;
                float ridge = 0;
                [loop] for (int i = 0; i < count; i++)
                {
                    if ((mask & (1u << i)) == 0u)
                        continue;
                    float4 source = _FogSources[i];
                    float3 delta = position - source.xyz;
                    float r2 = dot(delta, delta) / max(source.w * source.w, 1e-6);
                    float strength = saturate(_FogStrengths[i]);
                    float inner = 1.0 - smoothstep(0.18, 1.0, r2);
                    float outer = 1.0 - smoothstep(0.72, 2.25, r2);
                    clearance = max(clearance, inner * strength);
                    ridge = max(ridge, outer * (1.0 - inner) * strength);
                    displacement += delta * (outer * strength * 0.44);
                }
                return (1.0 - clearance) * (1.0 + 0.65 * ridge);
            }

            float BoundaryEnvelope(float3 local)
            {
                float distance = _FogMarch.z > 0.5 ? 0.5 - length(local) :
                    0.5 - Max3(abs(local.x), abs(local.y), abs(local.z));
                return smoothstep(0, max(0.002, _FogShape.w), distance);
            }

            // 流体を使わない場合の密度。二つの空間尺度で霧の上面と内部の筋を別々に作る。
            float AnalyticConcentration(float3 position, float height)
            {
                float turbulence = _FogShape.z;
                float3 uv = (position - _FogMotion.xyz * _FogMarch.w) * _FogShape.y;
                uv = float3(uv.x * 0.82 + uv.z * 0.23, uv.y * 1.44, uv.z * 1.13 - uv.x * 0.31);
                float broad = SAMPLE_TEXTURE3D_LOD(_FogNoise, sampler_FogNoise, uv, 0).r;
                float3 curls = uv.zxy * float3(1.9, 2.4, 1.7) + float3(0.37, 0.59, 0.21);
                curls += (broad - 0.5) * turbulence * float3(-0.85, 0.51, 0.79);
                curls += _FogMarch.w * turbulence * float3(0.005, -0.0025, 0.0035);
                float filaments = SAMPLE_TEXTURE3D_LOD(_FogNoise, sampler_FogNoise, curls, 0).r;
                float field = broad + (filaments - 0.5) * (0.16 + 0.53 * turbulence);
                float cover = smoothstep(0.30, 0.75, field);
                float top = 0.18 + 0.64 * cover + (filaments - 0.5) * 0.16;
                float vertical = 1.0 - smoothstep(top - 0.17, top + 0.14, height);
                return cover * lerp(1.0, vertical, _FogMotion.w);
            }

            struct FluidDetailSampling
            {
                float3 domainSize;
                float3 transverseStepUV;
                float amplitude;
            };

            // 同じ受動スカラーの等値面で厚い本体と並走する薄い縁を作る。高さで少し傾け、間には低密度の谷を残す。
            float MaterialRibbon(float tracer, float originHeight)
            {
                float section = tracer + 0.12 * (originHeight - 0.37);
                float bank = smoothstep(0.68, 0.91, section);
                float lip = 1.0 - smoothstep(0.024, 0.115, abs(section - 0.42));
                return 0.30 + 1.98 * bank + 1.50 * lip;
            }

            // D は一秒の時定数で平滑化した背景風からの実移動。背景乱流の速度幅内では薄霞を保ち、
            // 物体が霧を押し曲げた場所ではトレーサーの二重縁を出す。場に残る D に従って滑らかに戻る。
            float RibbonActivity(float3 displacement, float3 velocity, float mass)
            {
                float windSpeed = length(_FogMotion.xyz);
                float eddySpeed = (0.40 + 0.26 * windSpeed) * saturate(_FogShape.z);
                float quietTravel = min(0.42, 1.2 * eddySpeed);
                float excessSpeed = max(0.0, length(velocity) - windSpeed - 1.1 * eddySpeed);
                // 折り返し直後は履歴変位が小さいため、背景からの速度超過を短い応答分だけ先取りする。
                float travel = length(displacement) * (1.0 + min(0.65, 0.60 * excessSpeed)) + min(0.12, 0.14 * excessSpeed);
                float active = smoothstep(0.075 + 0.24 * quietTravel, 0.275 + 0.22 * quietTravel, travel);
                return lerp(0.10, 1.0, active) * smoothstep(0.04, 0.20, mass);
            }

            float TransportedDetail(float3 uv, float3 displacement, float originHeight, float mass, float ribbonActivity, FluidDetailSampling detail)
            {
                // Kim et al. (2008) §4 の移流座標の伸縮判定を、風の横断方向の有限差分に簡略化する。
                // 物質座標 Jacobian がこの方向の長さを半分以下、または倍以上にした場合、微細な模様だけを弱める。
                float3 neighbor = uv + detail.transverseStepUV;
                if (any(neighbor < 0.0) || any(neighbor > 1.0))
                    neighbor = saturate(uv - detail.transverseStepUV);
                float3 nearby = SAMPLE_TEXTURE3D_LOD(_FogFluidDisplacement, sampler_FogFluidDensity, neighbor, 0).xyz;
                float3 stepMetres = (neighbor - uv) * detail.domainSize;
                float3 materialStep = stepMetres - (nearby - displacement);
                float stretch2 = dot(materialStep, materialStep) / max(1e-8, dot(stepMetres, stepMetres));
                float bandLimit = smoothstep(0.25, 0.72, stretch2) * (1.0 - smoothstep(1.72, 4.0, stretch2));
                float3 q = uv * detail.domainSize - _FogFluidDrift.xyz - displacement;
                float3 noiseUV = float3(0.92 * q.x + 0.39 * q.z, q.y, 0.92 * q.z - 0.39 * q.x) * float3(0.215, 0.22, 0.32);
                noiseUV.xz += float2(0.36 * originHeight, -1.35 * originHeight * (1.0 - originHeight));
                float noise = SAMPLE_TEXTURE3D_LOD(_FogNoise, sampler_FogNoise, noiseUV, 0).r;
                // 静かな地霧には移流する濃淡を残し、剥離した帯では主流の連続した形を優先する。
                float variation = 2.0 * smoothstep(0.16, 0.76, noise) - 1.0;
                float2 contrastRange = lerp(float2(0.84, 0.37), float2(0.37, 0.20), ribbonActivity);
                float contrast = lerp(contrastRange.x, contrastRange.y, smoothstep(0.56, 0.94, mass)) * smoothstep(0.02, 0.13, mass);
                return 1.0 + detail.amplitude * bandLimit * contrast * variation;
            }

            // 視線と照明は同じ輸送密度・由来層に従う。疎に積分する照明には微細構造の空間平均を用いる。
            float FluidExtinction(float3 local, FluidDetailSampling detail, bool resolveRibbon)
            {
                float3 uv = local + 0.5;
                float mass = SAMPLE_TEXTURE3D_LOD(_FogFluidDensity, sampler_FogFluidDensity, uv, 0).r;
                float body = smoothstep(0.10, 0.80, mass);
                float extinction = mass * (0.13 + 2.47 * body * body);
                if (_FogMarch.x <= 12 || mass <= 0.02)
                    return extinction;

                // w は格子中心で輸送された由来層の高さ。同じ空気と動く厚い下層と薄い上層を
                // 滑らかに濃淡へ変える。途中の谷が折り込まれると内巻きの陰が残る。
                float4 flow = SAMPLE_TEXTURE3D_LOD(_FogFluidVelocity, sampler_FogFluidDensity, uv, 0);
                float originHeight = flow.w;
                float strength = smoothstep(0.02, 0.14, mass) * (_FogMarch.x < 32 ? 0.55 : 1.0);
                [branch] if (_FogMarch.x >= 32 && _FogFluidDrift.w > 0.5 && resolveRibbon)
                {
                    float4 field = SAMPLE_TEXTURE3D_LOD(_FogFluidDisplacement, sampler_FogFluidDensity, uv, 0);
                    float activity = RibbonActivity(field.xyz, flow.xyz, mass);
                    extinction *= lerp(1.0, MaterialRibbon(field.w, originHeight), strength * activity);
                    [branch] if (detail.amplitude > 0)
                        extinction *= TransportedDetail(uv, field.xyz, originHeight, mass, activity, detail);
                    strength *= lerp(1.0, 0.62, activity);
                }
                float lower = 1.0 - smoothstep(0.0, 0.18, abs(originHeight - 0.23));
                float upper = 1.0 - smoothstep(0.0, 0.12, abs(originHeight - 0.55));
                float valley = 1.0 - smoothstep(0.0, 0.10, abs(originHeight - 0.41));
                float layer = 0.80 + 0.67 * lower + 0.80 * upper - 0.28 * valley;
                extinction *= lerp(1.0, layer, strength);
                return extinction;
            }

            float Density(float3 position, float3 local, uint mask, int count, uint obstacleMask, FluidDetailSampling detail)
            {
                float envelope = BoundaryEnvelope(local);
                if (envelope <= 0)
                    return 0;

                if (_FogFluidActive > 0.5)
                    return envelope * FluidExtinction(local, detail, true) * _FogShape.x;

                float3 fieldPosition = position;
                if (obstacleMask != 0u)
                {
                    float3 displacement;
                    envelope *= BoundaryDisplacement(position, obstacleMask, displacement);
                    fieldPosition -= displacement;
                }
                if (mask != 0u)
                {
                    float3 displacement;
                    envelope *= RadialDisplacement(position, mask, count, displacement);
                    fieldPosition -= displacement;
                }

                return envelope * AnalyticConcentration(fieldPosition, saturate(local.y + 0.5)) * _FogShape.x;
            }

            float CoarseDensity(float3 position, float3 local, bool fluidOnly, bool resolveRibbon)
            {
                float envelope = BoundaryEnvelope(local);
                if (envelope <= 0)
                    return 0;

                float mass;
                if (fluidOnly || _FogFluidActive > 0.5)
                    mass = FluidExtinction(local, (FluidDetailSampling)0, resolveRibbon);
                else
                    mass = AnalyticConcentration(position, saturate(local.y + 0.5));
                return mass * envelope * _FogShape.x;
            }

            float ExitDistance(float3 local, float3 directionOS)
            {
                // 積分点は既に体積内。正側の出口だけを求め、視線に使う入口の計算を省く。
                if (_FogMarch.z <= 0.5)
                {
                    float3 side = 2.0 * step(0.0, directionOS) - 1.0;
                    float3 distance = (0.5 - side * local) / max(abs(directionOS), 1e-8);
                    return max(0, Min3(distance.x, distance.y, distance.z));
                }
                float a = dot(directionOS, directionOS);
                float b = dot(local, directionOS);
                float c = 0.25 - dot(local, local);
                return max(0, (sqrt(max(0, b * b + a * c)) - b) / max(1e-16, a));
            }

            float BeamTransmittance(float3 position, float3 local, float3 toLight, float3 lightOS, bool fluidOnly,
                                    float4 quadrature, float weightedOrigin)
            {
                float travel = min(5.0, ExitDistance(local, lightOS));
                float2 distances = travel * quadrature.xy;
                float nearDensity = CoarseDensity(position + distances.x * toLight, local + distances.x * lightOS, fluidOnly, true);
                float farDensity = CoarseDensity(position + distances.y * toLight, local + distances.y * lightOS, fluidOnly, false);
                float meanDensity = weightedOrigin + dot(float2(nearDensity, farDensity), quadrature.zw);
                return exp2(-1.442695 * travel * meanDensity);
            }

            float2 LightTransmittance(float3 position, float3 local, float3 toLight, float3 lightOS, float3 upOS, bool hasSun)
            {
                // 通常は二点 Gauss–Legendre。High の流体は折り目の根元を含む三点積分とし、始点を照明間で共有する。
                // 始点の有効幅を最大 5m / 16 に制限し、残る二点と重みは三次までの密度勾配を厳密に積分する値。
                float4 quadrature = float4(0.211324865, 0.788675135, 0.5, 0.5);
                float weightedOrigin = 0;
                [branch] if (_FogFluidActive > 0.5 && _FogMarch.x >= 32)
                {
                    quadrature = float4(0.274869823, 0.808463510, 0.483391290, 0.454108710);
                    weightedOrigin = CoarseDensity(position, local, true, true) * (1.0 / 16.0);
                }
                float sky = 1.0;
                if (_FogFluidActive > 0.5)
                    sky = BeamTransmittance(position, local, float3(0, 1, 0), upOS, true, quadrature, weightedOrigin);
                if (!hasSun)
                    return float2(0, sky);
                float shadow = 1.0;
                #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    float4 coord = TransformWorldToShadowCoord(position);
                    coord.w = 1.0; // 平行光のアトラス座標は透視除算を必要としない。
                    shadow = lerp(MainLightRealtimeShadow(coord), 1.0, GetMainLightShadowFade(position));
                #endif
                float direct = BeamTransmittance(position, local, toLight, lightOS, false, quadrature, weightedOrigin);
                return float2(direct * lerp(_FogFluidActive > 0.5 ? 0.22 : 0.48, 1.0, shadow), sky);
            }

            // 等方散乱に対する Henyey–Greenstein 位相関数の比。軟らかい前方散乱を残す。
            float PhaseGain(float cosTheta)
            {
                const float g = 0.29;
                float d = 1.0 + g * g - 2.0 * g * clamp(cosTheta, -1.0, 1.0);
                return (1.0 - g * g) * rsqrt(d) / d;
            }

            float3 LocalRadiance(float3 position, float3 local, int count, uint mask)
            {
                float3 radiance = 0;
                [loop] for (int i = 0; i < count; i++)
                {
                    if ((mask & (1u << i)) == 0u)
                        continue;
                    float4 lamp = _FogLamps[i];
                    float3 delta = position - lamp.xyz;
                    float distance2 = dot(delta, delta);
                    if (distance2 >= lamp.w * lamp.w)
                        continue;
                    float window = saturate(1.0 - distance2 / max(lamp.w * lamp.w, 1e-6));
                    float gain = 0.22;
                    if (_FogFluidActive > 0.5)
                    {
                        // 暖色光が周囲の濃い帯に届くまでの減衰。中心の空洞を透過し、壁の奥側は暗くなる。
                        float3 midpoint = position + 0.5 * delta;
                        float3 midpointOS = local + mul((float3x3)GetWorldToObjectMatrix(), 0.5 * delta);
                        gain = 1.1 * exp2(-1.442695 * sqrt(distance2) * CoarseDensity(midpoint, midpointOS, true, true));
                    }
                    // 近接時の発散を抑える有限な光源半径と、範囲端を消す滑らかな減衰。
                    radiance += _FogLampColors[i].rgb * (gain * window * window / (0.55 + distance2));
                }
                return radiance;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = GetNormalizedScreenSpaceUV(input.positionCS);
                float3 direction = normalize(-GetWorldSpaceViewDir(input.positionWS));
                // 視線ごとの近平面を起点にし、透視と平行投影の距離をワールド単位に揃える。
                float3 origin = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
                float3 originOS = TransformWorldToObject(origin);
                float3 directionOS = mul((float3x3)GetWorldToObjectMatrix(), direction);
                float2 interval = VolumeInterval(originOS, directionOS);
                float start = max(0, interval.x);
                if (interval.y <= start || _FogShape.x <= 0)
                    return 0;

                float depth = SampleSceneDepth(uv);
                #if !UNITY_REVERSED_Z
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
                #endif
                float3 opaqueWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                float opaqueT = dot(opaqueWS - origin, direction);
                float end = min(interval.y, min(_FogMarch.y, opaqueT));
                if (end <= start)
                    return 0;

                int budget = (int)clamp(_FogMarch.x, 4, 64);
                int sourceCount = (int)clamp(_FogSourceCount, 0, 12);
                int lampCount = (int)clamp(_FogLampCount, 0, 2);
                float segmentLength = end - start;
                float3 startWS = origin + direction * start;
                uint sourceMask = _FogFluidActive > 0.5 ? 0u : IntersectingSources(startWS, direction, segmentLength, sourceCount);
                uint obstacleMask = _FogFluidActive > 0.5 ? 0u : IntersectingObstacles(startWS, direction, segmentLength, (int)clamp(_FogObstacleCount, 0, 2));
                uint lampMask = IntersectingLamps(startWS, direction, segmentLength, lampCount);
                float sampleCount = RaySampleCount(segmentLength, directionOS, budget);
                int steps = (int)ceil(sampleCount);
                // 刻み幅は連続値にし、最後の端数だけ短く積分する。歩数が変わっても残りの点は飛ばない。
                float stepLength = segmentLength / sampleCount;
                FluidDetailSampling detail = (FluidDetailSampling)0;
                [branch] if (_FogFluidActive > 0.5 && budget >= 32 && _FogFluidDrift.w > 0.5)
                {
                    float4x4 axes = GetObjectToWorldMatrix();
                    detail.domainSize = float3(length(axes._m00_m10_m20), length(axes._m01_m11_m21), length(axes._m02_m12_m22));
                    float2 across = mul((float3x3)GetWorldToObjectMatrix(), _FogMotion.xyz).xz * detail.domainSize.xz;
                    across = dot(across, across) > 1e-8 ? float2(-across.y, across.x) : float2(1.0, 0.0);
                    float3 transverseUV = float3(across.x, 0, across.y) / detail.domainSize;
                    uint width, height, depth;
                    _FogFluidDisplacement.GetDimensions(width, height, depth);
                    detail.transverseStepUV = transverseUV / max(1e-8, length(transverseUV * float3(width, height, depth)));
                    // 背景乱流がゼロでも物体後流は細部を運ぶ。長い視線刻みでは空間平均を使用する。
                    float amplitude = lerp(0.58, 1.0, smoothstep(0.0, 0.84, _FogShape.z));
                    detail.amplitude = amplitude * (1.0 - smoothstep(0.22, 0.80, stepLength));
                }
                // 画素単位の固定オフセットで、時間方向のちらつきを増やさず層境界を分散する。
                float jitter = frac(52.9829189 * frac(dot(floor(input.positionCS.xy), float2(0.06711056, 0.00583715))));
                jitter = 0.2 + jitter * 0.6;
                Light sun = GetMainLight();
                float3 lightOS = mul((float3x3)GetWorldToObjectMatrix(), sun.direction);
                float3 upOS = GetWorldToObjectMatrix()._m01_m11_m21;
                bool fluid = _FogFluidActive > 0.5;
                float3 sunColor = _FogTint.rgb * sun.color * sun.distanceAttenuation * ((fluid ? 0.95 : 0.27) * PhaseGain(dot(direction, sun.direction)));
                float3 ambient = _FogShade.rgb * (fluid ? 0.32 : 0.82);
                float3 skylight = fluid ? 0.48 * _FogTint.rgb + 0.45 * _FogShade.rgb : 0.27 * _FogTint.rgb;
                float3 scattering = 0;
                float transmittance = 1.0;
                bool hasSun = any(sunColor > 0.0);
                int cachedEnd = -1;
                float cachedStartT = 0;
                float cachedInvLength = 0;
                float2 previousLight = 1;
                float2 nextLight = 1;
                [loop] for (int i = 0; i < steps; i++)
                {
                    float cellStart = start + i * stepLength;
                    float cellLength = clamp(end - cellStart, 0.0, stepLength);
                    float distanceAlongRay = cellStart + jitter * cellLength;
                    float3 position = origin + direction * distanceAlongRay;
                    float3 local = originOS + directionOS * distanceAlongRay;
                    float density = Density(position, local, sourceMask, sourceCount, obstacleMask, detail);
                    if (density > 0.0001)
                    {
                        float stepT = exp2(-1.442695 * density * cellLength);
                        float3 incident = ambient;
                        float2 lighting = 1;
                        if (hasSun || fluid)
                        {
                            // 空の区間は影を読まない。濃度がある区間だけ端点を取り、実際の距離で補間する。
                            int groupStart = i - (i & 3);
                            if (i > cachedEnd)
                            {
                                cachedStartT = SampleDistance(start, end, stepLength, groupStart, jitter);
                                previousLight = nextLight;
                                [branch] if (groupStart != cachedEnd)
                                    previousLight = LightTransmittance(origin + direction * cachedStartT,
                                        originOS + directionOS * cachedStartT, sun.direction, lightOS, upOS, hasSun);
                                cachedEnd = groupStart + 4;
                                float endT = SampleDistance(start, end, stepLength, cachedEnd, jitter);
                                nextLight = previousLight;
                                [branch] if (endT > cachedStartT)
                                    nextLight = LightTransmittance(origin + direction * endT,
                                        originOS + directionOS * endT, sun.direction, lightOS, upOS, hasSun);
                                cachedInvLength = rcp(max(1e-6, endT - cachedStartT));
                            }
                            lighting = lerp(previousLight, nextLight, saturate((distanceAlongRay - cachedStartT) * cachedInvLength));
                        }
                        incident += sunColor * lighting.x + skylight * lighting.y;
                        if (lampMask != 0u)
                            incident += LocalRadiance(position, local, lampCount, lampMask) * _FogTint.rgb;
                        scattering += transmittance * (1.0 - stepT) * incident;
                        transmittance *= stepT;
                        if (transmittance < 0.012)
                            break;
                    }
                }
                return half4(scattering, 1.0 - transmittance);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
